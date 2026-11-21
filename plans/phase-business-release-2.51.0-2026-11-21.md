# Business 2.51.0 Windows release

The owner requested Business 2.51.0. Use the accepted business head
dba06d5d4ee972b0e9886d9b4d994bf240b4ddbc as the source and bump the edition's
version. Verify the exact release source in GitHub CI.

Build the shared Windows x64 update using the existing Business packaging
workflow. Check the edition, version, revision, archive layout, and SHA-256.
Advance the Business manifest after validating the archive and preserving the
currently offered 2.50.14 package for rollback. The shared archive uses external
provisioning.

The accepted base has 12 successful checks and three disabled dispatch jobs.
The live Business manifest advertised 2.50.14 at intake.
