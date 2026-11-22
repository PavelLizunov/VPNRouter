# VMess TLS name in the quick probe

The Windows quick probe reported a TLS failure for an imported VMess row that carried both the configured TLS name and inherited Reality defaults. The configured TLS name succeeded in an isolated handshake to the same endpoint and the inherited name failed.

Use the TLS name whenever the probe dispatches to TLS and fall back to the dial host if it is absent. Test an imported row with inherited Reality settings. Verify on GitHub Actions. The live reproduction tested the existing client, and the scenario with the patched Windows GUI remains pending.
