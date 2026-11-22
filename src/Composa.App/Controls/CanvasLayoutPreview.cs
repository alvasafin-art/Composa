using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Composa.App.Dialogs;
using SkiaSharp;
using System.Globalization;

namespace Composa.App.Controls;

internal sealed class CanvasLayoutPreview : Control
{
    private NewCanvasOptions? options;

    public void Show(NewCanvasOptions value)
    {
        options = value;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (options is not { } value || Bounds.Width < 80 || Bounds.Height < 80) return;
        var bounds = new Rect(Bounds.Size);
        var bleed = value.Layout.BleedPixels(value.Resolution);
        var scale = (float)Math.Min(Math.Min(450, bounds.Width - 76) / (value.Width + 2 * bleed),
            Math.Min(386, bounds.Height - 58) / (value.Height + 2 * bleed));
        var w = value.Width * scale;
        var h = value.Height * scale;
        var x = (float)(bounds.Width - w) / 2 + 8;
        var y = (float)(bounds.Height - h) / 2 + 5;
        var view = new SKMatrix(scale, 0, x, 0, scale, y, 0, 0, 1);
        var margin = Math.Max(1, (float)bleed * scale);
        context.FillRectangle(Palette.Pasteboard, bounds);
        context.Custom(new CanvasView.DrawOperation(bounds, canvas =>
        {
            using var paint = new SKPaint();
            var sheet = new SKRect(x, y, x + w, y + h);
            paint.Color = SKColors.Black.WithAlpha(55);
            var shadow = sheet; shadow.Offset(3, 5); canvas.DrawRect(shadow, paint);
            if (value.Background is { } color)
            {
                paint.Color = color;
                canvas.DrawRect(sheet, paint);
            }
            else
            {
                canvas.Save();
                canvas.ClipRect(sheet);
                paint.Color = new SKColor(236, 238, 241);
                canvas.DrawRect(sheet, paint);
                paint.Color = new SKColor(207, 211, 217);
                for (int row = 0; row * 8 < h; row++)
                    for (int col = row % 2; col * 8 < w; col += 2)
                        canvas.DrawRect(x + col * 8, y + row * 8, 8, 8, paint);
                canvas.Restore();
            }
            CanvasLayoutDrawing.Draw(canvas, value.Layout, value.Width, value.Height, value.Resolution, view, 1);
            using var edge = new SKPaint { Color = CanvasLayoutDrawing.Edge, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
            canvas.DrawRect(sheet, edge);
        }));

        var typeface = new Typeface(TextElement.GetFontFamily(this));
        var dimensionPen = new Pen(new SolidColorBrush(CanvasLayoutDrawing.Edge.ToAvalonia()));
        Dimension(value.Width, x + w / 2, y - margin - 12, w);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(x - margin - 12, y + h / 2)))
            Dimension(value.Height, 0, 0, h);

        void Dimension(int extent, double cx, double cy, double length)
        {
            var label = new FormattedText($"{extent} px", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11, Palette.Secondary);
            var halfLabel = label.Width / 2 + 6;
            if (length / 2 > halfLabel)
            {
                var start = cx - length / 2;
                var end = cx + length / 2;
                context.DrawLine(dimensionPen, new Point(start, cy), new Point(cx - halfLabel, cy));
                context.DrawLine(dimensionPen, new Point(cx + halfLabel, cy), new Point(end, cy));
                context.DrawLine(dimensionPen, new Point(start, cy - 3), new Point(start, cy + 3));
                context.DrawLine(dimensionPen, new Point(end, cy - 3), new Point(end, cy + 3));
            }
            context.DrawText(label, new Point(cx - label.Width / 2, cy - label.Height / 2));
        }
    }
}
