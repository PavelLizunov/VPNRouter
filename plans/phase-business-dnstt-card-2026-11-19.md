# Business DNSTT row country and status

Business 2.50.13 sends the generic TCP port probe to the DNS resolver from the DNSTT profile. It also uses that resolver as the key for node country and activity. A reachable resolver can therefore appear as a healthy DNSTT server and carry another node's metadata.

Match the row's metadata to its delegated DNSTT DNS domain and leave DNSTT reachability unmeasured by the TCP port probe. Standard rows keep their existing metadata host and probe. The matching VPNCTL change publishes the optional domain on the eligible direct DNSTT node's existing country and activity record. Ambiguous or missing domains leave the row's country unknown.

Verify the row both with and without its domain metadata, with a conflicting resolver row, and with the regular TCP probe adjacent to a DNSTT row. Use Business CI and independent review. Scope excludes tunnel changes and production deployment. Rollback is a revert of the isolated client and metadata changes.
