# VPNRouter fframes repository film

- Owner: DSH lead for Pavel Lizunov
- Authorization: owner asked the assistant to choose a GitHub repository and deliver the completed film without a separate concept approval.
- Branch: dsh/fframes-vpnrouter-film
- Base: c3ebc46ee7a336bc689a893a00d2db8c9bf4a300
- Roadmap: N/A, standalone promotional media; no VPN product change.
- Risk: LOW. An isolated Rust video project and an unprivileged CI renderer.
- Blast radius: tools/help-videos/fframes-vpnrouter/, a dedicated Actions workflow, and this record. Existing product source, releases, READMEs and installs remain unchanged.
- Rollback: close the task PR or revert its task-owned additions; no deployment or release is performed.

## Why

Demonstrate the user's requested AI-assisted video workflow using fframes, and create a usable film for the public VPNRouter repository rather than a generic slideshow.

## What

- Deliver an approximately 36-second English-language 1920x1080, 30 fps H.264 MP4, a compact GitHub-friendly preview, and a self-contained source archive.
- Build the film with fframes 1.3.0 and Rust/SVG, using existing VPNRouter artwork and sanitized README fixture images.
- Explain per-application routing, contrast VPN and direct traffic, show the real published UI fixtures, and end with the repository URL.
- Add a quiet instrumental soundtrack if the available audio generator completes successfully. No voice-over is required.
- Do not change app code, branding assets, installer scripts, live settings, CI release gates, tags or repository settings.
- Do not claim a UI recording or live connection: published UI images are fixture renders. Label the illustrative routing diagram and fixture material appropriately.
- Product claims are bounded by README.md and its linked features documentation at the base SHA; no invented performance, popularity or security statistics.

## Design read

Motion explainer for developers browsing GitHub, rooted in VPNRouter's dark application palette and existing mascot. ENERGY 2 / RHYTHM 2 / MOTION 3. The moving split path is the narrative device, not decorative background. Large editorial type explains one choice at a time. Cyan identifies the selected route, green confirms active VPN, and a neutral path denotes direct traffic. Source screenshots retain their original pixels and provenance.

## How

1. Confirm ownership and current source through the existing GitHub login; select public VPNRouter for its concrete visual story.
2. Adopt the released fframes crate instead of building a second animation engine. Existing help-video code is a screen-tour assembler, not a substitute for the requested fframes workflow.
3. Commit this brief and observe actual PR checks before implementation.
4. Implement an isolated Cargo project, embedded licensed fonts and original product assets. Six connected scenes carry routing paths across transitions rather than independent static slides.
5. Build and render at an exact task SHA on GitHub-hosted CI. No toolchain provisioning or build on harness-test. Use the CPU backend because the hosted renderer has no dedicated GPU; this does not use or reconnect the local AI controller.
6. Run fframes inspection, render frame/contact-sheet evidence, verify export metadata and decode integrity, inspect representative frames, and review the scoped source independently.
7. Deliver files with native presentation. Commit and push sources on the task branch, verify the exact remote commit and PR checks. Do not merge or publish a release.

## Verification

1. Build: PENDING. Compile the standalone fframes release binary on CI at the exact pushed SHA; record worker CPU/RAM/disk/toolchain preflight. The unchanged .NET product build is outside this media-only delta; existing PR checks still apply.
2. Tests: PENDING. Run small deterministic timeline/layout checks and the built-in fframes inspect command. Validate 1920x1080/30 fps/expected duration and audio stream, then decode the encoded film. No VPN scenario or installed-app execution.
3. Docs: PENDING. Project README documents actual commands, assets, provenance, licensing and deliberate limitations. Root README.md and README.ru.md are reviewed but need no change because product behavior and setup remain unchanged.
4. Review: PENDING. Independent correctness, presentation/provenance and CI/security review of task-owned files, with lead verification of findings. No full-repository audit.
5. Visual/audio: PENDING. Review contact sheet and selected full-size frames; check line breaks, safe margins, source images, animated routing and final slate. Analyze audio loudness if a soundtrack is used. WINBRAT is N/A because there is no app UI change, installed-app scenario or release.
6. Integration: PENDING. Verify a clean scoped diff, repeatable renderer instructions, downloadable complete sources, exact pushed SHA and actual green PR checks. Mechanical characterization is N/A; no production code is split.

## Material unknowns

- First fframes dependency build and static FFmpeg prebuilt availability on the CI runner.
- Available generated music and exact generator restoration result.
- Final encoded size; provide a smaller preview if necessary for easy GitHub use.
- Audio update: the Host audio generator rejected both calls before submission because of its configured-workspace gate; no GPU job was accepted. A direct controller status check timed out. Do not retry generation or alter the tool configuration. Use the existing CC0 instrumental "Interchange" by TRG Banks instead; CC0 and instrumental status were checked at the original Free Music Archive page as well as Wikimedia Commons. This changes only the optional music implementation, not the video or its claims.

## Outcome

- Status: IN PROGRESS.
- Observed baseline: active GitHub account owns VPNRouter; all observed latest base checks succeeded. The initial workspace was empty and has been populated with the clean public repository at the base SHA.
- Environment: harness-test is control plane only. Existing Rust is present but no local compile or SDK installation will be performed. sudo is unavailable; no escalation is attempted.
- Implementation checkpoint: `24205d9f7d1247504a0485ba837b27fb4c496c55`, PR #623. Product CI `test`, `characterization-windows`, `go-test-windows` and `grep` passed at this head; the same-repository PR render was skipped by design.
- Render attempt 1: Actions run `40122584593` failed linking after 3m14s, exit 101. The downloaded static FFmpeg expects `x264_encoder_open_163`, while Ubuntu 24.04 supplies `x264_encoder_open_164`. No render, Rust tests, inspection, mux or export verification was reached. The 785-byte film artifact contains preflight evidence, not a film.
- Recovery intent: keep fframes 1.3.0 and the worker OS, force the dependency's documented source-build path against the runner's installed x264, use a fresh cache namespace (the dependency otherwise reuses an existing static archive), commit the actual resolved Cargo.lock and build/test with `--locked`. This is an in-scope build compatibility repair, not an encoder or rendering-engine substitution.
- Recovery verification: exact-head successful link and Rust tests, then all 1080-frame inspection, encoded output and source-archive gates; retrieve and inspect actual frames before any completion claim. Separate read-only reviewers cover timeline/correctness, presentation/provenance and export/security. Previous review references could not be recovered through the ordinary-chat reader; no review result is inferred from them.
- Worker attempt-1 preflight (2026-10-24 18:51 UTC): 4 logical CPUs, load 0.33/0.08/0.03, RAM 15989 MiB total / 14768 MiB available, swap 3071 MiB total / 0 used, disk 87422 MiB available; rustc/cargo 1.99.0 and Clang present. No competing compiler in recorded top processes. The replacement run repeats preflight before source compilation.
- Independent correctness review `3e5ff279` source-confirmed the 1080-frame timeline and overlap/compositing semantics. Lead reopened fframes 1.3.0 `video.rs:104-134` and `fframes_context.rs:124-135`. Two minor findings recorded in the ledger: align the header weight literal to its existing 500 clamp (fixed), and retain the unused light UI fixture as an editing asset (deferred). Chapter activation at the end of each crossfade is intentional; missing images are not silent at inspection time because `get_image()` records a diagnostic. Source review is not evidence of completed render or executed tests.
- Narrow local verification: shell syntax, Python AST, TOML lock parse, workflow YAML parse, scoped whitespace and source-build/locked-command assertions passed. The committed lock is byte-identical to the run-1 artifact (SHA256 `21f848c0b8115ac572a5456b8f01c2917d5b1ce1156dd963f164ea5b18cc6edb`). Five product media files are byte-identical to the originals at this checkout.
- Independent export/security review `0ddc6479`: the original missing source-build flag and lock are addressed by `export.sh` and the pending lockfile commit. A duplicate job-level flag is unnecessary: the only build step invokes this script, which exports the flag before both Cargo commands. Source-build timeout was raised from 35 to 50 minutes as headroom, not a measured performance requirement. Audio loudness remains a measured manual acceptance gate; no unexecuted automatic assertion is claimed. The preinstalled Rust 1.99.0 satisfies edition 2024; no control-plane tool installation is performed. GitHub's official tag mappings verify the pinned checkout SHA as v7.0.1, cache as v4.3.0 and upload-artifact as v7.0.1; the reviewer's untagged-action concern is refuted by that lookup. Other review hypotheses are not established defects.
- Independent presentation/provenance review `9e9f465d` confirmed bounded copy, sample-data labels, all five original product-image hashes and bundled font/music license coverage. Its weight/lock/unused-fixture findings duplicate those above. The duplicate GPL copy is retained intentionally in the license bundle; no functional/legal defect was established. Actual rasterized typography, transitions and audio remain unverified until export.
- Delivery, successful media CI, visual/audio review and final file evidence: pending.
