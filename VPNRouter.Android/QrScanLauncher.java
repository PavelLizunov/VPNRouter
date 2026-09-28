package com.ninitux.vpnrouter;

import android.app.Activity;
import android.content.Intent;

import com.journeyapps.barcodescanner.CaptureActivity;
import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

public final class QrScanLauncher {

    private QrScanLauncher() {  }

    public static final int REQUEST_CODE = IntentIntegrator.REQUEST_CODE;

    public static void launch(Activity activity) {
        IntentIntegrator integrator = new IntentIntegrator(activity);
        integrator.setDesiredBarcodeFormats(IntentIntegrator.QR_CODE);
        integrator.setOrientationLocked(true);
        integrator.setBeepEnabled(false);
        integrator.setPrompt("");
        integrator.setCaptureActivity(CaptureActivity.class);
        integrator.initiateScan();
    }

    public static String parseResult(int requestCode, int resultCode, Intent data) {
        IntentResult result = IntentIntegrator.parseActivityResult(requestCode, resultCode, data);
        if (result == null) return null;
        String contents = result.getContents();
        return contents == null ? "" : contents;
    }
}
