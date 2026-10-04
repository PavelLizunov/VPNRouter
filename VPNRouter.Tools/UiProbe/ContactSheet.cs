using SkiaSharp;

namespace VPNRouter.Tools.UiProbe;

// Several captures on one labelled sheet, so a model can compare themes, languages and sizes in one image.
public static class ContactSheet
{
    public static byte[] Build(IReadOnlyList<(string Label, byte[] Png)> items, int columns = 3, int cellWidth = 380)
    {
        if (items.Count == 0) throw new ArgumentException("nothing to lay out");
        columns = Math.Clamp(columns, 1, 6);
        const int pad = 12;
        const int labelHeight = 22;

        var bitmaps = items.Select(i => SKBitmap.Decode(i.Png)).ToList();
        try
        {
            var heights = bitmaps.Select(b => (int)Math.Round(b.Height * (cellWidth / (double)b.Width))).ToList();
            var rows = (items.Count + columns - 1) / columns;
            var rowHeights = new int[rows];
            for (var i = 0; i < items.Count; i++)
                rowHeights[i / columns] = Math.Max(rowHeights[i / columns], heights[i] + labelHeight);

            var sheetWidth = pad + columns * (cellWidth + pad);
            var sheetHeight = pad + rowHeights.Sum(h => h + pad);
            using var surface = SKSurface.Create(new SKImageInfo(sheetWidth, sheetHeight));
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(0x2B, 0x2B, 0x33));
            using var font = new SKFont(SKTypeface.Default, 13);
            using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };

            var y = pad;
            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < columns; col++)
                {
                    var index = row * columns + col;
                    if (index >= items.Count) break;
                    var x = pad + col * (cellWidth + pad);
                    canvas.DrawText(items[index].Label, x, y + 15, font, paint);
                    using var image = SKImage.FromBitmap(bitmaps[index]);
                    canvas.DrawImage(image, SKRect.Create(x, y + labelHeight, cellWidth, heights[index]),
                        new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                }
                y += rowHeights[row] + pad;
            }

            using var snapshot = surface.Snapshot();
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            foreach (var b in bitmaps) b.Dispose();
        }
    }
}
