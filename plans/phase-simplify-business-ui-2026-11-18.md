# Show the installed Corp version in the Business heading

Base: business at 97805d04. The prior Windows acceptance recorded a missing
installed version in the compact window. Bind its existing product heading
to the short application version already used by Standard. Remove unused text
projections for the retired details area after checking XAML and callers.
Preserve the native window title, connection controls, update feed and policy.
Rollback is a revert of the task change.

Verification gates: exact-head Business build and tests; test the rendered
heading against the running version; record this outcome; review bindings and
consumers; inspect 340/420 px Russian and English fixtures; verify shared
version ownership and existing automation IDs. Live install applies at release.

## Implementation

The compact window now reuses AppVersionShortText in its existing heading.
Removed four unused text projections and their backing translation entries.
The four narrow-window cases check the heading text, visibility and bounds.
The native title and updater commands still use their original bindings.
Static XAML, consumer and whitespace checks passed.
The project contract now identifies the PR base for each edition.
