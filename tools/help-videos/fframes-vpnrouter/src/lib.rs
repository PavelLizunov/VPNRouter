#[cfg(test)]
mod checks;
mod graphics;
mod shots;
mod styles;
mod variants;
mod elements;

pub use styles::Style;

use fframes::{AudioMap, Duration, FFramesContext, Frame, Overlap, Scene, Scenes, Svgr, Video};
use graphics::{BG, MUTED, WHITE, text};

pub const FPS: usize = 30;
pub const SECONDS: f32 = 36.0;
pub const SHOTS: [(&str, f32); 6] = [
    ("Choose", 5.0),
    ("Split", 7.0),
    ("Applications", 7.0),
    ("Connect", 6.0),
    ("Platforms", 5.0),
    ("Choice", 6.0),
];

#[derive(Debug)]
pub struct Shot {
    index: usize,
    style: Style,
}

#[derive(Debug)]
pub struct Film {
    shots: [Shot; 6],
    style: Style,
}

impl Default for Film {
    fn default() -> Self {
        Self::new()
    }
}

impl Film {
    pub fn new() -> Self {
        Self::with_style(Style::Original)
    }

    pub fn with_style(style: Style) -> Self {
        Self {
            shots: std::array::from_fn(|index| Shot { index, style }),
            style,
        }
    }
}

impl Scene for Shot {
    fn duration(&self) -> Duration<'_> {
        Duration::Seconds(SHOTS[self.index].1)
    }

    fn name(&self) -> &'static str {
        SHOTS[self.index].0
    }

    fn overlap(&self) -> Overlap {
        if self.index == 0 {
            Overlap::None
        } else {
            Overlap::Previous(0.4)
        }
    }

    fn render_frame<'a>(&'a self, frame: Frame, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
        let time = frame.seconds();
        let alpha = if self.index == 0 {
            1.0
        } else {
            (time / 0.4).clamp(0.0, 1.0)
        };
        let content = if self.style == Style::Original {
            match self.index {
                0 => shots::choose(time, ctx),
                1 => shots::split(time, ctx),
                2 => shots::applications(time, ctx),
                3 => shots::connect(time, ctx),
                4 => shots::platforms(time, ctx),
                _ => shots::finish(time, ctx),
            }
        } else {
            variants::render(self.style, self.index, time, ctx)
        };
        fframes::svgr!(
            <g opacity={alpha}>
                <rect width="1920" height="1080" fill={self.style.palette().bg} />
                {content}
            </g>
        )
    }
}

impl Video for Film {
    const FPS: usize = crate::FPS;
    const WIDTH: usize = 1920;
    const HEIGHT: usize = 1080;

    fn duration(&self) -> Duration<'_> {
        Duration::Auto
    }

    fn audio(&self) -> AudioMap<'_> {
        // Soundtrack is muxed by export.sh; the fframes visual master stays reusable.
        AudioMap::none()
    }

    fn define_scenes(&self) -> Scenes<'_> {
        Scenes::from(
            self.shots
                .iter()
                .map(|s| s as &dyn Scene)
                .collect::<Vec<_>>(),
        )
    }

    fn render_frame<'a>(&'a self, frame: Frame, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
        let time = frame.seconds();
        let progress = (time / SECONDS).clamp(0.0, 1.0);
        if self.style != Style::Original {
            let p = self.style.palette();
            return fframes::svgr!(
                <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1920 1080" width={ctx.current_video_size.width} height={ctx.current_video_size.height}>
                    <rect width="1920" height="1080" fill={p.bg} />
                    {ctx.render_scenes(&frame)}
                    <rect x="112" y="1005" width="1696" height="2" fill={p.muted} opacity="0.3" />
                    <rect x="112" y="1005" width={(progress*1696.0).max(0.01)} height="3" fill={p.accent} />
                    {self.style.label(112.0, 1050.0, "PER-APPLICATION ROUTING")}
                    {self.style.label(1500.0, 1050.0, &format!("{:02} / 36 SEC", time as usize))}
                </svg>
            );
        }
        let mut start = 0.0;
        let chapter_marks = SHOTS
            .iter()
            .enumerate()
            .map(|(i, (name, duration))| {
                let x = 112.0 + start / SECONDS * 1696.0;
                let width = *duration / SECONDS * 1696.0 - 8.0;
                let active = time >= start;
                start += duration;
                fframes::svgr!(<g>
                    <rect x={x} y="1005" width={width} height="2" fill={if active { "#38BDF8" } else { "#293343" }} />
                    {text(x, 1040.0, 19.0, if active { WHITE } else { MUTED }, 500, &format!("{:02}  {}", i + 1, name))}
                </g>)
            })
            .collect::<Vec<_>>();
        fframes::svgr!(
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1920 1080" width={ctx.current_video_size.width} height={ctx.current_video_size.height}>
                <rect width="1920" height="1080" fill={BG} />
                {ctx.render_scenes(&frame)}
                {graphics::image(ctx, "logo.png", 112.0, 49.0, 40.0, 40.0)}
                {text(167.0, 78.0, 26.0, WHITE, 500, "Per-application routing")}
                <rect x="112" y="118" width="1696" height="1" fill="#263040" />
                {chapter_marks}
                <rect x="112" y="974" width={(progress * 1696.0).max(0.01)} height="3" fill="#38BDF8" />
            </svg>
        )
    }
}
