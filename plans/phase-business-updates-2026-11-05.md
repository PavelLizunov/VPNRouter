# Business in-app updates

- Owner request: reuse the Standard edition update mechanism in Business.
- Scope: existing update check/download/apply UI in the compact menu, a Business-only source and retained corporate configuration.
- Invariants: never install Standard over Business, never publish an individual configured EXE, preserve account/selection/mode/language, and keep update responses data-only.
- Risk: HIGH because the existing installer replaces binaries containing an embedded corporate bundle. Plainly enabling the public GitHub source is insufficient.
- How: inspect existing IUpdateSource/UpdateNotificationViewModel/bootstrap/installer contracts, define private artifact identity and bounds, then implement the smallest compatible adapter and tests after the preceding exact-head CI gate.
- Rollback: preserve the issued installer and existing disabled-update default when no trusted Business update source exists.
- Gates: build/tests/headless CI pending; docs intent recorded; independent source design inspection running; exact update acceptance pending a safe private candidate; Standard API/behavior must remain compatible.
- Outcome: planning only; no updater implementation or release/publication performed.
