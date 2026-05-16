/*
 * QrScanLauncher.java — Bug-AND-023 v2 (lucid-pike follow-up, 2026-05-17).
 *
 * Thin Java bridge over `com.journeyapps:zxing-android-embedded`'s
 * IntentIntegrator. Called from MainActivity.cs via reflection so the
 * AndroidLibrary import of zxing-android-embedded-4.3.0.aar can stay
 * `Bind="false"` (avoids forcing the .NET Android binding generator to
 * walk the entire BarcodeView class graph; same trick we use for
 * libbox.aar). The aar's AndroidManifest declares its own CaptureActivity
 * so the manifest merger picks it up automatically.
 *
 * Live-preview rationale: the previous QR flow (Bug-AND-023 v1) launched
 * MediaStore.ACTION_IMAGE_CAPTURE → user took a photo → ZXing.Net decoded
 * the JPEG. That worked end-to-end but felt unnatural: the user has to
 * frame, press the shutter, then wait. zxing-android-embedded delivers
 * an on-the-fly preview with auto-detect and tight focus assist, which
 * matches what every other Android QR-capable app does.
 *
 * Result contract (parseResult):
 *   - returns null  → activity result wasn't from the QR scanner
 *                     (caller should fall through to other branches)
 *   - returns ""    → user cancelled / pressed back
 *   - returns text  → decoded QR contents
 */
package com.ninitux.vpnrouter;

import android.app.Activity;
import android.content.Intent;

// Live-preview capture activity ships in com.journeyapps.barcodescanner
// (the JourneyApps wrapper), but IntentIntegrator + IntentResult live in
// com.google.zxing.integration.android (the ZXing legacy compat package
// the JourneyApps aar re-exports for drop-in upgrades from upstream
// ZXing). Don't merge these into one import line — they're physically
// in different sub-packages inside the aar's classes.jar.
import com.journeyapps.barcodescanner.CaptureActivity;
import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

public final class QrScanLauncher {

    private QrScanLauncher() { /* static helpers only */ }

    /** IntentIntegrator's hard-coded REQUEST_CODE (0x0000C0DE / 49374).
     *  Exposed so the C# side knows which request-code branch in
     *  OnActivityResult routes here. */
    public static final int REQUEST_CODE = IntentIntegrator.REQUEST_CODE;

    /**
     * Launch the live-preview QR scanner. Caller is responsible for the
     * runtime CAMERA permission grant (zxing-android-embedded does NOT
     * request it itself — it just throws if it's missing, and we hit a
     * worse UX than just denying gracefully).
     */
    public static void launch(Activity activity) {
        IntentIntegrator integrator = new IntentIntegrator(activity);
        integrator.setDesiredBarcodeFormats(IntentIntegrator.QR_CODE);
        integrator.setOrientationLocked(false);
        // No beep — VPN setup UX is silent everywhere else, the sudden
        // shutter sound felt jarring on the v1 photo-capture flow too.
        integrator.setBeepEnabled(false);
        // Empty prompt — the default "Scan a barcode" is en-only and
        // doesn't fit our bilingual UI. The default capture activity
        // still shows a viewfinder rectangle, which is enough hint.
        integrator.setPrompt("");
        // Use the bundled CaptureActivity (declared in the aar's
        // AndroidManifest). No portrait lock so users with phones in
        // a landscape grip can still scan.
        integrator.setCaptureActivity(CaptureActivity.class);
        integrator.initiateScan();
    }

    /**
     * Parse the OnActivityResult payload. See the class-level Javadoc
     * for the null / "" / "text" contract.
     */
    public static String parseResult(int requestCode, int resultCode, Intent data) {
        IntentResult result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data);
        if (result == null) return null;
        String contents = result.getContents();
        return contents == null ? "" : contents;
    }
}
