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
- Delivery, CI, review and final file evidence: pending.
