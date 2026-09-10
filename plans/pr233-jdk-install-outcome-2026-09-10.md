# PR233 JDK preparation outcome

Owner approved toolchain preparation and explicitly accepted android-sdk-license for command-line tools, platform36 and build-tools36.0.0; no additional licenses accepted.

Linux JDK installed at /home/tester/.local/share/vpnrouter-build-tools/jdk-17.0.20.1. Official Temurin archive SHA256 verified against manifest. java -version reports17.0.20.1+1, x64 HotSpot, command exit0 (job bash-198). Existing environment profiles and system Java unchanged; no sudo used. Archive license/legal files retained in installation.

First extraction attempt bash-197 stopped on Python tarfile.extractall(filter=...) incompatibility before target creation. Task temporary files removed. Corrected extraction validated member types, relative paths, link destinations and absence of members under link parents, then used native tar. No Python upgrade or verification bypass.

Android command-line tools selected to match CI-pinned setup-android default archive14742923 (20.0), not latest23. Platform36 revision2 and build-tools36.0.0 official metadata recorded in pr233-android-toolchain-manifest-2026-09-10.json. SDK/JDK metadata and archive checksum retrieval completed; Adoptium API403 handled via official GitHub release metadata, not third-party mirror.

Android SDK archives and .NET Android workload NOT installed yet. Next: download/check/extract only approved command-line tools, execute sdkmanager version under JDK17, install exact approved platform/build-tools with scoped license handling, then Android workload with bundled manifests. Stop on additional package/license requirements. No builds, signing, emulator, NDK, deployment or VPN operations performed in this stage. #233 acceptance remains pending.
