use fframes::{
    FFramesContext, Svgr,
    animation::{AnimationRuntime, Easing},
};
use std::sync::OnceLock;

pub const BG: &str = "#080C15";
pub const PANEL: &str = "#141C2A";
pub const BORDER: &str = "#2C394C";
pub const WHITE: &str = "#F4F7FB";
pub const MUTED: &str = "#A6B5C9";
pub const CYAN: &str = "#38BDF8";
pub const GREEN: &str = "#4ADE80";

pub fn ease(time: f32, start: f32, duration: f32) -> f32 {
    static CURVE: OnceLock<AnimationRuntime> = OnceLock::new();
    let value = ((time - start) / duration).clamp(0.0, 1.0);
    CURVE
        .get_or_init(|| AnimationRuntime::new(1.0, &Easing::EaseOut))
        .solve(&value)
}

pub fn smooth(time: f32, start: f32, duration: f32) -> f32 {
    static CURVE: OnceLock<AnimationRuntime> = OnceLock::new();
    let value = ((time - start) / duration).clamp(0.0, 1.0);
    CURVE
        .get_or_init(|| AnimationRuntime::new(1.0, &Easing::EaseInOut))
        .solve(&value)
}

pub fn text(x: f32, y: f32, size: f32, color: &str, weight: u16, value: &str) -> Svgr<'static> {
    let color = color.to_owned();
    let value = value.to_owned();
    fframes::svgr!(
        <text x={x} y={y} font-family="DM Sans" font-size={size} font-weight={weight.min(500)} fill={color}>
            {value}
        </text>
    )
}

pub fn label(x: f32, y: f32, value: &str) -> Svgr<'static> {
    let value = value.to_owned();
    fframes::svgr!(
        <text x={x} y={y} font-family="JetBrains Mono" font-size="20" font-weight="400" fill={MUTED}>
            {value}
        </text>
    )
}

pub fn image<'a>(
    ctx: &FFramesContext<'a, '_>,
    name: &str,
    x: f32,
    y: f32,
    width: f32,
    height: f32,
) -> Svgr<'a> {
    let Some(asset) = ctx.get_image(name) else {
        return Svgr::empty();
    };
    fframes::svgr!(<image href={asset.href()} x={x} y={y} width={width} height={height} preserveAspectRatio="xMidYMid meet" />)
}

pub fn reveal<'a>(time: f32, delay: f32, content: Svgr<'a>) -> Svgr<'a> {
    let p = ease(time, delay, 0.65);
    fframes::svgr!(<g opacity={p} transform={format!("translate(0,{})", 26.0 * (1.0 - p))}>{content}</g>)
}

pub fn heading(time: f32, eyebrow: &str, first: &str, second: &str) -> Svgr<'static> {
    fframes::svgr!(<g>
        {reveal(time, 0.12, label(112.0, 210.0, eyebrow))}
        {reveal(time, 0.20, text(112.0, 330.0, 100.0, WHITE, 500, first))}
        {reveal(time, 0.32, text(112.0, 440.0, 100.0, CYAN, 500, second))}
    </g>)
}

pub fn tile(
    x: f32,
    y: f32,
    width: f32,
    name: &str,
    route: &str,
    selected: bool,
    progress: f32,
) -> Svgr<'static> {
    let name = name.to_owned();
    let route = route.to_owned();
    let color = if selected { CYAN } else { MUTED };
    fframes::svgr!(<g opacity={progress} transform={format!("translate({},0)", -35.0 * (1.0 - progress))}>
        <rect x={x} y={y} width={width} height="86" rx="14" fill={PANEL} stroke={if selected { "#28546A" } else { BORDER }} stroke-width="1.5" />
        <rect x={x + 23.0} y={y + 29.0} width="27" height="27" rx="6" fill={if selected { CYAN } else { "#283347" }} />
        {if selected { fframes::svgr!(<path d={format!("M {} {} l 6 6 l 11 -13", x+29.0, y+42.0)} fill="none" stroke={BG} stroke-width="3" stroke-linecap="round" stroke-linejoin="round" />) } else { Svgr::empty() }}
        {text(x + 72.0, y + 55.0, 30.0, WHITE, 500, &name)}
        {text(x + width - 115.0, y + 54.0, 23.0, color, 500, &route)}
    </g>)
}

pub type Curve = [(f32, f32); 4];

pub fn curve_point(curve: Curve, p: f32) -> (f32, f32) {
    let q = 1.0 - p;
    let x = q * q * q * curve[0].0
        + 3.0 * q * q * p * curve[1].0
        + 3.0 * q * p * p * curve[2].0
        + p * p * p * curve[3].0;
    let y = q * q * q * curve[0].1
        + 3.0 * q * q * p * curve[1].1
        + 3.0 * q * p * p * curve[2].1
        + p * p * p * curve[3].1;
    (x, y)
}

pub fn route(curve: Curve, color: &str, time: f32, delay: f32, length: f32) -> Svgr<'static> {
    let color = color.to_owned();
    let p = ease(time, delay, 1.05);
    let path = format!(
        "M {} {} C {} {}, {} {}, {} {}",
        curve[0].0,
        curve[0].1,
        curve[1].0,
        curve[1].1,
        curve[2].0,
        curve[2].1,
        curve[3].0,
        curve[3].1
    );
    let packets = (0..4).map(|i| {
        let phase = ((time - delay - 0.55) * 0.22 - i as f32 * 0.24).rem_euclid(1.0);
        let (x, y) = curve_point(curve, phase);
        let visible = ease(time, delay + 0.8 + i as f32 * 0.15, 0.4);
        let edge = (phase * 10.0).min((1.0-phase)*10.0).clamp(0.0, 1.0);
        fframes::svgr!(<circle cx={x} cy={y} r="5" fill={color.clone()} opacity={visible * edge} />)
    }).collect::<Vec<_>>();
    fframes::svgr!(<g>
        <path d={path} fill="none" stroke={color} stroke-width="3" stroke-opacity="0.65" stroke-linecap="round" stroke-dasharray={format!("{length} {length}")} stroke-dashoffset={length * (1.0-p)} />
        {packets}
    </g>)
}

pub fn endpoint(
    x: f32,
    y: f32,
    title: &str,
    subtitle: &str,
    color: &str,
    time: f32,
    delay: f32,
) -> Svgr<'static> {
    let color = color.to_owned();
    reveal(
        time,
        delay,
        fframes::svgr!(<g>
        <rect x={x} y={y} width="300" height="140" rx="18" fill={PANEL} stroke={color.clone()} stroke-opacity="0.65" stroke-width="1.5" />
        <circle cx={x+29.0} cy={y+38.0} r="5" fill={color.clone()} />
        {text(x+48.0, y+48.0, 34.0, &color, 500, title)}
        {text(x+28.0, y+98.0, 23.0, MUTED, 400, subtitle)}
    </g>),
    )
}

#[derive(Debug, Clone, Copy)]
pub struct ScreenshotPose {
    pub x: f32,
    pub y: f32,
    pub scale: f32,
    pub alpha: f32,
}

impl ScreenshotPose {
    pub fn at(time: f32, x: f32, y: f32, scale: f32) -> Self {
        let alpha = ease(time, 0.35, 0.95);
        Self {
            x,
            y: y + (1.0 - alpha) * 44.0 - smooth(time, 1.0, 6.0) * 8.0,
            scale,
            alpha,
        }
    }

    pub fn source_point(self, x: f32, y: f32) -> (f32, f32) {
        (self.x + x * self.scale, self.y + y * self.scale)
    }

    pub fn transform(self) -> String {
        format!("translate({},{}) scale({})", self.x, self.y, self.scale)
    }
}

#[derive(Clone, Copy, Debug)]
pub enum Focus {
    None,
    Application,
    Connection,
}

pub fn focus(time: f32, kind: Focus, color: &str) -> Svgr<'static> {
    let color = color.to_owned();
    match kind {
        Focus::None => Svgr::empty(),
        Focus::Application => {
            let alpha = ease(time, 2.0, 0.6) * (1.0 - ease(time, 5.8, 0.4));
            fframes::svgr!(<rect id="application-focus" x="147" y="171" width="402" height="41" rx="9" fill="none" stroke={color} stroke-width="3" opacity={alpha} />)
        }
        Focus::Connection => {
            let radius = 66.0 + (time * 2.0).sin() * 1.4;
            fframes::svgr!(<circle id="connection-focus" cx="280" cy="151" r={radius} fill="none" stroke={color} stroke-width="2" opacity={ease(time, 0.8, 0.7) * 0.8} />)
        }
    }
}

pub fn screenshot<'a>(
    time: f32,
    ctx: &FFramesContext<'a, '_>,
    asset: &str,
    pose: ScreenshotPose,
    annotation: Focus,
    color: &str,
) -> Svgr<'a> {
    // One source-coordinate group owns the image AND its annotation, including
    // entrance, drift and scale. Never add a separately positioned focus overlay.
    fframes::svgr!(<g id={format!("registered-screenshot-{asset}")} opacity={pose.alpha} transform={pose.transform()}>
        <rect x="-7" y="-7" width="574" height="794" rx="17" fill="#202C3C" />
        {image(ctx, asset, 0.0, 0.0, 560.0, 780.0)}
        {focus(time, annotation, color)}
    </g>)
}
