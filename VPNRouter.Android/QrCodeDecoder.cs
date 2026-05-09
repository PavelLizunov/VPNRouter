using System;
using System.Collections.Generic;
using ZXing;

namespace VPNRouter.Android;

/// <summary>
/// lucid-pike (2026-05-09) — pure-C# QR decode helper for the Simple-page
/// camera scan flow.
///
/// <para>The encoder half of QR lives in <c>VPNRouter.Core/Services/QrCode.cs</c>
/// (desktop's potential share path). This helper handles the inverse: take an
/// Android <see cref="global::Android.Graphics.Bitmap"/>, hand it through
/// ZXing.Net (RGBLuminanceSource → HybridBinarizer → MultiFormatReader), and
/// return the decoded text.</para>
///
/// <para>Why <see cref="BarcodeReaderGeneric"/> instead of the raw
/// <c>MultiFormatReader.decode</c> path: <see cref="BarcodeReaderGeneric.Decode(LuminanceSource)"/>
/// is the .NET-idiomatic public API across all ZXing.Net versions and rolls
/// the binarizer + hints + global histogram fallback into a single call. It
/// also wires AutoRotate so a QR photographed in landscape still decodes.
/// </para>
/// </summary>
internal static class QrCodeDecoder
{
    /// <summary>
    /// Decode the first QR code found in the bitmap. Returns <c>null</c> if
    /// no QR is detected; never throws on a missing barcode (catches
    /// <c>ReaderException</c> internally via the ZXing high-level wrapper).
    /// </summary>
    public static string? TryDecode(global::Android.Graphics.Bitmap bitmap)
    {
        if (bitmap is null) return null;

        int w = bitmap.Width, h = bitmap.Height;
        if (w <= 0 || h <= 0) return null;

        // Bitmap.GetPixels yields ARGB ints (0xAARRGGBB). ZXing.Net 0.16.x
        // takes byte[] luminance, so we collapse ARGB → grayscale here using
        // the same Y = 0.299R + 0.587G + 0.114B coefficients ZXing's other
        // sources use internally (rounded to 306/601/117 with a 0x200 bias
        // and >>10, so a naïve all-FF pixel maps to 255).
        var pixels = new int[w * h];
        bitmap.GetPixels(pixels, 0, w, 0, 0, w, h);
        var luminances = new byte[w * h];
        for (int i = 0; i < pixels.Length; i++)
        {
            int p = pixels[i];
            int r = (p >> 16) & 0xFF;
            int g = (p >> 8) & 0xFF;
            int b = p & 0xFF;
            luminances[i] = (byte)((306 * r + 601 * g + 117 * b + 0x200) >> 10);
        }

        try
        {
            var luminanceSource = new RGBLuminanceSource(luminances, w, h);
            var reader = new BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                    TryHarder = true,
                },
            };
            var result = reader.Decode(luminanceSource);
            return result?.Text;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("VpnRouter.QrScan",
                $"ZXing decode threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Decode a JPEG/PNG file from disk + downscale before decoding. We cap
    /// the longest side at <paramref name="maxDimension"/> (default caller
    /// passes 1280) so a 12-megapixel phone shot doesn't allocate a 50 MB
    /// pixel buffer on cheap devices. ZXing decodes a 1280×x QR with the
    /// same reliability as a 4032×x QR — the binarizer cares about edge
    /// contrast, not absolute resolution.
    /// </summary>
    public static global::Android.Graphics.Bitmap? LoadDownscaledBitmap(string path, int maxDimension)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            return null;

        // First pass: decode bounds only to compute the InSampleSize. The
        // returned Bitmap from this call is null because InJustDecodeBounds
        // suppresses pixel allocation.
        var bounds = new global::Android.Graphics.BitmapFactory.Options
        {
            InJustDecodeBounds = true,
        };
        global::Android.Graphics.BitmapFactory.DecodeFile(path, bounds);
        int srcW = bounds.OutWidth, srcH = bounds.OutHeight;
        if (srcW <= 0 || srcH <= 0) return null;

        int sample = 1;
        while ((srcW / sample) > maxDimension || (srcH / sample) > maxDimension)
        {
            sample *= 2;
        }

        var decode = new global::Android.Graphics.BitmapFactory.Options
        {
            InSampleSize = sample,
            InPreferredConfig = global::Android.Graphics.Bitmap.Config.Argb8888,
        };
        return global::Android.Graphics.BitmapFactory.DecodeFile(path, decode);
    }
}
