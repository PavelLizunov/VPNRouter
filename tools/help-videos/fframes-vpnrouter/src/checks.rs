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
