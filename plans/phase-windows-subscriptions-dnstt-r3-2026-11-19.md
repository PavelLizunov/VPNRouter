# Windows subscription switches and Standard DNSTT candidate

Base fbc023da. Windows subscription toggles must persist immediately and remove
disabled rows from the usable pool. The Simple URL and manual refresh must
respect a previously registered disabled source. Existing registrations and
cached rows remain usable after re-enabling. An established tunnel stays up
until the user's next Apply or reconnect.

Cover the real checkbox-backed view model, reload from YAML and Connect using
a disabled source. Run the desktop suite, Android compile, live Windows
verification and the Standard tag gates for 2.50.6-r3. Revert the fix if the
saved selection or manual subscriptions regress.
