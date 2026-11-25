"""Prepare a Standard GitHub release draft from an accepted source commit."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
from tempfile import TemporaryDirectory
from time import monotonic, sleep

# Importing the version grammar must preserve a clean release checkout.
sys.dont_write_bytecode = True
from stamp_release_version import VERSION_PATTERN

REPO = "PavelLizunov/VPNRouter"
ROOT = Path(__file__).resolve().parents[2]
PLATFORM_BUILDS = {
    "build-mac.yml": ("-mac.dmg", "-mac.zip"),
    "build-linux.yml": ("-linux-amd64.deb", "-linux-x86_64.AppImage", "-linux.tar.gz"),
    "build-android.yml": ("-android-arm64.apk",),
}
UPDATE = "test-windows-update.yml"
SIGN = "sign-windows.yml"
INTEGRITY = "verify-release-integrity.yml"
DEFAULT_WAIT_MINUTES = 120
TRANSFER_TIMEOUT = 60 * 60
SIGNPATH_SECRETS = {
    "SIGNPATH_API_TOKEN", "SIGNPATH_ORGANIZATION_ID", "SIGNPATH_PROJECT_SLUG",
    "SIGNPATH_SIGNING_POLICY_SLUG", "SIGNPATH_ARTIFACT_CONFIG_SLUG",
}


class PreparationError(Exception):
    pass


def command(*args, stdin=None, timeout=300):
    result = subprocess.run(args, cwd=ROOT, input=stdin, text=True, stdout=subprocess.PIPE,
                            timeout=timeout, check=True)
    return result.stdout.strip()


def gh(*args, timeout=300):
    return command("gh", *args, "--repo", REPO, timeout=timeout)


def api(endpoint, *, payload=None, paginate=False):
    args = ["gh", "api", "--hostname", "github.com", f"repos/{REPO}/{endpoint}"]
    if paginate:
        args.extend(("--paginate", "--slurp"))
    if payload is not None:
        args.extend(("--method", "POST", "-H", "X-GitHub-Api-Version: 2026-03-10", "--input", "-"))
    output = command(*args, stdin=json.dumps(payload) if payload is not None else None)
    try:
        return json.loads(output)
    except json.JSONDecodeError as error:
        raise PreparationError(f"GitHub returned no usable JSON for {endpoint}. Inspect remote state before retrying.") from error


def tag_commit(tag):
    ref = f"refs/tags/{tag}"
    lines = command("git", "ls-remote", "origin", ref, ref + "^{}")
    refs = {name: sha for sha, name in (line.split("\t") for line in lines.splitlines())}
    if not refs:
        return None
    commit = refs.get(ref + "^{}")
    if ref not in refs or not commit:
        raise PreparationError(f"{tag}: an annotated release tag is required.")
    return commit


def find_release(tag):
    pages = api("releases?per_page=100", paginate=True)
    matches = [release for page in pages for release in page if release["tag_name"] == tag]
    if len(matches) > 1:
        raise PreparationError(f"{tag}: more than one release exists.")
    return matches[0] if matches else None


def check_draft(release, tag):
    if not release or release["tag_name"] != tag or not release["draft"] or release["prerelease"] != ("-r" in tag):
        raise PreparationError(f"{tag}: expected a draft in the requested release channel.")
    return release


def check_source(tag, sha, existing_tag, candidate):
    main = api("branches/main")["commit"]["sha"]
    if existing_tag and existing_tag != sha:
        raise PreparationError(f"{tag}: existing tag points to another commit.")
    if candidate:
        if not re.fullmatch(r"v" + VERSION_PATTERN, candidate) or candidate.rpartition("-r")[0] != tag:
            raise PreparationError("Stable preparation requires a candidate of the same base version.")
        release = find_release(candidate)
        candidate_sha = tag_commit(candidate)
        if not release or release["draft"] or not release["prerelease"] or not candidate_sha:
            raise PreparationError("Stable preparation requires the published prerelease candidate.")
        try:
            command("git", "cat-file", "-e", f"{candidate_sha}:.github/scripts/stamp_release_version.py")
        except subprocess.CalledProcessError as error:
            raise PreparationError(
                "The candidate tree lacks tag-version builds. Prepare and verify a new candidate from main first."
            ) from error
        if candidate_sha != sha:
            raise PreparationError("The published candidate tag must point to this checkout.")
    elif not existing_tag and main != sha:
        raise PreparationError("A new candidate must use the current accepted main commit.")
    if sha != main and api(f"compare/{sha}...{main}")["status"] != "ahead":
        raise PreparationError("The release commit must be in accepted main history.")


def signing_configured():
    secrets = {item["name"] for page in api("actions/secrets?per_page=100", paginate=True)
               for item in page["secrets"] if item["name"].startswith("SIGNPATH_")}
    variables = {item["name"] for page in api("actions/variables?per_page=100", paginate=True)
                 for item in page["variables"]}
    subject = "SIGNPATH_EXPECTED_SUBJECT" in variables
    if secrets or subject:
        if not SIGNPATH_SECRETS <= secrets or not subject:
            raise PreparationError("SignPath is partially configured. Complete enrollment before staging.")
        return True
    return False


def create_draft(tag, sha, notes, existing_tag, release):
    if release:
        if not existing_tag:
            raise PreparationError(f"{tag}: an existing draft has no matching remote tag.")
        return check_draft(release, tag)
    if not notes or not notes.is_file():
        raise PreparationError("An existing notes file is required to create a draft.")

    if not existing_tag:
        ref = f"refs/tags/{tag}"
        local = command("git", "for-each-ref", "--format=%(objecttype) %(*objectname)", ref)
        if not local:
            command("git", "tag", "-a", tag, sha, "-m", f"Release {tag}")
        elif local != f"tag {sha}":
            raise PreparationError(f"{tag}: local tag conflicts with the accepted commit.")
        command("git", "-c", "credential.helper=", "-c", "credential.helper=!gh auth git-credential",
                "push", "origin", ref)
    if tag_commit(tag) != sha:
        raise PreparationError(f"{tag}: remote tag does not match the accepted commit.")
    arguments = ["release", "create", tag, "--verify-tag", "--draft", "--latest=false",
                 "--title", tag, "--notes-file", str(notes)]
    if "-r" in tag:
        arguments.append("--prerelease")
    gh(*arguments)
    return check_draft(find_release(tag), tag)


def windows_binaries(tag):
    return (f"VPNRouter-{tag}-win.zip", f"VPNRouter-update-{tag}-win.zip", f"VPNRouter-Setup-{tag}.exe")


def with_sidecars(binaries):
    return {asset for binary in binaries for asset in (binary, binary + ".sha256")}


def asset_records(release):
    assets = {asset["name"]: asset for asset in release["assets"]}
    if len(assets) != len(release["assets"]):
        raise PreparationError("Release contains duplicate asset names.")
    if any(asset["state"] != "uploaded" or asset["size"] <= 0 for asset in assets.values()):
        raise PreparationError("Release contains incomplete asset uploads.")
    return assets


def inventory_identity(assets):
    return {name: (asset["id"], asset["state"], asset["size"], asset["digest"])
            for name, asset in assets.items()}


def dispatch(workflow, tag, inputs):
    response = api(f"actions/workflows/{workflow}/dispatches", payload={"ref": tag, "inputs": inputs})
    run_id = response.get("workflow_run_id") if isinstance(response, dict) else None
    if type(run_id) is not int or run_id <= 0:
        raise PreparationError(f"{workflow}: GitHub did not return a workflow run ID.")
    print(f"{workflow}: https://github.com/{REPO}/actions/runs/{run_id}", flush=True)
    return run_id


def verify_run(run, workflow, tag, sha):
    if (run["path"] != f".github/workflows/{workflow}" or run["event"] != "workflow_dispatch"
            or run["head_sha"] != sha or run["head_branch"] != tag):
        raise PreparationError(f"{workflow}: workflow run does not match the exact tag and source.")


def launch_or_resume(workflow, tag, sha, inputs, *, staged=None, expected=None):
    response = api(f"actions/workflows/{workflow}/runs?branch={tag}&head_sha={sha}"
                   "&event=workflow_dispatch&per_page=1")
    runs = response["workflow_runs"]
    if runs:
        run = runs[0]
        verify_run(run, workflow, tag, sha)
        artifact_build = inputs.get("release_artifacts") == "true"
        if artifact_build and run["display_title"] != f"Windows release packages {tag}":
            return dispatch(workflow, tag, inputs)
        if run["status"] != "completed" or (run["conclusion"] == "success" and staged == expected):
            print(f"{workflow}: reusing https://github.com/{REPO}/actions/runs/{run['id']}", flush=True)
            return run["id"]
    if staged:
        raise PreparationError(f"{workflow}: inspect existing assets and exact-run provenance before retrying.")
    return dispatch(workflow, tag, inputs)


def wait_for_runs(runs, tag, sha, *, deadline):
    pending = dict(runs)
    succeeded = {}
    while pending and monotonic() < deadline:
        for workflow, run_id in list(pending.items()):
            run = api(f"actions/runs/{run_id}")
            verify_run(run, workflow, tag, sha)
            if run["status"] != "completed":
                continue
            if run["conclusion"] != "success":
                raise PreparationError(f"{workflow} {run_id}: {run['conclusion']}. Inspect before retrying.")
            succeeded[workflow] = run
            del pending[workflow]
        if pending:
            sleep(max(0, min(10, deadline - monotonic())))
    if pending:
        details = ", ".join(f"{workflow} ({run_id})" for workflow, run_id in pending.items())
        raise PreparationError(f"Timed out waiting for {details}. Rerun the same command to resume healthy runs.")
    return succeeded


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_windows_files(directory, tag):
    expected = with_sidecars(windows_binaries(tag))
    if {path.name for path in directory.iterdir()} != expected:
        raise PreparationError("Windows workflow artifact does not contain the six expected assets.")
    for name in expected:
        file = directory / name
        if not file.is_file() or file.is_symlink() or file.stat().st_size == 0:
            raise PreparationError(f"{name}: expected a nonempty regular file.")
    digests = {name: digest(directory / name) for name in expected}
    for name in windows_binaries(tag):
        text = (directory / (name + ".sha256")).read_text(encoding="utf-8-sig").strip()
        match = re.fullmatch(r"([0-9a-fA-F]{64})(?:[ \t]+\*?([^\r\n]+))?", text)
        if not match or match[2] not in (None, name) or match[1].lower() != digests[name]:
            raise PreparationError(f"{name}: Windows artifact checksum or sidecar filename differs.")
    return {name: "sha256:" + value for name, value in digests.items()}


def stage_windows(run, tag, sha, release_id):
    with TemporaryDirectory(prefix="vpnrouter-release-") as working:
        artifact = f"windows-release-{tag}-{run['run_attempt']}"
        gh("run", "download", str(run["id"]), "--name", artifact, "--dir", working,
           timeout=TRANSFER_TIMEOUT)
        directory = Path(working)
        digests = verify_windows_files(directory, tag)
        if signing_configured() or tag_commit(tag) != sha:
            raise PreparationError("Signing policy or tag changed while the Windows packages built.")
        assets = asset_records(check_draft(api(f"releases/{release_id}"), tag))
        for name in digests.keys() & assets.keys():
            if digests[name] != assets[name]["digest"]:
                raise PreparationError(f"{name}: existing draft asset differs from the verified Windows artifact.")
        missing = sorted(digests.keys() - assets.keys())
        if missing:
            gh("release", "upload", tag, *(str(directory / name) for name in missing),
               timeout=TRANSFER_TIMEOUT)


def prepare(tag, notes, candidate, wait_minutes=DEFAULT_WAIT_MINUTES):
    if not re.fullmatch(r"v" + VERSION_PATTERN, tag):
        raise PreparationError("Expected vX.Y.Z or vX.Y.Z-rN.")
    if ("-r" in tag) == bool(candidate):
        raise PreparationError("Supply --from-candidate for stable tags only.")
    canonical = {f"https://github.com/{REPO}".lower(), f"git@github.com:{REPO}".lower(),
                 f"ssh://git@github.com/{REPO}".lower()}
    for options in ((), ("--push",)):
        remote = command("git", "remote", "get-url", *options, "origin").removesuffix(".git").lower()
        if remote not in canonical:
            raise PreparationError("origin must point to the canonical Standard repository.")
    if command("git", "status", "--porcelain", "--untracked-files=all"):
        raise PreparationError("Release preparation requires a clean source checkout.")

    sha = command("git", "rev-parse", "HEAD")
    existing_tag = tag_commit(tag)
    check_source(tag, sha, existing_tag, candidate)
    signed = signing_configured()
    release = create_draft(tag, sha, notes, existing_tag, find_release(tag))
    release_id = release["id"]
    assets = asset_records(release)
    groups = {workflow: with_sidecars(f"VPNRouter-{tag}{suffix}" for suffix in suffixes)
              for workflow, suffixes in PLATFORM_BUILDS.items()}
    windows = with_sidecars(windows_binaries(tag))
    inventory = windows.union(*groups.values())
    if assets.keys() - inventory:
        raise PreparationError("The draft contains unexpected assets.")

    version = tag[1:]
    deadline = monotonic() + wait_minutes * 60
    runs = {}
    for workflow, expected in groups.items():
        runs[workflow] = launch_or_resume(
            workflow, tag, sha, {"version": version, "upload_to_release": "true"},
            staged=assets.keys() & expected, expected=expected,
        )
    runs["test.yml"] = launch_or_resume("test.yml", tag, sha, {})
    runs[UPDATE] = launch_or_resume(UPDATE, tag, sha, {"release_artifacts": str(not signed).lower()})
    if signed:
        signed_zip_assets = with_sidecars(windows_binaries(tag)[:2])
        runs[SIGN] = launch_or_resume(SIGN, tag, sha, {"version": version},
                                      staged=assets.keys() & signed_zip_assets, expected=signed_zip_assets)
    if not signed:
        windows_run = wait_for_runs({UPDATE: runs.pop(UPDATE)}, tag, sha, deadline=deadline)[UPDATE]
        stage_windows(windows_run, tag, sha, release_id)
    wait_for_runs(runs, tag, sha, deadline=deadline)
    if signed:
        raise PreparationError("Signed ZIPs staged. The installer requires an approved signing and provenance gate.")

    current = asset_records(check_draft(api(f"releases/{release_id}"), tag))
    if current.keys() != inventory:
        raise PreparationError("The draft still needs canonical assets: " + ", ".join(sorted(inventory - current.keys())))
    if signing_configured() != signed or tag_commit(tag) != sha:
        raise PreparationError("Signing policy or tag changed during draft preparation.")
    integrity = dispatch(INTEGRITY, tag, {"tag": tag, "auto_draft_on_failure": "false"})
    wait_for_runs({INTEGRITY: integrity}, tag, sha, deadline=monotonic() + wait_minutes * 60)
    after = asset_records(check_draft(api(f"releases/{release_id}"), tag))
    if inventory_identity(current) != inventory_identity(after) or tag_commit(tag) != sha:
        raise PreparationError("The tag or draft assets changed during integrity verification.")
    print(f"Prepared draft {tag} at {sha} with {len(current)} verified assets.")
    print(f"https://github.com/{REPO}/releases/tag/{tag}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tag", help="vX.Y.Z-rN candidate or vX.Y.Z stable")
    parser.add_argument("--notes", type=Path, help="release notes file for a new draft")
    parser.add_argument("--from-candidate", help="verified candidate tag for an authorized stable cut")
    parser.add_argument("--wait-minutes", type=int, default=DEFAULT_WAIT_MINUTES,
                        help="workflow wait budget in minutes (default: %(default)s)")
    args = parser.parse_args()
    if args.wait_minutes < 1:
        parser.error("--wait-minutes must be positive")
    notes = args.notes.resolve() if args.notes else None
    try:
        prepare(args.tag, notes, args.from_candidate, args.wait_minutes)
    except (PreparationError, OSError, subprocess.SubprocessError) as error:
        print(f"Preparation stopped: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
