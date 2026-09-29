# H-18: log a failed bin-directory ACL restriction

## Why

Audit finding WIN-BINDIR-ACL-FAIL-OPEN (2026-09-29). `AppPaths.RestrictWindowsBinDirAcl` swallowed
every exception, so a failed ACL change on the folder that holds `sing-box.exe` left no trace.

## What

- The catch now logs a warning with the exception and the directory.

## Not covered

Refusing to launch a binary from an unrestricted directory is a behavior change on machines where
the ACL cannot be set (for example a non-elevated dev run). It stays open in the ledger as an owner
decision. No unit test: the failure needs a directory whose ACL cannot be changed.

## Verification

Exact-head CI.

## Outcome

Merged as #354 on 2026-09-29; exact-head CI green.
