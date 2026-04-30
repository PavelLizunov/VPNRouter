# Phase 1.C drafts — libbox runtime wiring

These two C# files implement libbox's `PlatformInterface` and
`CommandServerHandler` callback interfaces against Mono.Android's
auto-generated bindings (i.e. they assume `Bind="true"` on the AAR).

## Why they live here, not in VPNRouter.Android/

A 2026-04-30 attempt to ship them ran into a Mono GC-bridge abort
during application init:

```
monodroid-gc: asked if a class System.Object is a bridge
              before we inited java.lang.Object
F libc: Fatal signal 6 (SIGABRT), code -1 (SI_QUEUE)
```

The crash reproduces with **just** `<AndroidLibrary Include="Lib\libbox.aar" />`
in the csproj (i.e. without these two files even being compiled in).
Setting `Bind="false"` on the AndroidLibrary item makes the crash
disappear — the build completes and the app launches stable on hardware
(Phase 1.A + 1.B foundation verified).

The hypothesis: libbox.aar exports ~80 generated Java classes via
gomobile's `Seq$Proxy` machinery. When Mono.Android binds them all to
C# via `Bind="true"`, something about that volume or shape of
generated types creates a transitive dependency on `Java.Lang.Object`
during the GC bridge initialisation phase — before that phase can ask
its own questions. We don't yet know which type is the offender.

## Phase 1.D work

Two paths forward:

### Option A — fix Bind="true"

1. Bisect the libbox class set: split the AAR into halves, build with
   each half, see which half triggers the abort. Repeat until the
   minimal offending class is found. Then either:
   - Add `[Register]`-overriding attributes on the offender, or
   - Filter it out via a Metadata.xml transform on the binding
     pipeline.

2. Cross-reference with sing-box-for-android's own `:app` module —
   they consume the same libbox.aar with full Java classpath
   resolution, so whatever gomobile produces *can* be bound; the
   question is which knob in the .NET Android binding generator
   handles it differently.

### Option B — go raw JNI (Bind="false")

Skip the C# binding generator entirely. Translate this draft to
`Java.Interop.JniEnvironment.InstanceMethods.CallVoidMethod(...)`
calls. Verbose but bypasses the binding generator's type-registration
issue.

Reference: <https://learn.microsoft.com/en-us/dotnet/communitytoolkit/maui/markup/extensions/java-interop>

The two files in this directory are the Bind="true" reference impl
and stay here (NOT in the build) until either path lands.

## Other Phase 1.C drafts NOT here yet

- `VpnRouterService.StartTunnel` libbox handoff (calls `Libbox.Setup`
  + `new CommandServer(...)` + `StartOrReloadService(configJson, null)`).
  Currently parked in `VpnRouterService.cs` with bisect comments.
- `MainActivity.OnCreate` VpnService.Prepare consent flow.
  Currently reverted — Phase 1.D will reinstate.
- `VpnEngine.cs` PLATFORM_ANDROID branch.
- `ConfigGenerator.cs` PLATFORM_ANDROID branch.
- Smoke test on device.
