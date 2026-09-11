# PR242 preservation exception

Closed PR242 tip `053b31b7d9d511274279269e292553dce8a9d882` adds general key=value/key: value scrubbing directly to CrashReporter.ScrubSecrets and a four-input regression fixture. Closure cites #252, but #252 expands DiagnosticsRedactor's separate log-key regex instead.

Parent checked current main2689ee77 source: CrashReporter.ScrubSecrets198-210 applies URI, UUID, long-key and query-parameter patterns only; it does not call DiagnosticsRedactor.RedactLogText. CrashReporter report construction calls ScrubSecrets directly for exception text and tail log lines (111,135). Therefore the diagnostics-export replacement is not by itself evidence that the crash-report entry point preserves the original plain key-value requirement. Do not delete this branch as redundant based only on its closure comment.

Disposition: RETAIN for scoped security/policy review. This source distinction is not a newly executed leakage reproduction. Do not automatically copy the duplicate regex or call DiagnosticsRedactor from ScrubSecrets: DiagnosticsRedactor already calls ScrubSecrets, so a naive delegation would recurse. Any implementation requires separate approved scope and tests. No product changes or branch deletion performed.
