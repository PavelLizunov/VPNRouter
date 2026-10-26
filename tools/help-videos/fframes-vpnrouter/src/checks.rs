use fframes::{FFramesContext, FFramesMode, Svgr, TimeBase, VideoSize};
use fframes::usvgr::svgtree::{AId, EId, NestedNodeData, NestedNodeKind};

fn context<'a>() -> FFramesContext<'a, 'a> {
    FFramesContext {
        time_base: TimeBase { fps: 30, sample_rate: 48000 },
        current_video_size: VideoSize { width: 1920, height: 1080 },
        duration_in_frames: 1080, mode: FFramesMode::Renderer,
        scenes: None, media_source: None, font_source: None, abort_signal: None,
    }
}

fn attr(node: &NestedNodeData<'_>, key: AId) -> Option<String> {
    node.attrs.iter().find(|a| a.name == key).map(|a| a.value.to_string())
}

fn find_id<'tree, 'data>(nodes: &'tree [Option<NestedNodeData<'data>>], id: &str) -> Option<&'tree NestedNodeData<'data>> {
    for node in nodes.iter().flatten() {
        if attr(node, AId::Id).as_deref() == Some(id) { return Some(node); }
        if let Some(found) = find_id(&node.children, id) { return Some(found); }
    }
    None
}

#[test]
fn screenshot_focus_is_a_child_of_the_exact_image_motion_group() {
    use crate::graphics::{self, Focus, ScreenshotPose};
    let directory = fframes::MediaDirectory::read_folder("media").unwrap();
    let media = directory.process_media_source().unwrap();
    let mut ctx = context();
    ctx.media_source = Some(&media);
    for frame in 0..240 {
        let time = frame as f32/30.0;
        for (kind,id) in [(Focus::Application,"application-focus"),(Focus::Connection,"connection-focus")] {
            let pose = ScreenshotPose::at(time, 1170.0, 166.0, 0.96);
            let svg = graphics::screenshot(time,&ctx,"applications.png",pose,kind,"#38BDF8");
            let group = find_id(&svg.svg_tree.nodes,"registered-screenshot-applications.png").unwrap();
            assert_eq!(attr(group,AId::Transform).as_deref(),Some(pose.transform().as_str()));
            let image = group.children.iter().flatten().find(|node|
                matches!(node.kind,NestedNodeKind::Element{tag_name:EId::Image})).expect("real fixture image must share the same parent");
            assert!(attr(image,AId::Transform).is_none());
            assert_eq!(attr(image,AId::Width).as_deref(),Some("560"));
            assert_eq!(attr(image,AId::Height).as_deref(),Some("780"));
            let focus = find_id(&group.children,id).expect("focus must share parent transform");
            assert!(attr(focus,AId::Transform).is_none(),"no independent overlay transform");
            if matches!(kind, Focus::Application) {
                assert_eq!(attr(focus,AId::X).as_deref(),Some("147"));
                assert_eq!(attr(focus,AId::Y).as_deref(),Some("171"));
            } else {
                assert_eq!(attr(focus,AId::Cx).as_deref(),Some("280"));
                assert_eq!(attr(focus,AId::Cy).as_deref(),Some("151"));
            }
            let (x,y) = pose.source_point(147.0,171.0);
            assert!((x-1311.12).abs()<0.01 && y.is_finite());
            assert!((158.0..=210.0).contains(&pose.y));
            if frame==0 { assert!((pose.y-210.0).abs()<0.01); }
        }
    }
}

fn all_text(svg: &Svgr<'_>) -> String {
    fn collect(nodes: &[Option<NestedNodeData<'_>>], output: &mut String) {
        for node in nodes.iter().flatten() {
            if let NestedNodeKind::Text(value) = &node.kind { output.push_str(value.as_ref()); output.push(' '); }
            collect(&node.children,output);
        }
    }
    let mut output=String::new();
    collect(&svg.svg_tree.nodes,&mut output);
    output
}

#[test]
fn eight_styles_are_explicit_distinct_and_have_no_repository_cta() {
    let ctx=context();
    let mut frames=std::collections::HashSet::new();
    for style in crate::Style::ADDITIONAL {
        assert_eq!(crate::Style::parse(style.slug()).unwrap(),style);
        let mut transcript=String::new();
        for index in 0..6 {
            let svg=crate::variants::render(style,index,3.0,&ctx);
            transcript.push_str(&all_text(&svg));
            if index==0 { frames.insert(format!("{svg:?}")); }
        }
        let lower=transcript.to_lowercase();
        for forbidden in ["vpnrouter","pavellizunov","github.com"] { assert!(!lower.contains(forbidden)); }
        for platform in ["Windows","macOS","Linux","Android"] { assert!(transcript.contains(platform)); }
        assert!(transcript.contains("sample data"));
    }
    assert_eq!(frames.len(),8);
    assert!(crate::Style::parse("unknown").is_err());
}

#[test]
fn screenshot_group_keeps_the_native_source_dimensions() {
    use crate::graphics::{self, Focus, ScreenshotPose};
    let ctx=context();
    let svg=graphics::screenshot(3.0,&ctx,"applications.png",ScreenshotPose::at(3.0,100.0,100.0,0.5),Focus::Application,"#38BDF8");
    let group=find_id(&svg.svg_tree.nodes,"registered-screenshot-applications.png").unwrap();
    let border=group.children.iter().flatten().find(|n|matches!(n.kind,NestedNodeKind::Element{tag_name:EId::Rect})).unwrap();
    assert_eq!(attr(border,AId::Width).as_deref(),Some("574"));
    assert_eq!(attr(border,AId::Height).as_deref(),Some("794"));
}

#[test]
fn timeline_contract_is_thirty_six_seconds_at_thirty_fps() {
    assert_eq!(
        crate::SHOTS.iter().map(|shot| shot.1).sum::<f32>(),
        crate::SECONDS
    );
    assert_eq!((crate::SECONDS * crate::FPS as f32) as usize, 1080);
    assert!(
        crate::SHOTS
            .iter()
            .all(|(name, duration)| !name.is_empty() && *duration > 0.4)
    );
}

#[test]
fn route_geometry_and_easing_stay_finite_at_every_export_frame() {
    use crate::graphics::{curve_point, ease, smooth};
    let curve = [
        (670.0, 373.0),
        (1010.0, 373.0),
        (1010.0, 440.0),
        (1395.0, 440.0),
    ];
    assert_eq!(curve_point(curve, 0.0), curve[0]);
    assert_eq!(curve_point(curve, 1.0), curve[3]);
    for frame in 0..1080 {
        let time = frame as f32 / 30.0;
        for value in [ease(time, 0.35, 0.65), smooth(time, 1.4, 1.25)] {
            assert!(value.is_finite() && (0.0..=1.0).contains(&value));
            let (x, y) = curve_point(curve, value);
            assert!(x.is_finite() && y.is_finite());
            assert!((670.0..=1395.001).contains(&x));
            assert!((372.999..=440.001).contains(&y));
        }
    }
}
