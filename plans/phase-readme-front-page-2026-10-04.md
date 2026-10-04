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

Pending.
