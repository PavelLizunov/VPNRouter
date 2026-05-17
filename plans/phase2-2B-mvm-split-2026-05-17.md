# Phase 2 — 2B: `MainWindowViewModel.cs` split (6,753 LOC → 4 new partials)

**Owner**: Wave 8 (sequential, single agent — no parallelism, characterization safety)
**Roadmap ref**: `plans/v3.0-refactor-roadmap.md` Phase 2B; `plans/v3.0-architecture-roadmap.md` §2 "MVM is a god-class"
**Depends on**: Wave 5 (2A Localization dedup landed — many `L_X` wrapper references already gone)
**Effort**: 2 days
**Risk**: HIGH (god-file split — Gate 6 characterization snapshot mandatory)

## Why

`VPNRouter.App/ViewModels/MainWindowViewModel.cs` is the central god-class of the GUI: **6,753 LOC** of view-state, commands, subscription handlers, UI orchestration, and lifecycle code. 6 partial classes already extract concerns that have natural boundaries (RuntimeStatus, ServerTesting, Wgturn, SimpleMode, Localization, AutostartBootstrap — totaling 2,588 LOC), but the main file still hosts everything else.

Splitting further makes:
- Per-concern navigation possible (jump to "Profiles tab" code without scrolling 5,000 lines)
- Per-concern git blame (one feature's history isn't tangled with another's)
- Per-concern testing possible (currently nothing in MVM is directly testable — every test goes through headless WPF/Avalonia)
- Lower merge-conflict surface (Wave 5 ConfigPipeline + Wave 6 abstractions both touched MVM tangentially — would have conflicted if we'd refactored MVM earlier)

## What

**Step 1**: Take a **characterization snapshot** of the current public surface BEFORE any move. Reflection-enumerates every `public` / `internal` member of `MainWindowViewModel`, captures `(Name, Kind, Type, Parameters[])`, sorts deterministically, JSON-serializes, hashes with SHA-256. Pin the hash in a test:

```csharp
[Fact]
public void MainWindowViewModel_PublicSurface_StableHash()
{
    var hash = ComputePublicSurfaceHash(typeof(MainWindowViewModel));
    Assert.Equal("<pin-hash-here>", hash);
}
```

This test goes red the moment the split accidentally renames or removes a member. Zero-tolerance gate.

**Step 2**: Extract 4 new partial classes by concern:

| New partial | Approx LOC | Concern |
|---|---|---|
| `MainWindowViewModel.Profiles.cs` | ~1,800 | Profile load / merge / display / Apply commands |
| `MainWindowViewModel.Subscriptions.cs` | ~1,400 | Subscription card UI, refresh commands, server list binding |
| `MainWindowViewModel.FreeConfigs.cs` | ~1,200 | FreeConfigs tab, cache UI, recheck commands |
| `MainWindowViewModel.Settings.cs` | ~900 | Settings page bindings, save/load, version info |

After extraction the main `MainWindowViewModel.cs` shrinks to the **constructor, the field declarations, the DI wiring, and the cross-concern orchestration** — target ~1,400 LOC.

**Step 3**: Re-run the characterization snapshot test. Hash MUST match. Zero behavior drift allowed.

**Step 4**: MCP verify on the running binary. Open the app, click through each tab (Profiles / Subscriptions / FreeConfigs / Settings), confirm bindings still work and commands still fire.

## How

**Step 1 — Characterization snapshot**:
- Add `VPNRouter.Tests/MainWindowViewModelCharacterizationTests.cs`
- Helper `ComputePublicSurfaceHash(Type t)` lists all public/internal members (excluding `<>` compiler-generated), sorts by `Name`, serializes each as `{Kind, Name, ReturnType, Parameters[]}`, JSON-serializes the array, SHA-256s the JSON bytes.
- Run it ONCE, capture the hash, paste into the Assert.
- Commit this test first. It's the safety net.

**Step 2 — Extract Profiles partial**:
- Identify all members related to Profile management (load, merge, display, Apply). Use `grep -nE '(profile|Profile)' MainWindowViewModel.cs` to find them.
- Move to `MainWindowViewModel.Profiles.cs` keeping `public partial class MainWindowViewModel`.
- Build + run characterization test. Must pass.
- Commit.

**Step 3 — Extract Subscriptions partial** (same process).

**Step 4 — Extract FreeConfigs partial** (same process).

**Step 5 — Extract Settings partial** (same process).

**Step 6 — MCP verify**:
- Launch built app: `dotnet run --project VPNRouter.App/VPNRouter.App.csproj`
- Use `mcp__computer-use__screenshot` after each tab click
- Compare with pre-split screenshots if needed
- PASS/FAIL per tab

**Step 7 — Update `VPNRouter.App/CLAUDE.md`**:
- Refresh the file listing under "ViewModels/" to include the 4 new partials

## Verification gate

- [ ] Characterization snapshot test committed BEFORE any extraction
- [ ] 4 new partials extracted (one commit each — bisect-friendly)
- [ ] Main `MainWindowViewModel.cs` shrinks from 6,753 → ~1,400 LOC
- [ ] Characterization hash matches pre- and post-split
- [ ] **Gate 1**: build 0 errors (after each commit)
- [ ] **Gate 2**: full scoped suite + headless suite stays green
- [ ] **Gate 5 MCP verify**: 4 tabs PASS (screenshot per tab pinned to brief)
- [ ] **Gate 6 characterization diff**: snapshot hash identical pre/post
- [ ] **Gate 4 simplify**: per-partial diff under 100 LOC of restructure (mostly cut + paste)
- [ ] **Hook gates** pass

## Outcome
*(filled by agent)*

## Follow-up

- Phase 3B (Avalonia 11→12) may further reshape MVM via new ViewModel base classes — characterization snapshot serves as a forward safety net for that work too.
- If any partial exceeds ~1,800 LOC after split, consider a Phase 2B-A follow-up to further break it down.
