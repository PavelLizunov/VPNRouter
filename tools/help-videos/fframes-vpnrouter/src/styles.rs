use crate::graphics::{ease, smooth};
use fframes::Svgr;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum Style {
    #[default]
    Original,
    Anime,
    Swiss,
    Blueprint,
    Editorial,
    Terminal,
    Paper,
    Kinetic,
    Cinema,
}

impl Style {
    pub const ADDITIONAL: [Self; 8] = [Self::Anime, Self::Swiss, Self::Blueprint,
        Self::Editorial, Self::Terminal, Self::Paper, Self::Kinetic, Self::Cinema];

    pub fn parse(value: &str) -> Result<Self, String> {
        std::iter::once(Self::Original).chain(Self::ADDITIONAL)
            .find(|style| style.slug() == value)
            .ok_or_else(|| format!("Unknown film style: {value}"))
    }

    pub fn slug(self) -> &'static str {
        match self {
            Self::Original => "original", Self::Anime => "anime", Self::Swiss => "swiss",
            Self::Blueprint => "blueprint", Self::Editorial => "editorial",
            Self::Terminal => "terminal", Self::Paper => "paper",
            Self::Kinetic => "kinetic", Self::Cinema => "cinema",
        }
    }

    pub fn palette(self) -> Palette {
        match self {
            Self::Original => Palette::new("#080C15", "#F4F7FB", "#38BDF8", "#4ADE80", "#A6B5C9", "#141C2A"),
            Self::Anime => Palette::new("#161321", "#FFF1D5", "#F4CB56", "#E99BA5", "#C4B9CF", "#2B253A"),
            Self::Swiss => Palette::new("#EEEDE7", "#141414", "#B92920", "#393934", "#5D5D56", "#DEDCD4"),
            Self::Blueprint => Palette::new("#0C2C44", "#E1F4F5", "#8AE8F0", "#EDD8A3", "#A7C5D1", "#173E57"),
            Self::Editorial => Palette::new("#F5F0E5", "#29231F", "#773E2D", "#505B42", "#696055", "#E8DFCE"),
            Self::Terminal => Palette::new("#06170E", "#C6F3CC", "#92E8A0", "#F1CF85", "#8EB59A", "#10251A"),
            Self::Paper => Palette::new("#EAE2D0", "#242C28", "#245744", "#883B2A", "#5D645C", "#F9F4E8"),
            Self::Kinetic => Palette::new("#E7F06B", "#151713", "#151713", "#4B3D67", "#4A4E29", "#D1DA5F"),
            Self::Cinema => Palette::new("#121210", "#F5EEE2", "#D8B783", "#AAC5B8", "#B1ACA0", "#22231E"),
        }
    }

    pub fn font(self) -> &'static str {
        match self {
            Self::Anime | Self::Kinetic => "Anton",
            Self::Blueprint | Self::Terminal => "JetBrains Mono",
            Self::Editorial | Self::Cinema => "Instrument Serif",
            _ => "DM Sans",
        }
    }

    pub fn text(self, x: f32, y: f32, size: f32, color: &str, value: &str) -> Svgr<'static> {
        let font = self.font();
        let value = value.to_owned();
        let color = color.to_owned();
        fframes::svgr!(<text x={x} y={y} font-family={font} font-size={size} font-style={if matches!(self, Self::Editorial | Self::Cinema) { "italic" } else { "normal" }} font-weight={if matches!(self, Self::Swiss | Self::Paper) { "500" } else { "400" }} fill={color}>{value}</text>)
    }

    pub fn body(self, x: f32, y: f32, size: f32, value: &str) -> Svgr<'static> {
        let font = if matches!(self, Self::Terminal | Self::Blueprint) { "JetBrains Mono" } else { "DM Sans" };
        let value = value.to_owned();
        fframes::svgr!(<text x={x} y={y} font-family={font} font-size={size} font-weight="400" fill={self.palette().ink}>{value}</text>)
    }

    pub fn label(self, x: f32, y: f32, value: &str) -> Svgr<'static> {
        let value = value.to_owned();
        let color = if self == Self::Kinetic && x >= 1130.0 && (145.0..930.0).contains(&y) { self.palette().bg } else { self.palette().muted };
        fframes::svgr!(<text x={x} y={y} font-family="JetBrains Mono" font-size="23" fill={color}>{value}</text>)
    }

    pub fn enter<'a>(self, t: f32, delay: f32, content: Svgr<'a>) -> Svgr<'a> {
        let p = if self == Self::Terminal {
            // Character-like stepped reveals, then a long stable reading hold.
            (ease(t, delay, 0.55) * 12.0).floor() / 12.0
        } else { ease(t, delay, if self == Self::Cinema { 1.2 } else { 0.75 }) };
        let (dx, dy) = match self {
            Self::Swiss => (-60.0 * (1.0-p), 0.0),
            Self::Anime => (100.0 * (1.0-p), -30.0 * (1.0-p)),
            Self::Paper => (-28.0 * (1.0-p), 65.0 * (1.0-p)),
            Self::Kinetic => (0.0, 100.0 * (1.0-p)),
            Self::Terminal => (0.0, 0.0),
            _ => (0.0, 24.0 * (1.0-p)),
        };
        fframes::svgr!(<g opacity={p} transform={format!("translate({dx},{dy})")}>{content}</g>)
    }

    pub fn background(self, t: f32, index: usize) -> Svgr<'static> {
        let p = self.palette();
        let art = match self {
            Self::Anime => {
                let lines = (0..16).map(|i| {
                    let y = 175.0 + i as f32 * 49.0;
                    let shift = 55.0 * smooth(t, 0.0, 2.0);
                    fframes::svgr!(<path d={format!("M {} {y} l 290 -75", 1410.0-shift)} stroke={p.accent} stroke-width={if i % 3 == 0 { "4" } else { "1" }} opacity="0.14" />)
                }).collect::<Vec<_>>();
                fframes::svgr!(<g>
                    <path d="M 1120 145 L 1810 145 L 1810 930 L 920 930 Z" fill={p.panel} stroke={p.ink} stroke-width="3" />
                    {lines}
                    <path d="M 116 902 L 588 902 L 558 934 L 116 934 Z" fill={p.accent} />
                </g>)
            }
            Self::Swiss => fframes::svgr!(<g>
                <rect x="112" y="138" width="1696" height="5" fill={p.ink} />
                <rect x="1010" y="165" width="2" height="746" fill={p.ink} opacity="0.25" />
                <rect x="112" y="924" width="1696" height="2" fill={p.ink} />
            </g>),
            Self::Blueprint => {
                let grid = (0..29).map(|i| {
                    let x = 120.0 + i as f32 * 60.0;
                    fframes::svgr!(<path d={format!("M {x} 148 V 920")} stroke={p.accent} stroke-width="1" opacity="0.11" />)
                }).chain((0..13).map(|i| {
                    let y = 160.0 + i as f32 * 60.0;
                    fframes::svgr!(<path d={format!("M 112 {y} H 1808")} stroke={p.accent} stroke-width="1" opacity="0.11" />)
                })).collect::<Vec<_>>();
                fframes::svgr!(<g>{grid}<rect x="112" y="144" width="1696" height="786" fill="none" stroke={p.accent} stroke-width="1" /></g>)
            }
            Self::Editorial => fframes::svgr!(<g>
                <rect x="1020" y="146" width="788" height="783" fill={p.panel} />
                <path d="M 112 138 H 1808 M 112 928 H 1808" stroke={p.ink} stroke-width="1" />
            </g>),
            Self::Terminal => {
                let scan = (0..46).map(|i| fframes::svgr!(<rect x="113" y={152+i*16} width="1694" height="1" fill={p.accent} opacity="0.035" />)).collect::<Vec<_>>();
                fframes::svgr!(<g>
                    <rect x="112" y="144" width="1696" height="784" fill="none" stroke={p.muted} stroke-width="2" />
                    <rect x="112" y="144" width="1696" height="44" fill={p.panel} />
                    {scan}
                </g>)
            }
            Self::Paper => fframes::svgr!(<g>
                <path d="M 1100 145 L 1786 170 L 1802 910 L 1060 929 Z" fill="#C9BEA7" />
                <path d="M 1080 155 L 1800 150 L 1772 922 L 1068 908 Z" fill={p.panel} />
                <rect x="1155" y="141" width="180" height="34" fill="#D2B783" opacity="0.8" transform="rotate(-4 1245 158)" />
                <path d="M 112 898 H 590 V 930 H 112 Z" fill={p.second} />
            </g>),
            Self::Kinetic => fframes::svgr!(<g>
                <path d="M 1130 145 H 1808 V 930 H 1130 Z" fill={p.ink} />
                <path d="M 112 930 H 1050" stroke={p.ink} stroke-width="14" />
                <circle cx="1748" cy="205" r="20" fill={p.bg} />
            </g>),
            Self::Cinema => fframes::svgr!(<g>
                <rect x="0" y="0" width="1920" height="135" fill="#050605" />
                <rect x="0" y="945" width="1920" height="135" fill="#050605" />
                <path d="M 111 898 H 560" stroke={p.accent} stroke-width="1" />
            </g>),
            Self::Original => Svgr::empty(),
        };
        fframes::svgr!(<g>
            <rect width="1920" height="1080" fill={p.bg} />
            {art}
            {self.label(112.0, 98.0, &format!("{:02} / {}", index+1, crate::SHOTS[index].0))}
        </g>)
    }
}

#[derive(Clone, Copy)]
pub struct Palette {
    pub bg: &'static str, pub ink: &'static str, pub accent: &'static str,
    pub second: &'static str, pub muted: &'static str, pub panel: &'static str,
}
impl Palette {
    const fn new(bg: &'static str, ink: &'static str, accent: &'static str,
        second: &'static str, muted: &'static str, panel: &'static str) -> Self {
        Self { bg, ink, accent, second, muted, panel }
    }
}
