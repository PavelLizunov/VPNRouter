using System;
using System.Collections.Generic;
using System.IO;
using Android.Graphics.Drawables;
using AndroidBitmap = Android.Graphics.Bitmap;
using AndroidCanvas = Android.Graphics.Canvas;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace VPNRouter.Android;

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
