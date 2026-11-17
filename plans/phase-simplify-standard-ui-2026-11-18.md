# Simplify the Standard server import form

Base: main at e7b4a384. The import form offers a destructive action on a
different object (the selected row). Each server already has a named delete
button. Keep Add under the link field and keep deletion on its target row.
Use the existing nullable row deletion method for the compatibility command.
Preserve config-mode actions, keyboard access, the public ViewModel commands
and persistence. Rollback is a revert of the task change.

Verification gates: Release build and test suite in exact-head CI; exercise
row deletion and import bindings; update this outcome; review the form and
its consumers; inspect isolated list and narrow screenshots; verify the
public command surface and custom-config path. Live install applies at release.

## Implementation

The server form now has one import action, while the row passes its own server
to deletion. The headless regression deletes a different row from the selection
and checks the stored server and selection. Narrow Russian rendering runs in
the existing UI smoke job. Static binding, workflow and whitespace checks passed.
The project contract now identifies the PR base for each edition.

The first 360 px CI render (run 44194961677) detected 25% overlap between
the Russian protocol-use badge and the ping text. Give metadata a full
row below the actions, wrap it at narrow sizes, and repeat the same render.
