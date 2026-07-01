using System;
using System.Collections.Generic;
using System.IO;
using Android.Graphics.Drawables;
// Disambiguate vs Android.Graphics.Bitmap — the cache stores the
// Avalonia type, the conversion uses both. Aliases keep the call sites
// readable without prefix noise.
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidCanvas = Android.Graphics.Canvas;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace VPNRouter.Android;

/// <summary>
/// v3.0 v2.32.0 (2026-05-07) — converts Android Drawable → Avalonia Bitmap
/// for the per-app picker (handbook §5.5 follow-up). Pre-2.32 the picker
/// rendered text-only rows, which made browsing 100+ installed apps a slog —
/// users had to read package names to identify Discord vs Telegram. Desktop
/// ApplicationsPage (VPNRouter.App/Views/Pages/ApplicationsPage.axaml) shows
/// a similar list but on Windows the OS doesn't expose a uniform per-process
/// icon API, so this concern only applies to the Android port.
///
/// <para>Conversion pipeline:
/// <list type="number">
///   <item>Resolve <see cref="Drawable.IntrinsicWidth"/> /
///   <see cref="Drawable.IntrinsicHeight"/>; cap at 96×96 so AdaptiveIconDrawables
///   don't allocate a 1024² bitmap for a 24px UI element.</item>
///   <item>Allocate a software ARGB_8888 Android bitmap, render the
///   Drawable into a Canvas backed by it.</item>
///   <item>PNG-encode to a <see cref="MemoryStream"/>, hand to Avalonia's
///   stream-based Bitmap constructor. PNG is slower per icon
///   (~10 ms vs ~1 ms for direct pixel-swap), but encapsulates the
///   format conversion + premultiplication + endianness trivia in one step.
///   At 96² capped + ~100 user apps the cold-cache cost is ~1 s on
///   KYOCERA A101BM, which is amortised by the existing async load that
///   the picker overlay already runs.</item>
/// </list></para>
///
/// <para>Cache: simple LRU keyed by package name, capped at 200 entries.
/// Reasons for the cap: typical user has 80-150 user-installed apps, plus
/// 100-300 system apps when the system-apps toggle flips. 200 covers a
/// recently-toggled session without unbounded heap growth (each Avalonia
/// Bitmap holds the decoded pixel buffer ~96×96×4=37 KB → 200 × 37 KB =
/// ~7 MB worst case).</para>
///
/// <para>Thread safety: all operations are guarded by an internal lock so
/// the AppListLoader background Task.Run can populate while the UI thread
/// reads from the picker row builder. Avalonia Bitmap instances are
/// immutable post-construction and safe to read across threads.</para>
/// </summary>
internal static class AppIconCache
{
    private const int MaxEntries = 200;
    private const int MaxIconSize = 96;

    private static readonly object _lock = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> _index =
        new(StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> _order = new();

    private sealed class CacheEntry
    {
        public string PackageName { get; }
        public Bitmap Bitmap { get; }
        public CacheEntry(string p, Bitmap b) { PackageName = p; Bitmap = b; }
    }

    public static Bitmap? GetOrConvert(string packageName, Drawable? drawable)
    {
        if (string.IsNullOrEmpty(packageName)) return null;

        lock (_lock)
        {
            if (_index.TryGetValue(packageName, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Bitmap;
            }
        }

        if (drawable is null) return null;

        Bitmap? converted = null;
        try
        {
            converted = ConvertDrawable(drawable);
        }
        catch
        {
            converted = null;
        }
        if (converted is null) return null;

        lock (_lock)
        {
            // Re-check under lock — another thread may have populated this key
            // while we were converting; prefer the existing entry to avoid
            // double-allocating the same icon.
            if (_index.TryGetValue(packageName, out var existing))
            {
                _order.Remove(existing);
                _order.AddFirst(existing);
                return existing.Value.Bitmap;
            }

            var entry = new CacheEntry(packageName, converted);
            var node = new LinkedListNode<CacheEntry>(entry);
            _order.AddFirst(node);
            _index[packageName] = node;

            while (_order.Count > MaxEntries)
            {
                var tail = _order.Last;
                if (tail is null) break;
                _order.RemoveLast();
                _index.Remove(tail.Value.PackageName);
            }
        }

        return converted;
    }

    private static Bitmap? ConvertDrawable(Drawable drawable)
    {
        int w = drawable.IntrinsicWidth > 0 ? drawable.IntrinsicWidth : 48;
        int h = drawable.IntrinsicHeight > 0 ? drawable.IntrinsicHeight : 48;

        // Adaptive icons can report 1024×1024 intrinsic — far more than the
        // 24px UI slot needs. Scale down preserving aspect.
        if (w > MaxIconSize || h > MaxIconSize)
        {
            double scale = Math.Min((double)MaxIconSize / w, (double)MaxIconSize / h);
            w = Math.Max(1, (int)(w * scale));
            h = Math.Max(1, (int)(h * scale));
        }

        using var androidBitmap = AndroidBitmap.CreateBitmap(
            w, h, AndroidBitmap.Config.Argb8888!);
        if (androidBitmap is null) return null;

        using (var canvas = new AndroidCanvas(androidBitmap))
        {
            drawable.SetBounds(0, 0, w, h);
            drawable.Draw(canvas);
        }

        using var stream = new MemoryStream();
        if (!androidBitmap.Compress(AndroidBitmap.CompressFormat.Png!, 100, stream))
            return null;
        stream.Position = 0;
        return new Bitmap(stream);
    }
}
