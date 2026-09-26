# Omarchy profile payload review

## Scope

Coordinator differential review of the uncommitted plugin `setup` and
`tests/test-packaging.py` against prior snapshot
`d655365f05f315fd841306c4ccbb0b3db251d24f`. The plugin repository has no delivered
commits yet; there is no historical blame for these new files. Source producer:
`VPNRouter.Headless/VPNRouter.Headless.csproj` publishes two profile catalogs.
Consumer: Core resolves bundled catalogs relative to AppContext.BaseDirectory.

## Confirmed findings and remediation

1. Important / confirmed: setup ignored subdirectories and dropped both published
   profile catalogs. This can change the catalog selected after installation.
   The actual setup regression failed with missing installed profiles/default.json.
   Setup now copies exactly profiles/default.json and profiles/default-linux.json,
   preserving their layout and bytes. Both are mandatory, regular files, <=1 MiB.
2. Important / confirmed by source: the top-level allowlist could pass a FIFO
   named dependency.dll to cp and block indefinitely. Non-regular files now fail
   before copy. New test creates an unconnected FIFO and observes prompt refusal.

The profile directory is opened with O_DIRECTORY|O_NOFOLLOW; leaves are opened
relative to that directory descriptor with O_NOFOLLOW|O_NONBLOCK, checked with
fstat, and read with a limit+1 bound. Missing/symlink/FIFO/oversize inputs abort
before handshake and preserve the previous payload. Staging is trap-cleaned.
This is narrow payload validation, not a sandbox for executable backend code.

## Executed evidence

- Red: one actual setup lifecycle test failed because installed profiles were
  missing (exit 1); it ran only in a TemporaryDirectory plugin copy.
- Green: `bash -n setup && PYTHONDONTWRITEBYTECODE=1 python3 tests/test-packaging.py`
  passed all 38 tests (bash-32, exit 0). The unsafe-profile test includes missing
  file, leaf symlink, directory symlink, FIFO and oversized-file subcases.
- RU/EN build examples now use the real root-relative Headless project path,
  not nonexistent src/VPNRouter.Headless. Shell syntax and README format checked.

## Coverage boundaries

No installation into the host shell or real plugin activation. Tests stub
Omarchy lock/validation commands. Whole-product readiness remains changes_required:
backend/sing-box version pinning, integrity/authenticity and sing-box runtime-path
integration are not completed. Top-level dependencies still use the existing
shell stat/copy path; no claim is made against concurrent malicious mutation by
the owner of the supplied build tree. No downloads, elevated helper or routes
were added. A scoped independent Gemini packaging review was subsequently performed; its
findings and coordinator disposition follow below. Whole-delivery review remains
pending.

Real backend proof (bash-33, exit 0): backend snapshot
`b93d5a952f38dba1d1ab788038b385688e5ff968` was framework-dependent published,
then installed by plugin snapshot `f3b1e1a62835253635e07062436449b98e7ff071`
into a disposable temporary plugin tree. The actual staged executable passed
stdio handshake; both profile catalogs matched publish bytes. Isolated HOME
remained empty. Omarchy commands were stubs: this does not validate host plugin
acceptance, sing-box operation, privilege integration or live networking.

## Independent consumer review and final scoped evidence

Gemini reviewed setup and the saved real-backend checker read-only. Coordinator
confirmed its important finding: both upstream catalogs contain the same nine
names, and profiles.list exposes names rather than process rules. The original
successful check (bash-34, plugin5dbcfaa3) proved transport/list compatibility but
could not prove Linux catalog selection. OMARCHY-CATALOG-PROOF was recorded.

The checker now retains that unchanged-payload scenario and separately adds a
unique Linux-only canary to the disposable installed copy. It never rewrites the
supplied publish tree. Snapshot `1712eb082f72165aecf6169477a09641c0871f3d`,
bash-35 exit 0, passed both scenarios through the real production wrapper:
9 unmodified profiles, then 10 profiles including the Linux-only canary. Both
requests left their data directories empty and completed EOF teardown. This is
catalog selection evidence, not process-routing or live-network acceptance.

The reviewer's low-impact observation that setup diagnostics are suppressed by
the checker is retained as a limitation: failure raises a nonzero process error;
raw backend or supplied-path output is intentionally not copied into test logs.
Operators can use existing setup diagnostics separately in an authorized sandbox.
The reviewer's claim that PID uniqueness and absence of an active backend rule
out backup collisions is not accepted: stale directories and concurrent setup
are separate concerns, not tested by these new checks. Its universal robustness
and no-network claims are limited to reviewed behavior with the trusted tested
backend, not arbitrary supplied executables. No arbitrary build tree is safe to
execute merely because it passed these filesystem checks.
