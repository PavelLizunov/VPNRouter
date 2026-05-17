# Phase 2 — 2E: UnitTest1.cs extraction (313 tests → per-class files)

**Owner**: Wave 5 parallel agent
**Roadmap ref**: plans/v3.0-refactor-roadmap.md Phase 2E; plans/test-coverage-audit-2026-05-17.md §"UnitTest1.cs is bloated"
**Effort**: 2 days
**Risk**: LOW (pure mechanical refactor, no logic change)

## Why
`VPNRouter.Tests/UnitTest1.cs` is **6,169 LOC** containing **313 tests across 42 classes**. Audit E flagged as the largest test extraction opportunity. Splitting into per-class files makes:
- Faster grep/navigation when debugging a specific test class
- Cleaner git blame (per-class history)
- Lower merge-conflict surface for future test additions
- Easier `dotnet test --filter "FullyQualifiedName~<ClassName>"` discovery

Pure cosmetic — no test changes, no logic changes, no count delta.

## What
1. Walk `UnitTest1.cs` and identify every `public class XxxTests` block (or `internal class`).
2. For each, extract to its own file `VPNRouter.Tests/<ClassName>.cs`.
3. Preserve:
   - All `using` directives (copy to new file)
   - All `namespace` decl (typically `namespace VPNRouter.Tests;`)
   - Helper methods + constants inside the class
   - XML doc comments

4. After extraction, `UnitTest1.cs` should be EMPTY (or contain a placeholder comment) and ideally deleted.

## How

**Step 1 — Inventory**:
```bash
cd C:/Project/VPNRouter
grep -nE '^(public|internal) class [A-Za-z0-9_]+Tests' VPNRouter.Tests/UnitTest1.cs > /tmp/test-classes.txt
wc -l /tmp/test-classes.txt
```
Expected: 42 lines.

**Step 2 — Extract one class at a time** (mechanical):
For each class:
1. Find class start (`public class Foo`) and end (matching `}` at column 0)
2. Capture from class start to class end + any leading XML doc comments (above the class) + any leading section divider comments (e.g. `// ─────────────`)
3. Create `VPNRouter.Tests/<ClassName>.cs` with:
   - File-top usings (copy from UnitTest1.cs top)
   - `namespace VPNRouter.Tests;`
   - Extracted class block
4. Remove extracted block from `UnitTest1.cs`
5. After EACH extraction:
   - `dotnet build VPNRouter.Tests/VPNRouter.Tests.csproj -c Release` → 0 errors
   - `dotnet test --filter "FullyQualifiedName~<ClassName>" --no-build` → all pass (move them from UnitTest1 to new file should not change behaviour)

**Step 3 — Cleanup `UnitTest1.cs`**:
After all 42 classes extracted, UnitTest1.cs should contain only file-level comments. Either:
- Delete UnitTest1.cs entirely (preferred — its name is meaningless once it's not unit test #1)
- OR replace with a `// extracted to per-class files 2026-05-17` placeholder

**Step 4 — Verify test count unchanged**:
```bash
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build --logger "console;verbosity=normal" 2>&1 | tail -3
```
Must show same total count (842) and same passing count (839).

**Step 5 — Update `VPNRouter.Tests/CLAUDE.md`** "Test classes" table:
- Section already lists ~25 of 42 classes — add the missing rows
- Or replace the table with a one-line note: "auto-discovered via `Get-ChildItem *.cs` after 2E extraction"

## Verification gate
- [ ] Inventory: 42 classes found in UnitTest1.cs
- [ ] Per-class extraction: 42 new .cs files created
- [ ] **Gate 1**: dotnet build → 0 errors
- [ ] **Gate 2**: dotnet test → same 839 passing (no count change)
- [ ] **Hook gates**: pre-commit + commit-msg both green
- [ ] **Sanity**: UnitTest1.cs deleted OR < 50 LOC placeholder
- [ ] **Docs**: VPNRouter.Tests/CLAUDE.md updated with new test class inventory

## Outcome
*(filled by agent after impl)*
