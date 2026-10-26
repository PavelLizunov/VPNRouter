# Existing solutions decision - 2026-10-25

## Requirement and boundary

Speed up the existing Rust/SVG fframes 1.3.0 batch without replacing its engine,
redesigning styles, changing decoded graphics/music or removing verification.
Separate authoring convenience from measured export speed. No library install,
new infrastructure or final-resolution export is part of this decision.

## Animation and authoring

- fframes 1.3.0 is MIT licensed in its published Cargo manifest. Its existing
  `timeline!`, `Easing::Spring`, typed `Transform` and text-width/wrapping APIs
  already cover the requested animation/layout helpers. The pinned upstream
  examples include hello-world, conference-splash-screen and beta. They are
  reusable patterns, not measured acceleration of this film.
  [Published source](https://docs.rs/crate/fframes/1.3.0/source/),
  [pinned examples](https://github.com/dmtrKovalenko/fframes/tree/654ae86ffdff31202940d8cf05cdcdc30efe6228/examples).
- Animato 1.7.3 registry record: published 2026-10-21, MIT OR Apache-2.0,
  Rust 1.89, edition 2024. Its default feature set includes std/tween/timeline/
  spring/driver: do not interpret its zero-mandatory-dependency description as
  zero default dependencies. It overlaps the existing primitives; no integration
  or measured gain is demonstrated. Its custom macro does not receive the
  fframes-specific timeline lifting automatically. This is not proof that all
  other SVG caching would break.
  [Exact registry record](https://crates.io/api/v1/crates/animato/1.7.3),
  [upstream MIT license](https://github.com/AarambhDevHub/animato/blob/main/LICENSE-MIT).
- Keyframe 1.1.1 registry record: published 2022-07-11, MIT, Rust 1.51,
  default mint_types/alloc features. Its sequence API and easing duplicate the
  existing film's time-based math. Registry age alone does not prove abandonment;
  no issue-response or compatibility build was performed.
  [Exact registry record](https://crates.io/api/v1/crates/keyframe/1.1.1).

Verdict: Extend - existing fframes 1.3.0 primitives and official examples - exact
API/feature fit without adding another animation dependency. Retain scene code
for this optimization; consider typed helpers only if profiling later justifies it.

## Build and cache

- Keep the already SHA-pinned MIT-licensed actions/cache action. It stores Cargo
  registry/git/target, including native FFmpeg build outputs. Caches are scoped by
  key/version/branch; unrelated branch caches are not generally reusable. A
  965244878-byte cache exists on the current task branch, but existence alone does
  not establish successful restoration or a warm build.
  [GitHub cache scope](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching),
  [action license](https://github.com/actions/cache/blob/main/LICENSE).
- Swatinem/rust-cache is LGPL-3.0 and automates compiler/environment keys and
  dependency-only cleanup. It shares GitHub's branch scope. Its target path is
  relative to the workspace (`tools/help-videos/fframes-vpnrouter -> target`),
  not a second repository-relative target path. Useful if cache maintenance is
  measured as a problem; no demonstrated need to replace the existing action.
  [README](https://github.com/Swatinem/rust-cache),
  [license](https://github.com/Swatinem/rust-cache/blob/master/LICENSE).
- Mozilla sccache is Apache-2.0 and supports Rust/C/C++ compilation caching.
  External storage can share records independently of GitHub branch caches but
  adds installation, credentials and infrastructure. Merely setting RUSTC_WRAPPER
  does not prove FFmpeg C objects are cached; native configure/CC propagation must
  be checked first. Not adopted.
  [Project](https://github.com/mozilla/sccache),
  [license](https://github.com/mozilla/sccache/blob/main/LICENSE).
- cargo-chef targets Docker dependency layers. The existing job is not a Docker
  build; adding container layers is not the smallest solution to this workload.
  [Project and licenses](https://github.com/LukeMathWalker/cargo-chef).

Important counterevidence: ffmpeg-sys-fframes 9.0.0 build.rs lines 782-786 invokes
`make -j num_cpus::get()`. CARGO_BUILD_JOBS=2 does not establish that FFmpeg make
was restricted to two CPUs. Raising it to four cannot honestly be described as a
proven 2x reduction or a 7-8 minute build. No such change is adopted without a
fresh attributable benchmark. Preserve FFMPEG_FORCE_BUILD=1 for the known x264 ABI.
[Exact crate source](https://docs.rs/crate/ffmpeg-sys-fframes/9.0.0/source/build.rs).

Verdict: Extend - existing actions/cache and FFmpeg stream copy - first measure
the current cache restoration, then reuse one identically prepared AAC soundtrack
per batch if full decoded-video/PCM equality and paired timings pass. No new
build service, dependency or cache backend is needed for that change.

## Measurement limits

Two independent read-only research workers inspected animation and build/cache
surfaces. The lead checked the published crate records, licenses, actual compiler
invocation and current exporter. Worker claims about release dates, automatic
zero allocations and guaranteed twofold build gains were not accepted unchanged.
Neither package compatibility builds nor subjective motion/listening tests were
run during research. Final benchmark outcomes belong in the task record.
