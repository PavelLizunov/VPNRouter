//! Fixed, disposable importer-observation fixture; copied to examples/importer_segment.rs.
//!
//! Source contract (no native compilation or execution during implementation):
//! - Approved brief: plans/phase-fframes-eight-styles-2026-10-25.md,
//!   "Authorized bounded importer observation - 2026-10-26" (brief commit
//!   969bbe05149eb1c197754e8824373a7058c06615).
//! - Frozen caller: ac5ac03c, tools/help-videos/fframes-vpnrouter/src/main.rs
//!   (media/fonts/encoder options) and src/lib.rs (unmodified Film, Style::Anime).
//! - Locked fframes 1.3.0 archive SHA256:
//!   24719b199dd9da6c5d8aed3fa50bd4bc89af1b3dc9e096a6472cf774634ee0c0.
//! - Retained source under tools/help-videos/fframes-vpnrouter/out/:
//!   native-importer-20261026/crate-source/src/renderer/cpu.rs:146-290
//!   (worker, caches, negotiation, pooled conversion, submit and finish), plus
//!   render_backend.rs and frame_guard.rs in the same directory;
//!   repeatability-analysis/source/fframes-1.3.0/src/renderer/{mod.rs,
//!   renderer.rs,scheduler.rs,segment_writer.rs,encoder.rs} (public reexports,
//!   full render context, original claims, segment naming and default GOP).
//!
//! Only segment 768 is rendered; skipped claims never build SVGs. The scheduler
//! still covers all 1080 frames. No claim is rewritten and no concatenator/audio
//! output is run. The CPU renderer owns its original converter and frame pool:
//! this fixture neither normalizes padding nor replaces pooled encoder input.
//! Limit: cold allocator/cache history is not the historical full-film history.
//! Observation can perturb state; failed transparency or absent reproduction must
//! stop the surrounding CI experiment, not trigger a larger render campaign.

use fframes::{
    AbortSignal, AudioTimelineSamples, CpuFrameRenderer, EncoderFrameRenderer, EncoderInput,
    EncoderOptions, FFramesContext, FFramesLogger, FFramesRenderBackend, FFramesRendererError,
    FFramesRendererResult, Frame, FrameClaim, FrameRenderer, FrameScheduler, MediaDirectory,
    RenderOptions, ResolvedRenderingTimeline, SegmentWriter, SvgrImages, TextCache, Video,
    VideoDecodersWorker, YuvMatrix,
    cpu::{CpuEncoderFrameRenderer, CpuRenderingBackend},
    render_frame_guarded, usvgr,
};
use std::{
    error::Error,
    fs::OpenOptions,
    io::{self, Write},
    path::{Path, PathBuf},
    process::ExitCode,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
};
use vpnrouter_film::{Film, Style};

const TIMELINE_FRAMES: usize = 1080;
const FPS: usize = 30;
const SEGMENT: usize = 768;
const SEGMENT_FRAMES: usize = 24;
const PREVIEW_SCALE: f64 = 0.4444444444444444;
const SEGMENT_FILE: &str = "0000000768.mp4";

struct ImporterSegmentBackend {
    cpu: CpuRenderingBackend,
}

fn invalid(message: impl Into<String>) -> FFramesRendererError {
    FFramesRendererError::Custom(message.into())
}

impl FFramesRenderBackend for ImporterSegmentBackend {
    fn frame_renderer(&self) -> Option<impl FrameRenderer + '_> {
        Some(CpuFrameRenderer::new(self.cpu.cache_capacity))
    }

    fn encoder_frame_renderer(
        &self,
        input: &EncoderInput,
        width: u32,
        height: u32,
    ) -> FFramesRendererResult<impl EncoderFrameRenderer + '_> {
        CpuEncoderFrameRenderer::new(self.cpu.cache_capacity, input, width, height)
            .map_err(|err| FFramesRendererError::from_chunk(0, err))
    }

    fn render_frame<'a, 'media: 'a, TVideo: Video + Sync + Sized + Send>(
        self,
        _frame: Frame,
        _video: &'a TVideo,
        _usvg_options: &usvgr::Options,
        _font_db: &usvgr::fontdb::Database,
        _ctx: FFramesContext<'a, 'media>,
    ) -> FFramesRendererResult<Vec<u8>> {
        Err(invalid("this fixed fixture only supports the segment render path"))
    }

    fn render<'a, 'media: 'a, TVideo: Video + Sync + Sized + Send>(
        self,
        output: impl AsRef<Path>,
        video: &'a TVideo,
        logger: Arc<dyn FFramesLogger>,
        usvg_options: &'a usvgr::Options,
        render_options: &RenderOptions<'a, 'media>,
        font_db: &'a usvgr::fontdb::Database,
        timeline: &'a ResolvedRenderingTimeline<AudioTimelineSamples>,
        ctx: &'a FFramesContext<'a, 'media>,
    ) -> FFramesRendererResult<()> {
        let directory = render_options.tmp_files_directory.ok_or_else(|| {
            invalid("the fixed fixture requires its fresh output folder as tmp_files_directory")
        })?;
        let expected_file = directory.join(SEGMENT_FILE);
        if output.as_ref() != expected_file.as_path() {
            return Err(invalid("unexpected segment output path"));
        }
        let frame_range = render_options.output_frame_range(ctx.duration_in_frames);
        let min_segment_frames = render_options
            .video_encoder_options
            .min_segment_frames(ctx.time_base.fps);
        let video_size = &ctx.current_video_size;
        if render_options.frame_range.is_some()
            || frame_range != (0..TIMELINE_FRAMES)
            || timeline.duration_in_frames != TIMELINE_FRAMES
            || ctx.time_base.fps != FPS
            || TVideo::FPS != FPS
            || TVideo::WIDTH != 1920
            || TVideo::HEIGHT != 1080
            || video_size.width != 854
            || video_size.height != 480
            || min_segment_frames != SEGMENT_FRAMES
            || self.cpu.concurrency != 1
        {
            return Err(invalid(
                "expected full 1080-frame 1920x1080 Film at 30fps, 854x480 preview, \
                 no frame range, default 24-frame segments and one default CPU worker",
            ));
        }
        // Reserve the evidence path without overwriting evidence from an earlier run.
        let mut manifest = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(directory.join("claims.json"))?;
        let frame_offset = frame_range.start;
        let scheduler = FrameScheduler::new(
            frame_range.len(),
            self.cpu.concurrency,
            min_segment_frames,
        );
        let writer = SegmentWriter::new(
            directory,
            "mp4",
            (
                video_size.width as i32,
                video_size.height as i32,
                ctx.time_base.fps as i32,
            ),
            render_options,
            &logger,
        );
        let encoder_input = writer
            .encoder_info()
            .and_then(|encoder| self.negotiate_encoder_input(&encoder))
            .map_err(|err| FFramesRendererError::RenderChunkError(0, err))?;
        let writer = writer.with_encoder_input(encoder_input);
        let failed = AtomicBool::new(false);
        let svgr_images = SvgrImages::new(self.svgr_rasterizer());

        let render_worker = |worker: usize| -> FFramesRendererResult<Vec<(usize, FrameClaim)>> {
            let worker_local_decoders = VideoDecodersWorker::new(1);
            let break_lines_cache = TextCache::new(self.cpu.text_cache_capacity);
            let mut converter_cache =
                usvgr::Cache::new_with_text_cache(self.cpu.text_cache_capacity);
            let mut renderer = self.encoder_frame_renderer(
                writer.encoder_input(),
                video_size.width as u32,
                video_size.height as u32,
            )?;
            let mut selected = Vec::with_capacity(SEGMENT_FRAMES);
            let mut rendered_frames = 0;
            while let Some(claim) = scheduler.claim(worker) {
                if failed.load(Ordering::Relaxed) {
                    return Ok(selected);
                }
                if ctx.abort_signal.is_some_and(AbortSignal::is_aborted) {
                    return Err(FFramesRendererError::Aborted);
                }
                // The only selection: preserve all three original FrameClaim fields.
                if claim.segment != SEGMENT {
                    continue;
                }
                if !(SEGMENT..SEGMENT + SEGMENT_FRAMES).contains(&claim.frame)
                    || claim.last_in_segment != (claim.frame == SEGMENT + SEGMENT_FRAMES - 1)
                {
                    return Err(invalid(format!("unexpected target FrameClaim: {claim:?}")));
                }
                let video_frame = claim.frame + frame_offset;
                let svg = render_frame_guarded(
                    video,
                    Frame::__internal_make_for_renderer(
                        video_frame,
                        video_frame,
                        ctx.time_base.fps,
                        break_lines_cache.clone(),
                        worker_local_decoders.clone(),
                    ),
                    ctx,
                )?;
                let rtree = svg.into_svg_tree(usvg_options, &mut converter_cache, font_db)?;
                let frame = renderer.render_tree(&rtree, TVideo::BACKGROUND_COLOR)?;
                writer
                    .submit_frame(claim, frame)
                    .map_err(|err| FFramesRendererError::RenderChunkError(worker, err))?;
                selected.push((worker, claim));
                logger.log_frame(rendered_frames, worker);
                rendered_frames += 1;
            }
            Ok(selected)
        };

        let workers = std::thread::scope(|scope| {
            let workers: Vec<_> = (0..scheduler.workers())
                .map(|worker| {
                    let (render_worker, failed, svgr_images) =
                        (&render_worker, &failed, &svgr_images);
                    scope.spawn(move || {
                        let result =
                            svgr_images.enter(usvg_options, font_db, || render_worker(worker));
                        if result.is_err() {
                            failed.store(true, Ordering::Relaxed);
                        }
                        result
                    })
                })
                .collect();
            workers
                .into_iter()
                .map(|worker| {
                    worker.join().map_err(|_| {
                        FFramesRendererError::Internal("Rendering thread panicked".to_owned())
                    })?
                })
                .collect::<FFramesRendererResult<Vec<_>>>()
        })?;
        let mut selected: Vec<_> = workers.into_iter().flatten().collect();
        selected.sort_by_key(|(_, claim)| claim.frame);
        if selected.len() != SEGMENT_FRAMES
            || selected.iter().enumerate().any(|(index, (_, claim))| {
                claim.segment != SEGMENT
                    || claim.frame != SEGMENT + index
                    || claim.last_in_segment != (index == SEGMENT_FRAMES - 1)
            })
        {
            return Err(invalid(format!(
                "selected claims were not exactly frames 768..791: {selected:?}"
            )));
        }
        let files = writer
            .finish()
            .map_err(|err| FFramesRendererError::RenderChunkError(0, err))?;
        if files.as_slice() != [expected_file.clone()]
            || std::fs::metadata(&expected_file)?.len() == 0
        {
            return Err(invalid(format!("expected exactly one nonempty segment: {files:?}")));
        }
        // Only integers, booleans and fixed strings: no JSON escaping dependency needed.
        writeln!(manifest, "{{")?;
        writeln!(manifest, "  \"style\": \"anime\", \"timeline_frames\": 1080, \"fps\": 30,")?;
        writeln!(manifest, "  \"width\": 854, \"height\": 480, \"frame_range\": null,")?;
        writeln!(manifest, "  \"scale\": 0.4444444444444444, \"segment\": 768,")?;
        writeln!(manifest, "  \"concurrency\": {}, \"min_segment_frames\": 24,", self.cpu.concurrency)?;
        writeln!(manifest, "  \"cache_capacity\": {}, \"text_cache_capacity\": {},", self.cpu.cache_capacity, self.cpu.text_cache_capacity)?;
        writeln!(manifest, "  \"segment_file\": \"0000000768.mp4\", \"claim_count\": {},", selected.len())?;
        writeln!(manifest, "  \"historical_allocator_cache_history\": false,")?;
        writeln!(manifest, "  \"claims\": [")?;
        for (index, (worker, claim)) in selected.iter().enumerate() {
            let comma = if index + 1 == selected.len() { "" } else { "," };
            writeln!(manifest, "    {{\"worker\": {worker}, \"segment\": {}, \"frame\": {}, \"last_in_segment\": {}}}{comma}", claim.segment, claim.frame, claim.last_in_segment)?;
        }
        writeln!(manifest, "  ]\n}}")?;
        manifest.sync_all()?;
        // Deliberately no concat_video_files_with_audio, logger final-output success,
        // preview export, decode, padding rewrite, or replacement frame allocation.
        eprintln!("Finished only {} (24 original scheduler claims); claims.json recorded", expected_file.display());
        Ok(())
    }
}

fn run() -> Result<(), Box<dyn Error>> {
    let mut args = std::env::args_os().skip(1);
    let output_folder = args.next().ok_or_else(|| {
        io::Error::new(io::ErrorKind::InvalidInput, "usage: importer_segment <fresh-output-folder>")
    })?;
    if args.next().is_some() {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "expected exactly one output folder argument; no range or settings flags").into());
    }
    if std::env::var("FFRAMES_NUM_THREADS").as_deref() != Ok("1") {
        return Err(io::Error::new(io::ErrorKind::InvalidInput, "CI must supply FFRAMES_NUM_THREADS=1; this fixture does not change the environment").into());
    }
    let output_folder = PathBuf::from(output_folder);
    std::fs::create_dir(&output_folder).map_err(|error| {
        io::Error::new(error.kind(), format!("cannot create fresh output folder {}: {error}", output_folder.display()))
    })?;
    let directory = MediaDirectory::read_folder("media")?;
    let media = directory.process_media_source()?;
    let film = Film::with_style(Style::Anime);
    let options = RenderOptions {
        media: Some(&media),
        default_font: "DM Sans",
        load_system_fonts: false,
        tmp_files_directory: Some(&output_folder),
        scale_resolution: PREVIEW_SCALE,
        frame_range: None,
        video_encoder_options: EncoderOptions {
            preferred_encoder: Some("libx264"),
            color_matrix: YuvMatrix::Bt709,
            codec_params: Some(&[("crf", "18"), ("preset", "fast"), ("tune", "animation")]),
            ..Default::default()
        },
        ..Default::default()
    };
    fframes::render(
        output_folder.join(SEGMENT_FILE),
        &film,
        ImporterSegmentBackend { cpu: CpuRenderingBackend::default() },
        &options,
    )?;
    Ok(())
}

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("Importer segment observation failed: {error}");
            ExitCode::FAILURE
        }
    }
}
