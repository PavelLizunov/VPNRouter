# Windows 2.51.0-r1 test packages

Build unsigned Windows packages from `65bab30af9d52b4362fa649b1beacae5cc0c9ab1`.
WINBRAT preflight passed, but the build stopped because its installed .NET SDKs
do not satisfy the `10.0.404` pin. Use the existing Windows update workflow on
an isolated dispatch branch. Keep the application checkout and SDK pin exact.
Store the three packages and SHA-256 sidecars as CI artifacts.

Verify the run, source SHA, selected SDK and all package hashes. Record live
desktop acceptance separately. This branch does not authorize a merge or release.
