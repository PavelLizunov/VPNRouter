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
The renderer artifact at 0c5f22ca produced RU/EN final frames with two rows,
selected first result and enabled Connect. The earlier fixture was cleared by
lazy cache load on first public-tab navigation; selecting tab 5 before seeding
fixes that owning cause. PR #606 CI green at 0c5f22ca. Follow-up regression now
uses the exact window-advanced/locale/navigation path and asserts enabled Button.
Website PR PavelLizunov/ninitux-landing#5 at b3ad554 is deployed on ninitux.com;
27 CI browser checks and 30 live bilingual frame checks pass. This tooling
change is not part of candidate v2.50.5-r3 and changes no shipped behavior.
