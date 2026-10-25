use crate::graphics::*;
use fframes::{FFramesContext, Svgr};

pub fn choose<'a>(t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let spread = smooth(t, 1.4, 1.25);
    let rows = [
        ("Browser", "VPN", true),
        ("Discord", "VPN", true),
        ("Games", "Direct", false),
        ("Banking", "Direct", false),
    ]
    .iter()
    .enumerate()
    .map(|(i, (name, route_name, selected))| {
        let x = 1060.0
            + if *selected {
                -36.0 * spread
            } else {
                36.0 * spread
            };
        let y = 315.0 + i as f32 * 112.0;
        tile(
            x,
            y,
            620.0,
            name,
            route_name,
            *selected,
            ease(t, 0.35 + i as f32 * 0.12, 0.65),
        )
    })
    .collect::<Vec<_>>();
    fframes::svgr!(<g>
        {heading(t, "PER-APPLICATION ROUTING", "Your apps.", "Your routes.")}
        {reveal(t, 0.6, text(116.0, 534.0, 35.0, MUTED, 400, "Choose what uses the VPN."))}
        {reveal(t, 0.8, text(116.0, 583.0, 35.0, MUTED, 400, "Keep the rest direct."))}
        {reveal(t, 0.8, image(ctx, "logo.png", 116.0, 716.0, 98.0, 98.0))}
        {reveal(t, 1.0, text(238.0, 757.0, 27.0, WHITE, 500, "Virtual Penguin Network"))}
        {reveal(t, 1.1, label(238.0, 795.0, "VPNRouter"))}
        {rows}
        {reveal(t, 1.25, label(1060.0, 847.0, "An example split, not a network trace"))}
    </g>)
}

pub fn split<'a>(t: f32, _ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let items = [
        ("Browser", true, 330.0),
        ("Discord", true, 444.0),
        ("Games", false, 650.0),
        ("Banking", false, 764.0),
    ];
    let routes = items
        .iter()
        .enumerate()
        .map(|(i, (_, selected, y))| {
            let target_y = if *selected { 440.0 } else { 733.0 };
            route(
                [
                    (670.0, y + 43.0),
                    (1010.0, y + 43.0),
                    (1010.0, target_y),
                    (1395.0, target_y),
                ],
                if *selected { CYAN } else { MUTED },
                t,
                0.65 + i as f32 * 0.12,
                900.0,
            )
        })
        .collect::<Vec<_>>();
    let tiles = items
        .iter()
        .enumerate()
        .map(|(i, (name, selected, y))| {
            tile(
                112.0,
                *y,
                558.0,
                name,
                if *selected { "VPN" } else { "Direct" },
                *selected,
                ease(t, 0.15 + i as f32 * 0.10, 0.7),
            )
        })
        .collect::<Vec<_>>();
    fframes::svgr!(<g>
        {reveal(t, 0.1, label(112.0, 205.0, "ONE DEVICE / TWO ROUTES"))}
        {reveal(t, 0.22, text(112.0, 287.0, 70.0, WHITE, 500, "Only the apps you choose."))}
        {routes}
        {tiles}
        {endpoint(1395.0, 370.0, "VPN", "Selected applications", CYAN, t, 0.7)}
        {endpoint(1395.0, 663.0, "Direct", "Everything else", MUTED, t, 0.95)}
        {reveal(t, 1.25, text(850.0, 572.0, 26.0, MUTED, 400, "Split tunnel"))}
        {reveal(t, 1.5, label(112.0, 916.0, "Illustrative include mode; configured exceptions still apply"))}
    </g>)
}

pub fn applications<'a>(t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let selection = ease(t, 1.35, 0.5);
    let tabs = [
        ("Browsers", 552.0),
        ("Messengers", 629.0),
        ("Work tools", 706.0),
    ];
    let rows = tabs.iter().enumerate().map(|(i, (name, y))| {
        reveal(t, 0.75 + i as f32*0.20, fframes::svgr!(<g>
            <rect x="116" y={y-31.0} width="35" height="35" rx="8" fill={CYAN} opacity={selection} />
            <rect x="116" y={y-31.0} width="35" height="35" rx="8" fill="none" stroke={CYAN} stroke-width="1.5" />
            <path d={format!("M 124 {} l 6 6 l 13 -14", y-14.0)} stroke={BG} stroke-width="3" stroke-linecap="round" stroke-linejoin="round" fill="none" opacity={selection} />
            {text(174.0, *y, 35.0, WHITE, 400, name)}
        </g>))
    }).collect::<Vec<_>>();
    let focus = ease(t, 2.0, 0.6) * (1.0 - ease(t, 5.8, 0.4));
    fframes::svgr!(<g>
        {heading(t, "SET THE RULE", "Pick the apps.", "Keep control.")}
        {rows}
        {reveal(t, 1.5, text(116.0, 819.0, 29.0, MUTED, 400, "No proxy setup inside each app."))}
        {screenshot(t, ctx, "applications.png", 1170.0, 166.0, 0.96)}
        <rect x="1310" y="322" width="382" height="44" rx="8" fill="none" stroke={CYAN} stroke-width="3" opacity={focus} />
        {reveal(t, 1.0, label(1170.0, 943.0, "Actual UI / sample data"))}
    </g>)
}

pub fn connect<'a>(t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let ring = 2.0 + (t * 2.0).sin() * 1.4;
    let ring_alpha = ease(t, 0.8, 0.7) * 0.8;
    let lines = [
        ("01", "Bring your server or subscription.", 535.0),
        ("02", "Choose a routing mode.", 623.0),
        ("03", "Connect from one screen.", 711.0),
    ]
    .iter()
    .enumerate()
    .map(|(i, (number, message, y))| {
        reveal(
            t,
            0.7 + i as f32 * 0.35,
            fframes::svgr!(<g>
        {label(116.0, *y, number)}
        {text(182.0, *y, 31.0, WHITE, 400, message)}
        <rect x="116" y={y+30.0} width="800" height="1" fill={BORDER} />
    </g>),
        )
    })
    .collect::<Vec<_>>();
    fframes::svgr!(<g>
        {heading(t, "YOUR CONNECTION", "One screen.", "Two paths.")}
        {lines}
        {reveal(t, 1.6, text(116.0, 851.0, 29.0, MUTED, 400, "Your infrastructure. Your choice of apps."))}
        {screenshot(t, ctx, "home-connected.png", 1170.0, 166.0, 0.96)}
        <circle cx="1439" cy="313" r={69.0+ring} fill="none" stroke={GREEN} stroke-width="2" opacity={ring_alpha} />
        {reveal(t, 1.0, label(1170.0, 943.0, "Actual UI / sample data"))}
    </g>)
}

pub fn platforms<'a>(t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let platforms = ["Windows", "macOS", "Linux", "Android"];
    let rows = platforms
        .iter()
        .enumerate()
        .map(|(i, name)| {
            let p = ease(t, 0.4 + i as f32 * 0.12, 0.7);
            let y = 470.0 + i as f32 * 96.0;
            let x = 120.0 + 38.0 * (1.0 - p);
            fframes::svgr!(<g opacity={p}>
            {text(x, y, 58.0, if i == 3 { CYAN } else { WHITE }, 400, name)}
            <rect x={x} y={y+23.0} width="740" height="1" fill={BORDER} />
            {label(800.0, y-8.0, &format!("0{}", i+1))}
        </g>)
        })
        .collect::<Vec<_>>();
    let turn = smooth(t, 0.5, 4.0);
    fframes::svgr!(<g>
        {reveal(t, 0.1, label(112.0, 212.0, "VPNROUTER"))}
        {reveal(t, 0.2, text(112.0, 324.0, 87.0, WHITE, 500, "Choose your platform."))}
        {rows}
        <g transform={format!("translate({}, {})", 18.0*turn, -8.0*turn)}>
            {reveal(t, 0.4, image(ctx, "mascot.png", 1140.0, 400.0, 450.0, 450.0))}
        </g>
        {reveal(t, 0.75, text(1180.0, 887.0, 26.0, MUTED, 400, "Built with Avalonia + sing-box"))}
    </g>)
}

pub fn repository<'a>(t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let draw = ease(t, 0.6, 1.0);
    let images = [
        ("applications.png", 1445.0, 318.0, 0.48),
        ("home-connected.png", 1160.0, 276.0, 0.61),
    ].iter().enumerate().map(|(i, (name, x, y, scale))| {
        let p = ease(t, 0.35+i as f32*0.16, 0.95);
        fframes::svgr!(<g opacity={p} transform={format!("translate({},0)", 80.0*(1.0-p))}>
            <rect x={x-5.0} y={y-5.0} width={560.0*scale+10.0} height={780.0*scale+10.0} rx="12" fill="#283347" />
            {image(ctx, name, *x, *y, 560.0*scale, 780.0*scale)}
        </g>)
    }).collect::<Vec<_>>();
    fframes::svgr!(<g>
        {reveal(t, 0.1, label(112.0, 210.0, "VIRTUAL PENGUIN NETWORK"))}
        {reveal(t, 0.2, text(112.0, 363.0, 137.0, WHITE, 500, "VPNRouter"))}
        {reveal(t, 0.4, text(118.0, 451.0, 42.0, CYAN, 400, "Choose what goes through the VPN."))}
        <path d="M 120 529 L 965 529" fill="none" stroke={CYAN} stroke-width="3" stroke-dasharray="845 845" stroke-dashoffset={845.0*(1.0-draw)} />
        {reveal(t, 0.85, label(118.0, 593.0, "SOURCE + DOWNLOADS"))}
        {reveal(t, 1.0, text(118.0, 658.0, 37.0, WHITE, 500, "github.com/PavelLizunov/VPNRouter"))}
        {reveal(t, 1.15, text(118.0, 727.0, 29.0, MUTED, 400, "Windows / macOS / Linux / Android"))}
        {reveal(t, 1.4, text(118.0, 859.0, 25.0, MUTED, 400, "GPL-3.0-or-later   /   Pavel Lizunov"))}
        {images}
        {reveal(t, 1.25, label(1170.0, 897.0, "UI shown with sample data"))}
    </g>)
}
