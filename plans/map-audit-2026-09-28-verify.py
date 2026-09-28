#!/usr/bin/env python3
"""Read-only verifier for plans/map-audit-2026-09-28.md (MAP-00).

Recomputes the accounting figures of the 2026-09-28 MAP_AUDIT from pinned Git
objects of the VPNRouter repository and compares them with fixed expectations.

Read-only contract:
- reads commits, trees and blobs through `git rev-parse`, `git cat-file`,
  `git ls-tree`, `git grep` and `git merge-base --is-ancestor` only;
- never fetches (lazy fetch is disabled for partial clones), checks out,
  writes files, or runs the historical map tooling
  (`plans/agent-map/supervisor.py`, `plans/agent-map/generate_manifest.py`);
- the companion repository is an external reference and is not read here.

Exit status: 0 when every expectation matches, 1 on any mismatch, 2 when a
pinned object is missing or Git fails. Missing objects must be fetched
manually, for example `git fetch --no-tags origin <sha>`.

Usage: python3 plans/map-audit-2026-09-28-verify.py [--repo PATH]
"""

import argparse
import collections
import hashlib
import json
import os
import re
import subprocess
import sys

TARGET = "6491be4c47c4ae0d6b97a94ec8758484991ab54b"
MAP_PUBLICATION = "04bdff27524906c757607046090060bec0092a16"
MAP_BASELINE = "5be8952a181f6bc66b41fd26f60dd93e6cc74951"

MAP_DIR = "plans/agent-map"

# Binary rule copied from the historical generator so reverse coverage uses
# the same definition of "text file" as the inventory it audits.
BINARY_EXTENSIONS = {
    ".png", ".ico", ".icns", ".ttf", ".woff", ".woff2", ".eot",
    ".zip", ".tar", ".gz", ".aar", ".so", ".dylib", ".dll", ".exe",
    ".bin", ".dat", ".pdf", ".mp3", ".wav", ".ogg",
}

# Tracked baseline text files the generator skipped (.git* prefix and .dsh/build
# directory exclusions), split by top-level directory.
BIN_MISSING = 131
TEXT_MISSING = 40
TEXT_MISSING_BY_TOP = {".dsh": 18, ".github": 16, ".githooks": 5, "packaging": 1}

EXPECTED_CHANGED_ON_TARGET = [
    "VPNRouter.Core/Models/AppConfig.cs",
    "VPNRouter.Core/Services/ConfigGenerator.Dns.cs",
    "VPNRouter.Core/Services/FreeConfigs/FreeConfigDeepVerifier.cs",
    "VPNRouter.Core/Services/FreeConfigs/FreeConfigFetcher.cs",
    "VPNRouter.Core/Services/HealthCheck.cs",
    "VPNRouter.Core/Services/ProcessOwnership.cs",
    "VPNRouter.Core/Services/SingBoxManager.Lifecycle.cs",
    "VPNRouter.Core/Services/SingBoxManager.LinuxStop.cs",
    "VPNRouter.Core/Services/SingBoxManager.cs",
    "VPNRouter.Core/Services/StartupPipeline.cs",
    "VPNRouter.Core/Services/TunOwnershipLock.cs",
    "VPNRouter.Core/Services/VpnEngine.cs",
    "VPNRouter.Service/VPNRouter.Service.csproj",
]

# Open entries of the PR #296 ledger and the source files each one cites.
# The verifier re-derives these citations from the branch ledger text.
IMPORTED_IDS_MAIN_APPLICABLE = {
    "FAILOVER-WARMUP-RACE": ["VPNRouter.Core/Services/StartupPipeline.cs",
                             "VPNRouter.Core/Services/VpnEngine.cs"],
    "WIN-DNS-RESTORE-ORPHAN": ["VPNRouter.Core/Services/VpnEngine.cs",
                               "VPNRouter.Core/Services/WindowsDnsHardening.cs"],
    "WIN-DNS-LOCKDOWN-TOCTOU": ["VPNRouter.Core/Services/WindowsDnsHardening.cs"],
    "LINUX-NFT-TAILSCALE-LOCKOUT": ["VPNRouter.Core/Platform/Linux/LinuxFirewallManager.cs"],
    "WIN-DNS-NETSH-TIMEOUT-DEADLINE": ["VPNRouter.Core/Services/FirewallManager.cs"],
    "WIN-BINDIR-ACL-FAIL-OPEN": ["VPNRouter.Core/AppPaths.cs"],
}
# Entries already checked as resolved (source-only) in the PR #296 ledger.
IMPORTED_IDS_OUT_OF_TARGET = [
    "HEADLESS-GATE-DISPOSE-RACE",
    "HEADLESS-SERILOG-RAW-EXCEPTION",
    "HEADLESS-CUSTOM-CONFIG-TRANSACTION",
    "OMARCHY-QML-CLEAN-EXIT-STALL",
    "OMARCHY-QML-CONFLICT-CASCADE",
]


class GitError(Exception):
    pass


class Git:
    def __init__(self, repo):
        self.repo = repo
        self.env = dict(os.environ, GIT_OPTIONAL_LOCKS="0", GIT_TERMINAL_PROMPT="0",
                        GIT_NO_LAZY_FETCH="1")

    def run(self, args, stdin=None, ok_codes=(0,)):
        proc = subprocess.run(
            ["git", "-C", self.repo, "--no-pager"] + args,
            input=stdin, capture_output=True, env=self.env,
        )
        if proc.returncode not in ok_codes:
            raise GitError("git %s failed (%d): %s" % (
                " ".join(args), proc.returncode,
                proc.stderr.decode("utf-8", "replace").strip()))
        return proc.returncode, proc.stdout

    def require_commit(self, sha):
        code, out = self.run(["rev-parse", "--verify", "--quiet", sha + "^{commit}"],
                             ok_codes=(0, 1))
        if code != 0 or out.decode().strip() != sha:
            raise GitError("pinned commit %s is not available locally; "
                           "fetch it manually: git fetch --no-tags origin %s" % (sha, sha))

    def blob(self, sha, path):
        return self.run(["cat-file", "blob", "%s:%s" % (sha, path)])[1]

    def tree(self, sha, prefix=None):
        args = ["ls-tree", "-r", "-z", sha]
        if prefix:
            args += ["--", prefix]
        entries = {}
        for rec in self.run(args)[1].split(b"\0"):
            if not rec:
                continue
            meta, path = rec.split(b"\t", 1)
            mode, kind, obj = meta.decode().split()
            if kind == "blob":
                entries[path.decode("utf-8")] = obj
        return entries

    def blobs(self, object_ids):
        """Return {object_id: bytes} using one `cat-file --batch` call."""
        unique = sorted(set(object_ids))
        if not unique:
            return {}
        out = self.run(["cat-file", "--batch"],
                       stdin="".join(o + "\n" for o in unique).encode())[1]
        result, pos = {}, 0
        for oid in unique:
            nl = out.index(b"\n", pos)
            header = out[pos:nl].split()
            if len(header) != 3 or header[1] != b"blob":
                raise GitError("unexpected cat-file header for %s: %r" % (oid, out[pos:nl]))
            size = int(header[2])
            result[oid] = out[nl + 1:nl + 1 + size]
            pos = nl + 1 + size + 1
        return result

    def grep_files(self, sha, pattern, pathspecs, fixed=True):
        args = ["grep", "--no-color", "-l", "-I"]
        if fixed:
            args.append("-F")
        args += ["-e", pattern, sha, "--"] + pathspecs
        code, out = self.run(args, ok_codes=(0, 1))
        if code == 1:
            return []
        prefix = sha + ":"
        return sorted(line[len(prefix):] if line.startswith(prefix) else line
                      for line in out.decode("utf-8").splitlines())


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def crlf_variant(data):
    return data.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")


def is_binary(path, data):
    return os.path.splitext(path)[1].lower() in BINARY_EXTENSIONS or b"\0" in data[:1024]


class Checker:
    def __init__(self):
        self.failures = 0
        self.passes = 0

    def eq(self, name, actual, expected):
        if actual == expected:
            self.passes += 1
            print("OK   %s = %s" % (name, _fmt(actual)))
        else:
            self.failures += 1
            print("FAIL %s: expected %s, got %s" % (name, _fmt(expected), _fmt(actual)))


def _fmt(value):
    if isinstance(value, (list, dict)):
        return json.dumps(value, sort_keys=True)
    return str(value)


def check_receipts(git, c):
    print("== A. receipts at map publication %s" % MAP_PUBLICATION)
    events = [json.loads(line) for line in
              git.blob(MAP_PUBLICATION, MAP_DIR + "/events.jsonl").decode("utf-8").splitlines()
              if line.strip()]
    types = collections.Counter(e["type"] for e in events)
    accepted_events = [e for e in events if e["type"] == "BATCH_ACCEPTED"]
    per_id = collections.Counter(e["data"]["batch_id"] for e in accepted_events)
    artifacts_by_id = collections.defaultdict(set)
    for e in accepted_events:
        artifacts_by_id[e["data"]["batch_id"]].add(tuple(e["data"]["artifacts"] or []))

    c.eq("journal.lines", len(events), 73)
    c.eq("journal.event_types", dict(types), {"BATCH_ACCEPTED": 72, "SUPERVISOR_INITIALIZED": 1})
    c.eq("journal.acceptance_events", len(accepted_events), 72)
    c.eq("journal.unique_accepted_batches", len(per_id), 32)
    c.eq("journal.duplicate_acceptance_events", len(accepted_events) - len(per_id), 40)
    c.eq("journal.repeat_histogram", dict(collections.Counter(per_id.values())), {1: 8, 2: 8, 3: 16})
    c.eq("journal.ids_with_conflicting_artifacts",
         sorted(k for k, v in artifacts_by_id.items() if len(v) > 1), [])

    cp = json.loads(git.blob(MAP_PUBLICATION, MAP_DIR + "/checkpoint.json"))
    records = cp["batches"]
    status = collections.Counter(b["status"] for b in records.values())
    accepted = {k for k, b in records.items() if b["status"] == "accepted"}
    c.eq("checkpoint.header_stats", cp["stats"], {
        "total_batches": 172, "pending_batches": 172, "running_batches": 0,
        "accepted_batches": 72, "failed_batches": 0})
    c.eq("checkpoint.revision", cp["revision"], 73)
    c.eq("checkpoint.records", len(records), 172)
    c.eq("checkpoint.record_status", dict(status), {"accepted": 32, "pending": 140})
    c.eq("checkpoint.records_with_nonzero_attempt",
         sum(1 for b in records.values() if b["attempt"] != 0), 0)
    c.eq("checkpoint.accepted_equals_journal_ids", accepted == set(per_id), True)
    c.eq("checkpoint.hashes_differ_from_journal",
         sorted(k for k in accepted
                if {tuple(records[k]["artifact_hashes"])} != artifacts_by_id[k]), [])

    module_tree = git.tree(MAP_PUBLICATION, MAP_DIR + "/modules/")
    module_data = git.blobs(module_tree.values())
    module_hash = {sha256(module_data[oid]): path for path, oid in module_tree.items()}
    artifact_hashes = [h for k in accepted for h in records[k]["artifact_hashes"]]
    c.eq("modules.files", len(module_tree), 32)
    c.eq("modules.artifact_hashes", len(artifact_hashes), 32)
    c.eq("modules.artifact_hashes_matching_file",
         sum(1 for h in artifact_hashes if h in module_hash), 32)
    c.eq("modules.unreferenced_files",
         sorted(set(module_tree) - {module_hash[h] for h in artifact_hashes if h in module_hash}), [])
    return accepted


def check_inventory(git, c, accepted):
    print("== B. inventory at map publication %s" % MAP_PUBLICATION)
    manifest = json.loads(git.blob(MAP_PUBLICATION, MAP_DIR + "/inventory/manifest.json"))
    batches = json.loads(git.blob(MAP_PUBLICATION, MAP_DIR + "/inventory/batches.json"))
    files = manifest["files"]
    c.eq("manifest.header", {k: v for k, v in manifest.items() if k != "files"}, {
        "total_files": 1709, "total_lines": 437585, "production_lines": 133354,
        "test_lines": 93900, "modules_count": 51, "batches_count": 172})
    c.eq("manifest.files", len(files), 1709)
    c.eq("manifest.lines", sum(f["line_count"] for f in files), 437585)
    c.eq("manifest.repo_ids", dict(collections.Counter(f["repo_id"] for f in files)),
         {"vpnrouter": 1662, "omarchy-vpnrouter": 47})

    key = lambda f: (f["repo_id"], f["path"])
    batch_files = [f for b in batches.values() for f in b["files"]]
    c.eq("batches.count", len(batches), 172)
    c.eq("batches.file_entries", len(batch_files), 1709)
    c.eq("batches.files_equal_manifest", {key(f) for f in batch_files} == {key(f) for f in files}, True)

    acc_files = [f for k in accepted for f in batches[k]["files"]]
    prod = [f for f in files if f["classification"] == "first_party_production"]
    acc_prod = [f for f in acc_files if f["classification"] == "first_party_production"]
    c.eq("coverage.accepted_batches", len(accepted), 32)
    c.eq("coverage.mapped_files", len(acc_files), 200)
    c.eq("coverage.mapped_lines", sum(f["line_count"] for f in acc_files), 59792)
    c.eq("coverage.production_files", [len(acc_prod), len(prod)], [182, 384])
    c.eq("coverage.production_lines",
         [sum(f["line_count"] for f in acc_prod), sum(f["line_count"] for f in prod)], [56149, 133354])

    by_module = collections.defaultdict(list)
    for bid, b in batches.items():
        by_module[b["module_id"]].append(bid)
    with_receipt = sorted(m for m, ids in by_module.items() if any(i in accepted for i in ids))
    first_only = all(sorted(ids)[0] in accepted and sum(i in accepted for i in ids) == 1
                     for m, ids in by_module.items() if m in with_receipt)
    c.eq("coverage.modules_total", len(by_module), 51)
    c.eq("coverage.modules_with_receipt", len(with_receipt), 32)
    c.eq("coverage.receipt_is_first_batch_of_module_only", first_only, True)
    c.eq("coverage.modules_without_receipt", sorted(set(by_module) - set(with_receipt)), [
        "A07", "C37", "C39", "D01", "D05", "D06", "D08", "D09", "H01", "O01", "O04",
        "O07", "O08", "R01", "R03", "R05", "R06", "T01", "T05"])

    own_outputs = sorted(f["path"] for f in files if f["path"].startswith(MAP_DIR + "/"))
    c.eq("self_inclusion.paths", own_outputs, [
        "plans/agent-map/generate_manifest.py", "plans/agent-map/inventory/batches.json",
        "plans/agent-map/inventory/manifest.json"])
    c.eq("self_inclusion.lines", sum(f["line_count"] for f in files
                                     if f["path"].startswith(MAP_DIR + "/")), 43285)

    mac = [f for f in files if f["path"].startswith("VPNRouter.Core/Platform/macOS/")]
    c.eq("macos.files", len(mac), 5)
    c.eq("macos.modules", sorted({f["module_id"] for f in mac}), ["C39"])
    c.eq("macos.c39_accepted_batches", sum(1 for i in by_module["C39"] if i in accepted), 0)
    return files, batches


def check_baseline(git, c, files):
    print("== C. reverse coverage against map baseline %s" % MAP_BASELINE)
    c.eq("baseline.is_ancestor_of_publication",
         git.run(["merge-base", "--is-ancestor", MAP_BASELINE, MAP_PUBLICATION],
                 ok_codes=(0, 1))[0] == 0, True)
    tree = git.tree(MAP_BASELINE)
    data = git.blobs(tree.values())
    man = {f["path"]: f for f in files if f["repo_id"] == "vpnrouter"}
    both = set(tree) & set(man)
    raw_equal = {p for p in both if sha256(data[tree[p]]) == man[p]["content_sha256"]}
    differ = both - raw_equal
    crlf_equal = {p for p in differ if sha256(crlf_variant(data[tree[p]])) == man[p]["content_sha256"]}
    missing = set(tree) - set(man)
    binary = {p for p in missing if is_binary(p, data[tree[p]])}
    text = missing - binary

    c.eq("baseline.tracked", len(tree), 1807)
    c.eq("baseline.in_manifest", len(both), 1636)
    c.eq("baseline.hash_equal_raw", len(raw_equal), 1604)
    c.eq("baseline.hash_equal_after_crlf", len(crlf_equal), 30)
    c.eq("baseline.crlf_extensions", sorted({os.path.splitext(p)[1] for p in crlf_equal}), [".cmd", ".ps1"])
    c.eq("baseline.content_differs", sorted(differ - crlf_equal),
         ["plans/OPEN-DEFECTS.md", "plans/phase-omarchy-plugin-2026-09-17.md"])
    only_man = set(man) - set(tree)
    c.eq("baseline.manifest_only_inputs", len(only_man), 26)
    c.eq("baseline.manifest_only_by_top", dict(collections.Counter(p.split("/")[0] for p in only_man)),
         {".pytest_cache": 5, "plans": 19, "proxy_sources_2026-09-13.json": 1, "vpn_issue_report.md": 1})
    c.eq("baseline.tracked_not_in_manifest", len(missing), 171)
    c.eq("baseline.tracked_not_in_manifest_split", [len(binary), len(text)], [BIN_MISSING, TEXT_MISSING])
    c.eq("baseline.text_not_in_manifest_by_top",
         dict(collections.Counter(p.split("/")[0] for p in text)), TEXT_MISSING_BY_TOP)


def check_target(git, c, batches, accepted):
    print("== D. mapped files against target %s" % TARGET)
    tree = git.tree(TARGET)
    c.eq("target.tracked", len(tree), 1689)
    acc_files = [f for k in accepted for f in batches[k]["files"]]
    present = [f for f in acc_files if f["repo_id"] == "vpnrouter" and f["path"] in tree]
    data = git.blobs(tree[f["path"]] for f in present)
    status = collections.Counter()
    changed = []
    for f in acc_files:
        if f["repo_id"] != "vpnrouter":
            status["companion"] += 1
        elif f["path"] not in tree:
            status["absent"] += 1
        else:
            blob = data[tree[f["path"]]]
            if f["content_sha256"] in (sha256(blob), sha256(crlf_variant(blob))):
                status["equal"] += 1
            else:
                status["changed"] += 1
                changed.append(f["path"])
    c.eq("target.mapped_file_status", dict(status),
         {"equal": 141, "changed": 13, "absent": 31, "companion": 15})
    c.eq("target.mapped_files_changed", sorted(changed), EXPECTED_CHANGED_ON_TARGET)
    c.eq("target.headless_or_linux_tun_ownership_present",
         sorted(p for p in tree if p.startswith("VPNRouter.Headless/")
                or p.endswith("/LinuxTunOwnership.cs")), [])


def check_claims(git, c):
    print("== E. claim evidence on target %s" % TARGET)
    g = lambda pattern, specs: git.grep_files(TARGET, pattern, specs)
    # Map lock names vs target: _gate/_stateLock/_engineGeneration are wrong
    # identifiers; equivalent mechanisms exist under other names.
    c.eq("claim.vpnengine_lifecycle_gate", g("SemaphoreSlim _lifecycleGate", ["VPNRouter.Core/Services/"]),
         ["VPNRouter.Core/Services/VpnEngine.cs"])
    c.eq("claim.singbox_manager_lifecycle_lock", g("object _lifecycleGate", ["VPNRouter.Core/Services/SingBoxManager*.cs"]),
         ["VPNRouter.Core/Services/SingBoxManager.cs"])
    c.eq("claim.failover_generation_increment", g("_failoverGeneration++", ["VPNRouter.Core/Services/"]),
         ["VPNRouter.Core/Services/VpnEngine.cs"])
    c.eq("claim.config_lock_symbol", g("_configLock", ["*.cs"]), [])
    c.eq("claim.engine_generation_symbol", g("_engineGeneration", ["*.cs"]), [])
    c.eq("claim.state_lock_in_core", g("_stateLock", ["VPNRouter.Core/"]), [])
    c.eq("claim.scutil_in_core", g("scutil", ["VPNRouter.Core/"]), [])
    c.eq("claim.mac_dns_networksetup", g("/usr/sbin/networksetup", ["VPNRouter.Core/Platform/macOS/"]),
         ["VPNRouter.Core/Platform/macOS/MacDnsHardening.cs"])
    # The only "setBlocking(" text is a localized hint string, not a call.
    c.eq("claim.set_blocking_paren_occurrences", g("setBlocking(", ["*.java", "*.cs"]),
         ["VPNRouter.Core/Localization/Strings.Android.cs"])
    # NEW-1 evidence. Core readers: ProfileApplication/ProfileManager propagate
    # the value; StartupPipeline/VpnEngine drive the desktop firewall.
    c.eq("claim.block_on_vpn_fail_core_readers", g(".BlockOnVpnFail", ["VPNRouter.Core/Services/"]), [
        "VPNRouter.Core/Services/ProfileApplication.cs", "VPNRouter.Core/Services/ProfileManager.cs",
        "VPNRouter.Core/Services/StartupPipeline.cs", "VPNRouter.Core/Services/VpnEngine.cs"])
    c.eq("claim.config_generator_reads_block_on_vpn_fail",
         g("BlockOnVpnFail", ["VPNRouter.Core/Services/ConfigGenerator*.cs"]), [])
    c.eq("claim.android_profile_from_storage",
         g("BlockOnVpnFail = AndroidStorage.GetBlockOnVpnFail()", ["VPNRouter.Android/"]),
         ["VPNRouter.Android/AndroidConfigBuilder.cs", "VPNRouter.Android/AndroidConfigShare.cs"])
    android_sources = ["VPNRouter.Android/*.cs", "VPNRouter.Android/*.java"]
    c.eq("claim.android_mentions_desktop_engine", {
        name: g(name, android_sources)
        for name in ("VpnEngine", "StartupPipeline", "FirewallManager", "ConfigPipeline")}, {
        "VpnEngine": [], "StartupPipeline": [], "FirewallManager": [],
        # comment only: "bypassing ConfigPipeline"
        "ConfigPipeline": ["VPNRouter.Android/AndroidConfigBuilder.cs"]})
    c.eq("claim.windows_named_owner_lock", g("Global\\VPNRouter-SingBox-Owner", ["VPNRouter.Core/"]),
         ["VPNRouter.Core/Services/TunOwnershipLock.cs"])
    c.eq("claim.cgroup_or_net_cls", sorted(set(g("cgroup", ["*.cs"]) + g("net_cls", ["*.cs"]))), [])
    c.eq("claim.unix_domain_socket_endpoint", g("UnixDomainSocketEndPoint", ["*.cs"]), [])
    c.eq("claim.named_pipe_users", g("NamedPipeServerStream", ["*.cs"]),
         ["VPNRouter.App/Services/SingleInstance.cs"])
    c.eq("claim.windows_service_hosting", g("AddWindowsService", ["VPNRouter.Service/"]),
         ["VPNRouter.Service/Program.cs"])

    print("== F. ledger and build/test facts")
    target_ledger = git.blob(TARGET, "plans/OPEN-DEFECTS.md").decode("utf-8")
    map_ledger = git.blob(MAP_PUBLICATION, "plans/OPEN-DEFECTS.md").decode("utf-8")
    ids = sorted(list(IMPORTED_IDS_MAIN_APPLICABLE) + IMPORTED_IDS_OUT_OF_TARGET)
    c.eq("ledger.ids_on_map_branch", sorted(i for i in ids if i in map_ledger), ids)
    c.eq("ledger.ids_on_target", sorted(i for i in ids if i in target_ledger), [])
    map_lines = {i: [l for l in map_ledger.splitlines()
                     if l.lstrip().startswith("- [") and (" " + i + ":") in l]
                 for i in ids}
    c.eq("ledger.branch_entry_state", {i: [l.lstrip()[:5] for l in ls] for i, ls in map_lines.items()},
         dict([(i, ["- [ ]"]) for i in IMPORTED_IDS_MAIN_APPLICABLE] +
              [(i, ["- [x]"]) for i in IMPORTED_IDS_OUT_OF_TARGET]))
    base_tree, target_tree = git.tree(MAP_BASELINE), git.tree(TARGET)
    cited = {}
    for ident in IMPORTED_IDS_MAIN_APPLICABLE:
        names = set(re.findall(r"`([A-Za-z0-9_.]+\.cs)(?::[0-9,\-]+)?`", " ".join(map_lines[ident])))
        cited[ident] = sorted(p for name in names for p in base_tree if p.endswith("/" + name))
    c.eq("ledger.cited_files_from_branch_ledger", cited,
         {k: sorted(v) for k, v in IMPORTED_IDS_MAIN_APPLICABLE.items()})
    identical = {ident: all(p in target_tree and base_tree[p] == target_tree[p] for p in paths)
                 for ident, paths in cited.items()}
    c.eq("ledger.cited_files_identical_baseline_to_target", identical, {
        "FAILOVER-WARMUP-RACE": False, "LINUX-NFT-TAILSCALE-LOCKOUT": True,
        "WIN-BINDIR-ACL-FAIL-OPEN": True, "WIN-DNS-LOCKDOWN-TOCTOU": True,
        "WIN-DNS-NETSH-TIMEOUT-DEADLINE": True, "WIN-DNS-RESTORE-ORPHAN": False})

    sln = git.blob(TARGET, "VPNRouter.sln").decode("utf-8")
    android_guid = "{1E1BC019-F81F-4AA2-93D5-4BC65B566C10}"
    c.eq("build.android_solution_cfg",
         [sum(1 for l in sln.splitlines() if android_guid in l and ".ActiveCfg" in l),
          sum(1 for l in sln.splitlines() if android_guid in l and ".Build.0" in l)], [2, 0])
    test_yml = git.blob(TARGET, ".github/workflows/test.yml").decode("utf-8")
    c.eq("ci.linux_filter_excludes_headless",
         'FullyQualifiedName!~Headless&FullyQualifiedName!~PageScreenshot&FullyQualifiedName!~VisualDiff'
         in test_yml, True)
    c.eq("ci.headless_gui_tests_on_target", g("class HeadlessGuiTests", ["VPNRouter.Tests/"]),
         ["VPNRouter.Tests/HeadlessGuiTests.cs"])
    c.eq("ci.android_build_on_pull_request",
         "pull_request" in git.blob(TARGET, ".github/workflows/build-android.yml").decode("utf-8"), False)



def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repo", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    args = parser.parse_args()
    git = Git(os.path.abspath(args.repo))
    c = Checker()
    try:
        for sha in (TARGET, MAP_PUBLICATION, MAP_BASELINE):
            git.require_commit(sha)
        accepted = check_receipts(git, c)
        files, batches = check_inventory(git, c, accepted)
        check_baseline(git, c, files)
        check_target(git, c, batches, accepted)
        check_claims(git, c)
    except GitError as exc:
        print("ERROR %s" % exc)
        return 2
    print("== result: %d passed, %d failed" % (c.passes, c.failures))
    return 1 if c.failures else 0


if __name__ == "__main__":
    sys.exit(main())
