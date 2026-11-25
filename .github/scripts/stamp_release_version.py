"""Set the release version in an ephemeral checkout bound to a GitHub tag."""

import os
from pathlib import Path
import re
import subprocess
import sys

VERSION_PATTERN = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-r[1-9][0-9]*)?"
VERSION_FIELD = re.compile(rb'(?m)^( *public static readonly string Version = ")[^"\r\n]+(";\r?$)')
VERSION_SOURCE = Path("VPNRouter.Core/AppVersion.cs")


def stamp(version, ref, sha):
    if not re.fullmatch(VERSION_PATTERN, version) or ref != f"refs/tags/v{version}":
        raise ValueError("Release version must match the exact workflow tag.")

    head = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    subprocess.run(["git", "fetch", "--no-tags", "origin", ref], check=True)
    commit = subprocess.check_output(["git", "rev-parse", "FETCH_HEAD^{commit}"], text=True).strip()
    if head != sha or commit != sha:
        raise ValueError("Requested tag does not point to the exact workflow commit.")

    source = VERSION_SOURCE.read_bytes()
    stamped, count = VERSION_FIELD.subn(
        lambda match: match[1] + version.encode("ascii") + match[2], source
    )
    if count != 1:
        raise ValueError("Expected exactly one AppVersion.Version declaration.")
    VERSION_SOURCE.write_bytes(stamped)


if __name__ == "__main__":
    try:
        stamp(sys.argv[1], os.environ["GITHUB_REF"], os.environ["GITHUB_SHA"])
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        raise SystemExit(str(error)) from None
