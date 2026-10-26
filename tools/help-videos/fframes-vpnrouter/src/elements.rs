use crate::{Style, graphics::{self, Focus, ScreenshotPose, ease}};
use fframes::{FFramesContext, Svgr};

pub fn app(style: Style, t: f32, delay: f32, x: f32, y: f32, width: f32,
    name: &str, vpn: bool) -> Svgr<'static> {
    let p = style.palette();
    let route = if vpn { "VPN" } else { "Direct" };
    let color = if vpn { p.accent } else { p.second };
    let (fill, ink) = if style == Style::Kinetic { (p.ink, p.bg) } else { (p.panel, p.ink) };
    let name = name.to_owned();
    let left = if style == Style::Terminal { "> " } else { "" };
    let line = format!("{left}{name}");
    let mark = if style == Style::Anime { 6.0 } else { 2.0 };
    style.enter(t, delay, fframes::svgr!(<g>
        {if style == Style::Paper { fframes::svgr!(<rect x={x+7.0} y={y+8.0} width={width} height="78" fill="#C8BDA8" />) } else { Svgr::empty() }}
        <rect x={x} y={y} width={width} height="78" fill={fill} stroke={if style == Style::Kinetic { p.ink } else { color }} stroke-width={mark} rx={if style == Style::Cinema { "12" } else { "0" }} />
        <text x={x+22.0} y={y+49.0} font-family={if matches!(style,Style::Terminal|Style::Blueprint) {"JetBrains Mono"} else {"DM Sans"}} font-size="30" fill={ink}>{line}</text>
        <text x={x+width-104.0} y={y+48.0} font-family="JetBrains Mono" font-size="23" fill={if style == Style::Kinetic { ink } else { color }}>{route}</text>
    </g>))
}

pub fn note(style: Style, t: f32, x: f32, y: f32, first: &str, second: &str) -> Svgr<'static> {
    style.enter(t, 0.9, fframes::svgr!(<g>
        {style.body(x, y, 30.0, first)}
        {style.body(x, y+44.0, 30.0, second)}
    </g>))
}

pub fn ui<'a>(style: Style, t: f32, ctx: &FFramesContext<'a, '_>, asset: &str,
    x: f32, y: f32, scale: f32, focus: Focus) -> Svgr<'a> {
    let pose = ScreenshotPose::at(t, x, y, scale);
    fframes::svgr!(<g>
        {graphics::screenshot(t, ctx, asset, pose, focus, if matches!(focus, Focus::Connection) { "#4ADE80" } else { "#38BDF8" })}
        {style.enter(t, 1.0, style.label(x, y+780.0*scale+34.0, "Actual UI / sample data"))}
    </g>)
}

pub fn line(style: Style, t: f32, delay: f32, from: (f32, f32), to: (f32, f32), vpn: bool) -> Svgr<'static> {
    let p = style.palette();
    let color = if vpn { p.accent } else { p.second };
    let mid = (from.0+to.0)/2.0;
    let length = (to.0-from.0).abs() + (to.1-from.1).abs() + 40.0;
    if style == Style::Terminal || style == Style::Blueprint {
        let points = format!("M {} {} H {mid} V {} H {}", from.0, from.1, to.1, to.0);
        let appear = ease(t, delay, 0.8);
        fframes::svgr!(<path d={points} fill="none" stroke={color} stroke-width="3" stroke-dasharray={format!("{length} {length}")} stroke-dashoffset={length*(1.0-appear)} />)
    } else {
        graphics::route([from, (mid, from.1), (mid, to.1), to], color, t, delay, length)
    }
}

pub fn endpoint(style: Style, t: f32, x: f32, y: f32, vpn: bool) -> Svgr<'static> {
    let p = style.palette();
    let color = if vpn { p.accent } else { p.second };
    let value = if vpn { "VPN" } else { "DIRECT" };
    let color = if style == Style::Kinetic { p.bg } else { color };
    style.enter(t, 0.9, fframes::svgr!(<g>
        <rect x={x} y={y} width="285" height="108" fill={if style==Style::Kinetic {p.ink}else{p.panel}} stroke={color} stroke-width="2" />
        {style.text(x+22.0, y+72.0, if style == Style::Editorial || style == Style::Cinema { 65.0 } else { 46.0 }, color, value)}
    </g>))
}

pub fn mascot<'a>(t: f32, ctx: &FFramesContext<'a, '_>, x: f32, y: f32, size: f32) -> Svgr<'a> {
    graphics::reveal(t, 0.4, fframes::svgr!(<g>
        <rect x={x-12.0} y={y-12.0} width={size+24.0} height={size+24.0} fill="#22252A" />
        {graphics::image(ctx, "mascot.png", x, y, size, size)}
    </g>))
}
