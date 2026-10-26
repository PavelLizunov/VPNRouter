use fframes::{EncoderOptions, MediaDirectory, RenderOptions, YuvMatrix};
use std::{error::Error, path::PathBuf, process::ExitCode};
use vpnrouter_film::{Film, Style};

fn run() -> Result<ExitCode, Box<dyn Error>> {
    let directory = MediaDirectory::read_folder("media")?;
    let media = directory.process_media_source()?;
    let value = std::env::var("FILM_STYLE").unwrap_or_else(|_| "original".to_owned());
    let style = Style::parse(&value).map_err(std::io::Error::other)?;
    let film = Film::with_style(style);
    let temporary = PathBuf::from("render-tmp");
    std::fs::create_dir_all(&temporary)?;
    Ok(fframes::cli::new(
        &film,
        RenderOptions {
            media: Some(&media),
            default_font: "DM Sans",
            load_system_fonts: false,
            tmp_files_directory: Some(&temporary),
            video_encoder_options: EncoderOptions {
                preferred_encoder: Some("libx264"),
                color_matrix: YuvMatrix::Bt709,
                codec_params: Some(&[("crf", "18"), ("preset", "fast"), ("tune", "animation")]),
                ..Default::default()
            },
            ..Default::default()
        },
    )
    .default_output("out/vpnrouter-film-silent.mp4")
    .run())
}

fn main() -> ExitCode {
    match run() {
        Ok(code) => code,
        Err(error) => {
            eprintln!("Cannot prepare the film: {error}");
            ExitCode::FAILURE
        }
    }
}
