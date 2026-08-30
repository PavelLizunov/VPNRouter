# VPNRouter.Tests

xUnit test suite covering `VPNRouter.Core` business logic and `VPNRouter.App` Avalonia UI (headless mode).

## Quick Verification

```powershell
dotnet build VPNRouter.sln -c Release
dotnet test VPNRouter.Tests/VPNRouter.Tests.csproj -c Release --no-build
```

## Structure & Layout

- Keep one test class per correspondingly named `.cs` file; established suffixes include `Tests`, `Test`, and `Fact`.
- `TestAppBuilder.cs`: Assembly-level Avalonia AppBuilder (`[assembly: AvaloniaTestApplication(...)]`).
- `HeadlessGuiTests.cs` & `PageScreenshotTests.cs`: Headless UI smoke, navigation, and PNG rendering (`screenshots/`).
- `VisualDiffHelper.cs` & `VisualDiffTests.cs`: SkiaSharp pixel regression testing against pinned baselines (`screenshots/baseline/`).
- `Fakes/`: Reusable in-memory stores and process/network/state seams for deterministic tests.

## Critical Patterns & Execution Invariants

### Test Framework Selection
- Use `[Fact]` for one-case pure unit, data, or converter tests and `[Theory]` for data-driven variants.
- Use `[AvaloniaFact]` when instantiating ViewModels, Windows, or Avalonia dispatcher components.

### sing-box Binary Integration
- Integration tests calling `sing-box.exe check` must gracefully return/skip when binary is absent (`if (!File.Exists(path)) return;`).

### Testhost Locking
- Avoid running parallel `dotnet build` while `dotnet test` or `testhost.exe` process is active to prevent DLL lock errors.

### Visual Baseline Diff Strategy
- Visual diff tests run on Windows as pre-ship verification gates. Regenerate and update `screenshots/baseline/` when layout changes are intentional.
