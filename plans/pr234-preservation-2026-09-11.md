# PR234 log-key preservation

Closed PR234 `sentinel/redact-log-key-secrets-646982941767858305`, tip `8251eb30932d00b7baea6c9e3f27da677ee41c02`. Full merge-base diff inspected: explicit key alternatives and one seven-key test, no other files. Current main2689ee77 contains each production alternative (access/auth/client/app/user/enc/encryption key) plus session key.

After accepted #257, the existing diagnostics fixture tests six of the seven original key names; app_key appears in comments/production regex but a repository-wide search for app_key/app-key/appkey followed by = or : in C# tests found no executable fixture row. Unlike mere separator cross-products, app_key has a separate literal regex alternative, so its original regression row is useful coverage to preserve. No production vulnerability or runtime failure inferred.

Disposition: retain branch pending bounded test-case preservation or explicit archive decision. Do not restore duplicate seven-key fixture; one row and assertion in existing fixture is sufficient if authorized. No code changes or deletion performed.
