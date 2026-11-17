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
