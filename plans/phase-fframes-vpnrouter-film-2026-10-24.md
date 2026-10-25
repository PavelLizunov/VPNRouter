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

1. Build: PASS at `1df8f84319073960a72a1f6ddddb767ce06700b4`, Actions run `40124176191`: isolated release renderer compiled with source-built FFmpeg in 14m26s; complete job 15m54s. Worker preflight recorded below. Existing product CI Release build passed, zero errors (197 existing warnings).
2. Tests: PASS. Two Rust tests; all 1080 frames inspected with zero findings; 1080 frames scanned for pops with zero findings. Both H.264/yuv420p/AAC exports passed exact size/fps/frame-count/duration assertions and complete decoding. Product CI: Linux 3189 passed / 77 skipped; Windows 3332 passed / 19 skipped; Go and fingerprint checks passed. No live VPN scenario was run.
3. Docs: PASS. Renderer README documents commands, source-built FFmpeg dependency path, original assets, provenance, licenses and verification limits. Existing root README files need no edit because product behavior/setup remain unchanged.
4. Review: PASS for scoped source review. Three independent read-only reviews covered correctness/timeline, presentation/provenance and export/security; lead reopened source and disposed all findings below. No unresolved task P1 remains. No full-repository audit.
5. Visual/audio: PARTIAL. Lead visually inspected the 12-cell contact sheet and six full-size frames (3/9/16/22/28/34 seconds): readable captions, intact source images/mascot, no observed clipping or overlap, visible sample-data disclaimers. Declared foreground/background contrast is at least 7.97:1; normal fades intentionally pass through lower opacity. Master audio measured -18.0 LUFS, 1.5 LU range and -6.8 dBFS true peak. Subjective listening and browser playback NOT VERIFIED: browser tools blocked file URLs and access to the current GUI; the owned browser was closed. No replacement server was started. WINBRAT is N/A: no product UI change or release.
6. Integration: PASS for artifact delivery/integrity. Task source committed/pushed; exact remote and PR head verified, every executed check green. Downloaded source ZIP passes CRC and matches all 25 source/media/license files byte-for-byte; excludes Git credentials and build caches. All three delivery hashes match CI, both MP4s have faststart metadata before media payload, and native presentation succeeded for both videos plus the source ZIP. Mechanical characterization is N/A; no production code was split.

## Material unknowns

- Resolved: prebuilt FFmpeg had an x264 ABI mismatch; a source build on the same worker OS exported successfully.
- Unresolved but unused: generated-music controller restoration was not confirmed after rejected submissions; this film does not use that controller or any generated music.
- Resolved: encoded sizes are 5.93 MB for the master and 1.42 MB for the smaller preview.
- Audio update: the Host audio generator rejected both calls before submission because of its configured-workspace gate; no GPU job was accepted. A direct controller status check timed out. Do not retry generation or alter the tool configuration. Use the existing CC0 instrumental "Interchange" by TRG Banks instead; CC0 and instrumental status were checked at the original Free Music Archive page as well as Wikimedia Commons. This changes only the optional music implementation, not the video or its claims.

## Outcome

- Status: DELIVERED, with subjective listening/browser-playback limitations stated below.
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
- Successful export revision: `1df8f84319073960a72a1f6ddddb767ce06700b4`, run `40124176191`, job `120961936488`; artifact `12573315352` (19358345 bytes). The exact remote branch SHA and PR head matched. Five executed checks passed, plus the intentional duplicate PR render skip.
- Successful worker preflight, 2026-10-24 19:11:49 UTC: 4 CPUs, load 0.18/0.07/0.02; RAM 15989 MiB total / 15009 MiB available; swap 3071 MiB total / 0 used; disk 87422 MiB available; rustc/cargo 1.99.0, Clang present, no competing compiler in the recorded top processes. Full export work stayed on the disposable CI runner; local work was download, inspection and lightweight validation only.
- Download verification: master 5932715 bytes, preview 1420030 bytes, source ZIP 5652340 bytes. SHA256 respectively `dce0f3d8bea755941f0e27fa3ee48f7881026cad3746b675c52625b7d1a00b3a`, `d7fbb41aeb7e9f3a9b03b12b8251e3a130b515a63578355882a1f4aa21800fc5`, `04c71a3f4bc05708f1a82fede433d7e0c1f4f2c9dbe003eebadbe0062416b068`. Archive CRC and all 25 exact source/media/license byte comparisons passed. Master and preview MP4 top-level boxes confirm faststart.
- Native delivery: `present` succeeded for `out/vpnrouter-film.mp4`, `out/vpnrouter-github.mp4` and `out/vpnrouter-film-source.zip`. `artifact_evidence` retained the successful presentation receipt. Its image-generation status is NOT_RUN because these are deterministic CI fframes artifacts, not image-model output; no image-generation claim is made.
- Browser-playback verification attempt: direct file access is blocked; current GUI access is also blocked by the available browser client. Browser closed. No server substitution or browser-policy bypass attempted. Decoding, metadata, frames, source integrity and measured audio passed; subjective soundtrack listening remains unverified and is disclosed rather than inferred from the level report.
- Anti-slop delivery gate: factual copy/provenance PASS; explicit design-read and restrained palette/motion purpose PASS; measured palette contrast PASS; visible static frame composition PASS; interactive verification N/A for a noninteractive film; subjective playback/listening NOT VERIFIED. No fake stats, quotes, security promises or generated-brand claims.
- Product installs, VPN/network state, release settings, tags, root READMEs and branding originals are unchanged. Nothing merged, released or deployed. Rollback remains a revert of task-owned commits on the PR.

## Late review disposition

- Review `3c409ed7-c161-419f-8193-f2e783f993bb` arrived after delivery; its closing message identifies `gemini-flash-high-latest`. It supplies additional source-review evidence, not another render, playback or listening receipt.
- Lead reopened `graphics.rs:162-175` and `shots.rs:113-151` at `567daaadf3df8d0a8f942ae8cacc5325ccabcf5f`: `screenshot()` applies an upward drift capped at 8 pixels, but the Applications focus rectangle and Connect pulse ring do not share that transform. The relative-motion inconsistency is source-confirmed P2. Automated clipping/pop checks do not test registration between a screenshot and its highlight.
- Disposition: retain the already delivered export unchanged; record this non-blocking visual polish item as `FFRAMES-HIGHLIGHT-DRIFT` in the defect ledger. A future media revision should put each screenshot and its highlight in the same motion group (including entrance motion), add an alignment regression, and rerender both MP4s/source ZIP together. No new export is claimed by this documentation checkpoint.
- The late report's unused-light-fixture finding duplicates the existing deferred entry; chapter activation at crossfade completion remains intentional. General source-review assurances do not extend the established visual/audio verification limits.
