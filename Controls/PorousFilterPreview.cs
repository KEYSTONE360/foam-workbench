using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FoamWorkbench.Controls;

public sealed class PorousFilterPreview : FrameworkElement
{
    public IReadOnlyList<PorousLayer> Layers { get; set; } = [];

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = Math.Max(ActualWidth, 320);
        var height = Math.Max(ActualHeight, 420);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(13, 21, 31)),
            new Pen(new SolidColorBrush(Color.FromRgb(52, 69, 91)), 1),
            new Rect(0.5, 0.5, width - 1, height - 1), 16, 16);
        if (Layers.Count == 0) return;

        DrawText(dc, "TOP · WATER INLET", 18, 16, 12, Colors.White, FontWeights.SemiBold);
        DrawArrow(dc, width - 44, 18, width - 44, 48);
        var allThicknessKnown = Layers.All(layer => layer.Thickness is > 0);
        var weights = Layers.Select(layer => allThicknessKnown
            ? layer.Thickness!.Value
            : layer.Category == PorousMaterialCategory.GranularFill ? 2.6 : 1.0).ToArray();
        var total = weights.Sum();
        var top = 58.0;
        var bottomCaption = 39.0;
        var available = height - top - bottomCaption;
        var y = top;
        for (var i = 0; i < Layers.Count; i++)
        {
            var layer = Layers[i];
            var h = available * weights[i] / total;
            var color = ParseColor(layer.VisualMetadata.ColorHex);
            var foreground = RelativeLuminance(color) > 0.5 ? Color.FromRgb(18, 25, 34) : Colors.White;
            dc.DrawRectangle(new SolidColorBrush(color),
                new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 0.8),
                new Rect(18, y, width - 36, Math.Max(2, h)));
            var thickness = layer.Thickness is > 0
                ? $"{layer.Thickness.Value.ToString("G5", CultureInfo.CurrentCulture)} mm"
                : "INPUT REQUIRED";
            DrawText(dc, $"{layer.Id}  {layer.DisplayNameEn}", 30, y + 6,
                h < 39 ? 10 : 11, foreground, FontWeights.SemiBold);
            if (h >= 39)
                DrawText(dc, $"{layer.DisplayNameKo}  ·  {thickness}", 30, y + 22, 9, foreground, FontWeights.Normal);
            y += h;
        }

        DrawText(dc, "BOTTOM · WATER OUTLET", 18, height - 26, 11, Colors.White, FontWeights.SemiBold);
        DrawArrow(dc, width - 44, height - 39, width - 44, height - 13);
    }

    private static void DrawArrow(DrawingContext dc, double x1, double y1, double x2, double y2)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(79, 215, 197)), 2);
        dc.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
        dc.DrawLine(pen, new Point(x2, y2), new Point(x2 - 5, y2 - 7));
        dc.DrawLine(pen, new Point(x2, y2), new Point(x2 + 5, y2 - 7));
    }

    private static void DrawText(
        DrawingContext dc, string text, double x, double y, double size,
        Color color, FontWeight weight)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size, new SolidColorBrush(color), 1.0);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static Color ParseColor(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(122, 138, 155); }
    }

    private static double RelativeLuminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
}
