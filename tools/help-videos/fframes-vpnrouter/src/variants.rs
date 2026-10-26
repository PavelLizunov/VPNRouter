use crate::{Style, elements as el, graphics::{self, Focus, ScreenshotPose, ease, smooth}};
use fframes::{FFramesContext, Svgr};

const APPS: [(&str, bool); 4] = [("Browser", true), ("Discord", true), ("Games", false), ("Banking", false)];
const COPY: [(&str, &str); 6] = [
    ("Choose your", "own routes."),
    ("One device.", "Two paths."),
    ("Pick the apps.", "Keep control."),
    ("Your server.", "Your choice."),
    ("Choose your", "platform."),
    ("Your apps.", "Your rules."),
];

pub fn render<'a>(style: Style, index: usize, t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let content = match index {
        0 => intro(style, t, ctx),
        1 => split(style, t),
        2 | 3 => interface(style, index, t, ctx),
        4 => platforms(style, t, ctx),
        _ => finish(style, t, ctx),
    };
    fframes::svgr!(<g>{style.background(t, index)}{content}</g>)
}

fn title(style: Style, t: f32, index: usize, x: f32, y: f32, size: f32) -> Svgr<'static> {
    let (first, second) = COPY[index];
    let p = style.palette();
    fframes::svgr!(<g>
        {style.enter(t, 0.12, style.text(x, y, size, p.ink, first))}
        {style.enter(t, 0.28, style.text(x, y+size*1.04, size, p.accent, second))}
    </g>)
}

fn intro<'a>(style: Style, t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let p = style.palette();
    let apps = match style {
        Style::Swiss => APPS.iter().enumerate().map(|(i,(name,vpn))|
            el::app(style,t,0.5+i as f32*0.14,1070.0,365.0+i as f32*114.0,650.0,name,*vpn)).collect::<Vec<_>>(),
        Style::Blueprint => APPS.iter().enumerate().map(|(i,(name,vpn))|
            el::app(style,t,0.5+i as f32*0.14,1140.0,310.0+i as f32*117.0,570.0,name,*vpn)).collect(),
        Style::Anime => APPS.iter().enumerate().map(|(i,(name,vpn))|
            el::app(style,t,0.6+i as f32*0.14,125.0+i as f32*410.0,790.0,365.0,name,*vpn)).collect(),
        Style::Editorial => APPS.iter().enumerate().map(|(i,(name,vpn))|
            el::app(style,t,0.6+i as f32*0.14,112.0,515.0+i as f32*94.0,760.0,name,*vpn)).collect(),
        Style::Terminal => APPS.iter().enumerate().map(|(i,(name,vpn))|
            el::app(style,t,0.6+i as f32*0.2,1055.0,345.0+i as f32*112.0,650.0,name,*vpn)).collect(),
        Style::Paper => APPS.iter().enumerate().map(|(i,(name,vpn))|
            el::app(style,t,0.65+i as f32*0.15,1030.0+if i%2==0 {0.0} else {40.0},315.0+i as f32*123.0,625.0,name,*vpn)).collect(),
        Style::Kinetic => APPS.iter().enumerate().map(|(i,(name,vpn))|
            el::app(style,t,0.55+i as f32*0.13,1180.0,310.0+i as f32*124.0,560.0,name,*vpn)).collect(),
        _ => APPS.iter().enumerate().map(|(i,(name,vpn))|
            el::app(style,t,0.9+i as f32*0.2,180.0+i as f32*407.0,690.0,365.0,name,*vpn)).collect(),
    };
    let words = match style {
        Style::Anime => fframes::svgr!(<g>
            {title(style,t,0,135.0,330.0,128.0)}
            {el::note(style,t,140.0,565.0,"Choose what uses the VPN.","Keep the rest direct.")}
            <path d="M 1120 245 L 1710 245 L 1650 702 L 1060 702 Z" fill={p.accent} />
            {el::mascot(t,ctx,1210.0,290.0,350.0)}
            {style.enter(t,0.5,style.text(1125.0,730.0,55.0,p.ink,"YOU CHOOSE THE PATH"))}
        </g>),
        Style::Swiss => fframes::svgr!(<g>
            {style.enter(t,0.1,style.text(110.0,330.0,160.0,p.accent,"01"))}
            {title(style,t,0,112.0,520.0,108.0)}
            {el::note(style,t,118.0,780.0,"Choose what uses the VPN.","Keep the rest direct.")}
        </g>),
        Style::Blueprint => fframes::svgr!(<g>
            {style.label(145.0,220.0,"FIG. 01 / APPLICATION RULES")}
            {title(style,t,0,150.0,410.0,72.0)}
            {el::note(style,t,150.0,640.0,"Choose what uses VPN.","Keep the rest direct.")}
            <path d="M 150 738 H 920 M 150 728 V 748 M 920 728 V 748" stroke={p.accent} stroke-width="2" />
            {style.label(150.0,790.0,"RULE SCOPE: SELECTED PROCESSES")}
        </g>),
        Style::Editorial => fframes::svgr!(<g>
            {style.enter(t,0.15,style.text(112.0,312.0,130.0,p.ink,"A route for every app."))}
            {style.enter(t,0.4,style.body(118.0,415.0,32.0,"Choose what uses VPN. Keep the rest direct."))}
            {el::mascot(t,ctx,1155.0,370.0,460.0)}
            {style.label(1095.0,878.0,"01. A CHOICE, NOT A BLANKET RULE")}
        </g>),
        Style::Terminal => fframes::svgr!(<g>
            {style.enter(t,0.1,style.body(145.0,270.0,28.0,"$ route-plan --by-application"))}
            {title(style,t,0,145.0,410.0,65.0)}
            {el::note(style,t,145.0,640.0,"include: Browser, Discord","direct: Games, Banking")}
            {style.label(145.0,835.0,"ILLUSTRATION / NOT LIVE OUTPUT")}
        </g>),
        Style::Paper => fframes::svgr!(<g>
            {title(style,t,0,145.0,390.0,112.0)}
            {el::note(style,t,150.0,690.0,"Put selected apps on VPN.","Leave other apps direct.")}
            {style.enter(t,0.55,style.label(1120.0,235.0,"SORT YOUR APPLICATIONS"))}
        </g>),
        Style::Kinetic => fframes::svgr!(<g>
            {style.enter(t,0.1,style.text(112.0,394.0,210.0,p.ink,"YOUR APPS."))}
            {style.enter(t,0.3,style.text(112.0,620.0,194.0,p.ink,"YOUR ROUTES."))}
            {el::note(style,t,118.0,782.0,"Choose what uses VPN.","Keep the rest direct.")}
        </g>),
        _ => fframes::svgr!(<g>
            {title(style,t,0,530.0,350.0,108.0)}
            {style.enter(t,0.8,style.body(570.0,595.0,32.0,"Choose what uses the VPN. Keep the rest direct."))}
        </g>),
    };
    fframes::svgr!(<g>{words}{apps}{style.enter(t,1.4,style.label(112.0,968.0,"Example choices / configured exceptions still apply"))}</g>)
}

fn split(style: Style, t: f32) -> Svgr<'static> {
    let p = style.palette();
    // Three route compositions: vertical lanes, a top-to-bottom diagram and a
    // side-to-side circuit. They share facts, never a finished recolored frame.
    match style {
        Style::Anime | Style::Paper | Style::Kinetic => {
            let kinetic = style == Style::Kinetic;
            let left_x = if kinetic { 135.0 } else { 150.0 };
            let right_x = if kinetic { 1170.0 } else { 1140.0 };
            let rows = APPS.iter().enumerate().map(|(i,(name,vpn))| {
                let x = if *vpn { left_x } else { right_x };
                let y = 410.0 + (i%2) as f32*126.0;
                el::app(style,t,0.4+i as f32*0.12,x,y,570.0,name,*vpn)
            }).collect::<Vec<_>>();
            fframes::svgr!(<g>
                {style.enter(t,0.1,style.text(135.0,310.0,if kinetic {105.0}else{92.0},p.ink,"ONE DEVICE. TWO PATHS."))}
                {rows}
                {el::line(style,t,0.85,(left_x+280.0,625.0),(left_x+280.0,748.0),true)}
                {el::line(style,t,0.95,(right_x+280.0,625.0),(right_x+280.0,748.0),false)}
                {el::endpoint(style,t,left_x+135.0,748.0,true)}
                {el::endpoint(style,t,right_x+135.0,748.0,false)}
                {style.enter(t,1.1,style.body(800.0,578.0,28.0,"SPLIT"))}
                {style.label(135.0,968.0,"Include-mode example / not a packet trace")}
            </g>)
        }
        Style::Swiss | Style::Cinema => {
            let rows = APPS.iter().enumerate().map(|(i,(name,vpn))|
                el::app(style,t,0.4+i as f32*0.1,160.0+i as f32*407.0,435.0,350.0,name,*vpn)).collect::<Vec<_>>();
            let lines = APPS.iter().enumerate().map(|(i,(_,vpn))|
                el::line(style,t,0.8+i as f32*0.1,(335.0+i as f32*407.0,513.0),(if *vpn {520.0}else{1340.0},748.0),*vpn)).collect::<Vec<_>>();
            fframes::svgr!(<g>
                {style.enter(t,0.1,style.text(160.0,320.0,if style==Style::Swiss {116.0}else{105.0},p.ink,"One device. Two paths."))}
                {lines}{rows}
                {el::endpoint(style,t,377.0,748.0,true)}
                {el::endpoint(style,t,1197.0,748.0,false)}
                {style.label(160.0,968.0,"Illustrative include mode / not live traffic")}
            </g>)
        }
        _ => {
            let rows = APPS.iter().enumerate().map(|(i,(name,vpn))|
                el::app(style,t,0.3+i as f32*0.12,145.0,380.0+i as f32*118.0,515.0,name,*vpn)).collect::<Vec<_>>();
            let lines = APPS.iter().enumerate().map(|(i,(_,vpn))|
                el::line(style,t,0.8+i as f32*0.1,(660.0,419.0+i as f32*118.0),(1370.0,if *vpn {470.0}else{760.0}),*vpn)).collect::<Vec<_>>();
            fframes::svgr!(<g>
                {style.enter(t,0.1,style.text(145.0,292.0,if style==Style::Editorial {110.0}else{64.0},p.ink,"One device. Two paths."))}
                {lines}{rows}
                {el::endpoint(style,t,1370.0,416.0,true)}
                {el::endpoint(style,t,1370.0,706.0,false)}
                {style.label(145.0,968.0,"Selected apps via VPN / configured exceptions apply")}
            </g>)
        }
    }
}

fn interface<'a>(style: Style, index: usize, t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let connection = index == 3;
    let asset = if connection { "home-connected.png" } else { "applications.png" };
    let focus = if connection { Focus::Connection } else { Focus::Application };
    let p = style.palette();
    let (tx,ty,size,ux,uy,scale,nx,ny) = match style {
        Style::Anime => (135.0,325.0,108.0,1230.0,205.0,0.84,145.0,610.0),
        Style::Swiss => (112.0,345.0,102.0,1170.0,180.0,0.89,118.0,650.0),
        Style::Blueprint => (150.0,322.0,65.0,1130.0,180.0,0.90,150.0,580.0),
        Style::Editorial => (112.0,355.0,120.0,1165.0,180.0,0.89,118.0,640.0),
        Style::Terminal => (145.0,345.0,64.0,1180.0,210.0,0.83,145.0,580.0),
        Style::Paper => (145.0,345.0,101.0,1170.0,190.0,0.88,155.0,620.0),
        Style::Kinetic => (112.0,355.0,155.0,1180.0,195.0,0.87,118.0,640.0),
        _ => (250.0,350.0,110.0,1135.0,195.0,0.86,255.0,645.0),
    };
    let notes = if connection { ["01  Bring your server.", "02  Choose a routing mode.", "03  Connect from one screen."] }
        else { ["Browsers", "Messengers", "Work tools"] };
    let details = notes.iter().enumerate().map(|(i,value)| {
        let y = ny+i as f32*67.0;
        style.enter(t,0.8+i as f32*0.25,fframes::svgr!(<g>
            {if !connection { fframes::svgr!(<rect x={nx} y={y-26.0} width="27" height="27" fill={p.accent} />) } else { Svgr::empty() }}
            {style.body(nx+if connection {0.0}else{46.0},y,if style==Style::Terminal {27.0}else{31.0},value)}
        </g>))
    }).collect::<Vec<_>>();
    fframes::svgr!(<g>
        {title(style,t,index,tx,ty,size)}
        {details}
        {el::ui(style,t,ctx,asset,ux,uy,scale,focus)}
        {style.enter(t,1.6,style.label(tx,899.0,if connection {"Your infrastructure / your app choices"} else {"No proxy setup inside each app"}))}
    </g>)
}

fn platforms<'a>(style: Style, t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let p = style.palette();
    let names = ["Windows", "macOS", "Linux", "Android"];
    let (tx,ty,size) = match style {
        Style::Anime => (135.0,285.0,90.0),
        Style::Swiss => (112.0,325.0,105.0),
        Style::Blueprint => (150.0,290.0,65.0),
        Style::Terminal => (145.0,310.0,65.0),
        Style::Kinetic => (112.0,290.0,155.0),
        _ => (135.0,315.0,108.0),
    };
    let grid = matches!(style,Style::Anime|Style::Paper|Style::Cinema);
    let rows = names.iter().enumerate().map(|(i,name)| {
        let (x,y) = if grid {(150.0+(i%2) as f32*470.0,630.0+(i/2) as f32*170.0)}
            else {(tx+8.0,580.0+i as f32*83.0)};
        style.enter(t,0.45+i as f32*0.18,fframes::svgr!(<g>
            {style.text(x,y,if style==Style::Terminal || style==Style::Blueprint {48.0}else{61.0},if i==3 {p.accent}else{p.ink},name)}
            <path d={format!("M {x} {} h {}",y+22.0,if grid {365}else{710})} stroke={p.muted} stroke-width="1" />
        </g>))
    }).collect::<Vec<_>>();
    // The original mascot is placed as an image, not redrawn or recolored.
    let turn = smooth(t,0.5,4.0);
    fframes::svgr!(<g>
        {title(style,t,4,tx,ty,size)}
        {rows}
        <g transform={format!("translate({},0)",12.0*turn)}>
            {el::mascot(t,ctx,1210.0,420.0,400.0)}
        </g>
        {style.enter(t,1.2,style.label(112.0,968.0,"Built with Avalonia + sing-box"))}
    </g>)
}

fn finish<'a>(style: Style, t: f32, ctx: &FFramesContext<'a, '_>) -> Svgr<'a> {
    let p = style.palette();
    let (tx,ty,size) = match style {
        Style::Anime => (135.0,345.0,145.0),
        Style::Swiss => (112.0,365.0,150.0),
        Style::Blueprint => (150.0,345.0,88.0),
        Style::Terminal => (145.0,345.0,88.0),
        Style::Kinetic => (112.0,395.0,205.0),
        _ => (135.0,365.0,145.0),
    };
    let small_ui = matches!(style,Style::Blueprint|Style::Swiss|Style::Terminal|Style::Cinema);
    let art = if small_ui {
        fframes::svgr!(<g>
            {graphics::screenshot(t,ctx,"applications.png",ScreenshotPose::at(t,1470.0,295.0,0.43),Focus::None,p.accent)}
            {graphics::screenshot(t,ctx,"home-connected.png",ScreenshotPose::at(t,1115.0,210.0,0.62),Focus::None,p.accent)}
            {style.enter(t,1.0,style.label(1135.0,845.0,"Actual UI / sample data"))}
        </g>)
    } else {
        fframes::svgr!(<g>
            {el::mascot(t,ctx,1250.0,360.0,390.0)}
            {style.enter(t,0.9,style.label(1200.0,844.0,"VIRTUAL PENGUIN NETWORK"))}
        </g>)
    };
    let extra = if style==Style::Kinetic {"SELECT / ROUTE / CONNECT"} else {"Selected apps via VPN. The rest direct."};
    fframes::svgr!(<g>
        {title(style,t,5,tx,ty,size)}
        {style.enter(t,0.8,style.body(tx+5.0,720.0,if style==Style::Terminal {26.0}else{31.0},extra))}
        <path d={format!("M {tx} 765 H {}",tx+750.0)} stroke={p.accent} stroke-width="3" stroke-dasharray="750 750" stroke-dashoffset={750.0*(1.0-ease(t,0.6,1.0))} />
        {style.enter(t,1.0,style.label(tx+5.0,833.0,"Windows / macOS / Linux / Android"))}
        {style.enter(t,1.25,style.label(112.0,968.0,"Open source / GPL-3.0-or-later"))}
        {art}
    </g>)
}
