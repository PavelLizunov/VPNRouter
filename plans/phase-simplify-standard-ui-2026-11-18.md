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
