using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FoamWorkbench.Controls;

public sealed class ResidualPlot : FrameworkElement
{
    private const double FullLogMinimum = -12;
    private const double FullLogMaximum = 0;
    private const double MinimumHorizontalSpan = 0.01;
    private const double MinimumVerticalSpan = 0.5;
    private IReadOnlyList<ResidualSample> _samples = [];
    private double _viewXMinimum;
    private double _viewXMaximum = 1;
    private double _viewLogMinimum = FullLogMinimum;
    private double _viewLogMaximum = FullLogMaximum;
    private static readonly Color[] Colors =
    [
        Color.FromRgb(124, 140, 255),
        Color.FromRgb(79, 215, 197),
        Color.FromRgb(242, 198, 109),
        Color.FromRgb(193, 140, 255),
        Color.FromRgb(255, 124, 146),
        Color.FromRgb(110, 185, 255)
    ];

    public IReadOnlyList<ResidualSample> Samples
    {
        get => _samples;
        set
        {
            _samples = value;
            if (_samples.Count == 0) ResetZoom();
            InvalidateVisual();
        }
    }

    public bool IsZoomed =>
        _viewXMinimum > 1e-9 || _viewXMaximum < 1 - 1e-9 ||
        _viewLogMinimum > FullLogMinimum + 1e-9 || _viewLogMaximum < FullLogMaximum - 1e-9;
    public double HorizontalZoomFactor => 1 / (_viewXMaximum - _viewXMinimum);
    public double VerticalZoomFactor =>
        (FullLogMaximum - FullLogMinimum) / (_viewLogMaximum - _viewLogMinimum);

    public ResidualPlot()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        ToolTip = "마우스 휠: 확대/축소 · Shift+휠: 가로축 · Ctrl+휠: 세로축 · 더블클릭: 전체 보기";
    }

    public void ResetZoom()
    {
        _viewXMinimum = 0;
        _viewXMaximum = 1;
        _viewLogMinimum = FullLogMinimum;
        _viewLogMaximum = FullLogMaximum;
        InvalidateVisual();
    }

    public void Zoom(double horizontalAnchorRatio, double verticalAnchorRatio, double factor,
        bool zoomHorizontal = true, bool zoomVertical = true)
    {
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), "Zoom factor must be finite and greater than zero.");

        var xRatio = Math.Clamp(horizontalAnchorRatio, 0, 1);
        var yRatio = Math.Clamp(verticalAnchorRatio, 0, 1);
        if (zoomHorizontal)
        {
            var anchor = _viewXMinimum + xRatio * (_viewXMaximum - _viewXMinimum);
            ZoomWindow(ref _viewXMinimum, ref _viewXMaximum, anchor, factor,
                0, 1, MinimumHorizontalSpan);
        }
        if (zoomVertical)
        {
            var anchor = _viewLogMaximum - yRatio * (_viewLogMaximum - _viewLogMinimum);
            ZoomWindow(ref _viewLogMinimum, ref _viewLogMaximum, anchor, factor,
                FullLogMinimum, FullLogMaximum, MinimumVerticalSpan);
        }

        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var plotArea = GetPlotArea();
        var pointer = e.GetPosition(this);
        if (!plotArea.Contains(pointer))
        {
            base.OnMouseWheel(e);
            return;
        }

        Focus();
        var factor = e.Delta > 0 ? 0.8 : 1.25;
        var modifiers = Keyboard.Modifiers;
        var horizontalOnly = modifiers.HasFlag(ModifierKeys.Shift) &&
                             !modifiers.HasFlag(ModifierKeys.Control);
        var verticalOnly = modifiers.HasFlag(ModifierKeys.Control) &&
                           !modifiers.HasFlag(ModifierKeys.Shift);
        ZoomAt(pointer, plotArea, factor, !verticalOnly, !horizontalOnly);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetZoom();
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 80 || height < 80) return;

        var plotArea = GetPlotArea();
        var left = plotArea.Left;
        var top = plotArea.Top;
        var right = plotArea.Right;
        var bottom = plotArea.Bottom;
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(43, 57, 74)), 1);
        var textBrush = new SolidColorBrush(Color.FromRgb(162, 176, 193));
        var legendTextBrush = new SolidColorBrush(Color.FromRgb(243, 246, 250));

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(9, 14, 21)), null, new Rect(0, 0, width, height));

        const int verticalTickCount = 6;
        for (var tick = 0; tick <= verticalTickCount; tick++)
        {
            var exponent = _viewLogMaximum -
                           (_viewLogMaximum - _viewLogMinimum) * tick / verticalTickCount;
            var y = MapLogY(exponent, top, bottom);
            dc.DrawLine(axisPen, new Point(left, y), new Point(right, y));
            var label = new FormattedText($"1e{exponent:0.##}", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Consolas"), 10, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(7, y - 7));
        }

        dc.DrawLine(axisPen, new Point(left, bottom), new Point(right, bottom));
        dc.DrawLine(axisPen, new Point(left, top), new Point(left, bottom));

        if (_samples.Count == 0)
        {
            var empty = new FormattedText("솔버 실행 시 잔차 곡선이 실시간으로 표시됩니다.",
                System.Globalization.CultureInfo.GetCultureInfo("ko-KR"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 13, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(empty, new Point((width - empty.Width) / 2, (height - empty.Height) / 2));
            return;
        }

        var maxSequence = Math.Max(2, _samples.Max(s => s.Sequence));
        const int horizontalTickCount = 4;
        for (var tick = 0; tick <= horizontalTickCount; tick++)
        {
            var fraction = _viewXMinimum +
                           (_viewXMaximum - _viewXMinimum) * tick / horizontalTickCount;
            var x = left + (right - left) * tick / horizontalTickCount;
            var sequence = (int)Math.Round(fraction * maxSequence);
            var label = new FormattedText(sequence.ToString("N0", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"),
                9, textBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(Math.Clamp(x - label.Width / 2, left, right - label.Width), bottom + 5));
        }

        var fields = _samples.Select(s => s.Field).Distinct().ToArray();
        dc.PushClip(new RectangleGeometry(plotArea));
        for (var fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
        {
            var field = fields[fieldIndex];
            var points = _samples.Where(s => s.Field == field).OrderBy(s => s.Sequence).ToArray();
            if (points.Length == 0) continue;

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var first = Map(points[0], maxSequence, left, right, top, bottom);
                context.BeginFigure(first, false, false);
                foreach (var sample in points.Skip(1))
                    context.LineTo(Map(sample, maxSequence, left, right, top, bottom), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Colors[fieldIndex % Colors.Length]), 1.7), geometry);
        }
        dc.Pop();

        var legendX = left;
        var legendY = 8d;
        var legendTypeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);
        foreach (var (field, index) in fields.Select((f, i) => (f, i)))
        {
            var brush = new SolidColorBrush(Colors[index % Colors.Length]);
            var label = new FormattedText(field, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, legendTypeface, 11, legendTextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var itemWidth = label.Width + 34;
            if (legendX + itemWidth > right)
            {
                legendX = left;
                legendY += 25;
            }
            if (legendY > 34) break;

            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(32, 42, 57)), axisPen,
                new Rect(legendX, legendY, itemWidth, 21), 5, 5);
            dc.DrawRectangle(brush, null, new Rect(legendX + 7, legendY + 6, 9, 9));
            dc.DrawText(label, new Point(legendX + 21, legendY + 2));
            legendX += itemWidth + 7;
        }

        var interactionHint = IsZoomed
            ? "확대됨 · 휠 축소 · 더블클릭 전체"
            : "휠 확대/축소 · 더블클릭 전체";
        var hint = new FormattedText(interactionHint, CultureInfo.GetCultureInfo("ko-KR"),
            FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, textBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(hint, new Point(Math.Max(left, right - hint.Width), 42));
    }

    private Rect GetPlotArea() => new(52, 64, Math.Max(0, ActualWidth - 68), Math.Max(0, ActualHeight - 98));

    private void ZoomAt(Point pointer, Rect plotArea, double factor, bool zoomHorizontal, bool zoomVertical)
    {
        var xRatio = Math.Clamp((pointer.X - plotArea.Left) / Math.Max(plotArea.Width, 1), 0, 1);
        var yRatio = Math.Clamp((pointer.Y - plotArea.Top) / Math.Max(plotArea.Height, 1), 0, 1);
        Zoom(xRatio, yRatio, factor, zoomHorizontal, zoomVertical);
    }

    private static void ZoomWindow(ref double minimum, ref double maximum, double anchor,
        double factor, double fullMinimum, double fullMaximum, double minimumSpan)
    {
        var newMinimum = anchor - (anchor - minimum) * factor;
        var newMaximum = anchor + (maximum - anchor) * factor;
        var span = Math.Clamp(newMaximum - newMinimum, minimumSpan, fullMaximum - fullMinimum);
        var anchorRatio = (anchor - newMinimum) / Math.Max(newMaximum - newMinimum, double.Epsilon);
        newMinimum = anchor - span * anchorRatio;
        newMaximum = newMinimum + span;

        if (newMinimum < fullMinimum)
        {
            newMaximum += fullMinimum - newMinimum;
            newMinimum = fullMinimum;
        }
        if (newMaximum > fullMaximum)
        {
            newMinimum -= newMaximum - fullMaximum;
            newMaximum = fullMaximum;
        }

        minimum = Math.Max(fullMinimum, newMinimum);
        maximum = Math.Min(fullMaximum, newMaximum);
    }

    private Point Map(ResidualSample sample, int maxSequence,
        double left, double right, double top, double bottom)
    {
        var normalizedSequence = sample.Sequence / (double)maxSequence;
        var x = left + (right - left) *
            (normalizedSequence - _viewXMinimum) / (_viewXMaximum - _viewXMinimum);
        return new Point(x, MapY(sample.Initial, top, bottom));
    }

    private double MapY(double value, double top, double bottom)
    {
        var clamped = Math.Clamp(value, 1e-12, 1);
        return MapLogY(Math.Log10(clamped), top, bottom);
    }

    private double MapLogY(double exponent, double top, double bottom) =>
        top + (_viewLogMaximum - exponent) /
        (_viewLogMaximum - _viewLogMinimum) * (bottom - top);
}
