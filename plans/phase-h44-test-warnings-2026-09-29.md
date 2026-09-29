# H-44: clear the compiler and analyzer warnings of the test project

## Why

The exact-head CI log of `main` at `b0b26148` listed 147 warnings in `VPNRouter.Tests` besides `CA1416`: 85 xUnit1051
(calls that should pass the test cancellation token), 18 nullable dereferences, 10 xUnit1031 (blocking task
operations), 9 never-raised stub events, 9 xUnit2013, 6 xUnit2031, 4 obsolete API uses and a few single ones. With
that noise, a new warning in the tests could not be seen. H-41 did the same for the shipped projects.

## What

- xUnit1051: the token `TestContext.Current.CancellationToken` is passed to the awaited or blocking call, or
  replaces a trailing `default`. Only the argument changed; no call was reordered.
- xUnit2013 / xUnit2031 / xUnit2002: `Assert.Equal(1, x.Count)` became `Assert.Single(x)`;
  `Assert.Single(x.Where(p))` became `Assert.Single(x, p)`; a redundant `Assert.NotNull` on a `JsonElement` became a
  discarded `GetProperty` call (which still throws when the property is missing).
- Nullable warnings: `?[` on JSON nodes that a `JsonArray` may hold as null, `!` where a test already asserted the
  value, `null!` where a test assigns null on purpose.
- CS0067: the stub events that are never raised sit between `#pragma warning disable CS0067` and `restore`.
- CS0618: the tests that construct `VpnEngine` directly on purpose are wrapped in the same pragma that
  `VpnEngineSplitTunnelResolveTests` and `VpnEngineOrchestratorTests` already use; `ScreenshotHelper` saves with
  `PngBitmapEncoderOptions.Default` instead of the obsolete overload.
- xUnit1031: ten statements that block on a task on purpose (bounded waits in process and lifecycle tests) are
  wrapped in a scoped pragma with the reason next to it, instead of rewriting the tests as async.

## Verification

The exact-head CI log of this PR must show none of the warnings above; full suite in CI (Windows and Linux).

## Outcome

Merged in #378 after green exact-head CI.
