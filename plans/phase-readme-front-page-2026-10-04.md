# README front page: short, phone-first, current screenshots (R-1)

## Why

The owner pointed out that README.md is huge and hard to read, and that on a phone the releases can only be found at the bottom (the repository sidebar with Releases sits after the whole README on a narrow screen). Measured with the repo-hygiene `readme_check.py` on f41cc214:

- 293 lines in each of README.md and README.ru.md (target: about 150 or fewer).
- The first text link to the releases is on line 60 (visible line 14 of the page); above it only a small badge image.
- One HTML table with code blocks inside, three wide markdown tables, six code lines over 70 characters.
- The three screenshots (`VPNRouter.Tests/screenshots/page-*.png`) were last committed 2026-08-13: 43 to 51 days older than the newest UI change, i.e. they show the pre-redesign interface.
- Hard-coded versions (`v2.32.3`, `v2.15.8`, the `build.ps1 -Version` example) that go stale, and a "historical feature matrix" of a v2.32.3 audit from 2026-05-17.

## What

1. `README.md` and `README.ru.md` become a front page of about 120 lines each, same structure: name and one sentence, text links "Latest release" and "All releases", platforms, one current hero screenshot, install per platform as a short list, five-line "what it does", two more screens, a short clip, links to the guide pages, a short privacy and security summary, license.
2. The detail moves, mechanically and unchanged, into `docs/guide/` (English) and `docs/guide/ru/` (Russian): features, install and download (the manual download table, requirements, Windows notes), build from source, architecture, privacy and trust (with the code signing note), credits. The stale v2.32.3 feature matrix is dropped (still readable with `git show 6491be4c:plans/feature-catalog-2026-05-17.md`). Build examples use `{version}` placeholders instead of a typed version.
3. Screenshots are regenerated, not edited: rendered headlessly by the CI-built UI renderer (artifact of run 37170338541, head 8f7e3e97) from fixture data, no real server or credential, in English and Russian, dark and light. A short silent clip is made from those renders with Motion Studio and stored as an animated WebP. They live in `docs/images/`.
4. `AgentContextContractTests.PublicReadmes_ReferenceVersionedCredentialFreeScreenshots` follows the new image paths. The Android asset line `VPNRouter-v{version}-android-arm64.apk` stays in both READMEs (a release test pins it).
5. The release skills no longer ask to retype versions into the READMEs (`cut-stable` step 3, the last paragraph of `ship-rolling-candidate`); the examples are placeholders now. `docs/AGENTS.md` lists the guide pages.

## Not covered

- Android device screenshots (needs an emulator or a device with a prepared state); the Android screen can be added later.
- Live desktop capture: forbidden by the contract (secret-bearing config). All images are isolated headless renders with fixture data.
- Hosting the MP4: GitHub does not embed a repository-relative video in a README; the WebP is the embedded form.

## Invariants

- No product code changes. No credentials, addresses or subscription links in images or text.
- `VPNRouter-v{version}-android-arm64.apk` present in both READMEs; "arm64/arm/x64/x86 universal" absent.
- README.ru mirrors README.md section by section. No emoji added; the old platform icons are removed in this dedicated presentation edit.
- Nothing is lost: every line removed from the old README is either on a guide page or on the list of intentional drops (checked with the line multiset script).

## Verification

`readme_check.py` before and after (target: no FAIL), line multiset old README vs new README plus guide pages, relative links and anchors, image sizes (at most about 200 KB each, clip a few MB), a render of the new README at phone width in headless Chromium, `git diff --check`, PR CI on the exact head (contract tests for the READMEs and skills). Independent read of the first screen by one external model through `pi` (owner-approved), findings checked by hand.

## Rollback

Revert the PR. The old screenshots under `VPNRouter.Tests/screenshots/` stay in place.

## Outcome

Ready for review (not merged: merging needs the owner's command).

- README.md and README.ru.md: 293 -> 95 lines each. `readme_check.py` before: 2 FAIL (length; first text link to the releases at visible line 14) and 6 WARN (a wide-table and HTML-table group, 6 long code lines, 3 screenshots 43 to 51 days older than the UI, 8 hard-coded versions). After: 0 FAIL, 0 WARN.
- Detail moved unchanged to `docs/guide/` (6 pages) and `docs/guide/ru/` (6 pages), links re-based, build examples use `{version}`. Line multiset check old README vs new README plus pages: only the intentional drops remain (stale v2.32.3 feature matrix, the old screenshot block, the rewritten header and tagline, the merged license lines). The check caught one real loss: the Russian "Free Configs" subsection sat inside the dropped matrix section; it is now on `docs/guide/ru/features.md`.
- Images: 6 PNG (about 60 KB each) and 2 WebP clips (1.3 and 1.4 MB, 12 s, silent, seamless loop) in `docs/images/`, rendered headlessly from fixture data by the CI-built UI renderer (run 37170338541, head 8f7e3e97), English and Russian. First renders at 740 px showed a scrollbar and a cut-off card, re-rendered at 780 px. The clip was made with Motion Studio's capture tool (360 frames in about 36 s per language, run on the workstation after a load check: load 2.5, 4 GB RAM free; it is a render, not a build). Clip sources are kept outside the repo in `~/VPNRouter-knowledge/readme-clip` with rebuild notes.
- Phone check: both READMEs rendered with GitHub's markdown renderer and loaded in headless Chromium at 390 px: body width 390, no sideways page scroll; the three install one-liners scroll inside their code blocks.
- Test: `AgentContextContractTests.PublicReadmes_ReferenceVersionedCredentialFreeScreenshots` follows the new image paths (both languages, four files each). The Android line `VPNRouter-v{version}-android-arm64.apk` is unchanged in both READMEs. Release skills `cut-stable` (step 3) and `ship-rolling-candidate` (last paragraph) no longer tell people to retype versions into the READMEs. `docs/AGENTS.md` indexes the guide pages and images.
- Independent read: one external reviewer (`pi`, owner-approved) compared the new pages with the old README, about 7 minutes. Confirmed and fixed: the tagline "only the apps you choose go through the VPN" was wrong for the exclude and full-tunnel modes; the Free Configs tab was folded into the Windows-only bullet (it is available on all platforms). Changed after its doubt: "one tap" became "one button" in the clip and alt text. The light theme and the sample-data provenance it could not verify were checked by hand against the renders.
- CI on the exact head 5e0a8e81: test, characterization-windows, go-test-windows and grep all passed.
- Not done: Android device screenshots; no live desktop capture (forbidden by the contract). The old `VPNRouter.Tests/screenshots/page-*.png` stay (tests regenerate them) but the READMEs no longer use them.
- Follow-ups: refresh the images with the renderer after the next visible redesign; `docs/guide` Russian pages mirror the English ones by hand.

Rollback: revert the PR.
