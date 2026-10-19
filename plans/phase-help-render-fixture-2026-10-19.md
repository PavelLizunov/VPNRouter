# Help guide fixture - 2026-10-19

The owner requested current website help animations and repair of the last guide.
The final free-server frame incorrectly shows an empty list and disabled Connect.
Add a deterministic, offline, documentation-only `free-results` UiProbe scenario
using reserved example addresses. No application runtime behavior or release
version changes. Test fixture selection and render, publish the task branch and
build the renderer at its exact SHA. Use the artifact for bilingual site frames.
Rollback: remove this scenario and regenerate the previous frames.

Verification: focused UiProbe test and CI; inspect rendered final frame for a
selected result and enabled Connect. No real endpoint, network scan or VPN action.
Outcome pending.
