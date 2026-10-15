using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Tweak.App.Controls;

/// <summary>Shows signed steering as two opposing, independently filled triangular gauges.</summary>
public sealed class SteeringPreview : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SteeringPreview),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush LeftFill = Freeze(new SolidColorBrush(Color.FromRgb(255, 127, 114)));
    private static readonly Brush RightFill = Freeze(new SolidColorBrush(Color.FromRgb(57, 213, 208)));
    private static readonly Brush IdleFill = Freeze(new SolidColorBrush(Color.FromRgb(38, 53, 64)));
    private static readonly Brush Outline = Freeze(new SolidColorBrush(Color.FromRgb(63, 82, 95)));
    private static readonly Brush LabelText = Freeze(new SolidColorBrush(Color.FromRgb(242, 247, 250)));
    private static readonly Brush LabelPill = Freeze(new SolidColorBrush(Color.FromArgb(230, 16, 24, 32)));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20)
            return;

        var mid = width / 2;
        var padX = 7d;
        var padY = 8d;
        var top = padY;
        var bottom = height - padY;
        var bounded = double.IsFinite(Value) ? Math.Clamp(Value, -1, 1) : 0;
        var leftAmount = Math.Max(0, -bounded);
        var rightAmount = Math.Max(0, bounded);
        var leftTriangle = new[] { new Point(mid, top), new Point(mid, bottom), new Point(padX, height / 2) };
        var rightTriangle = new[] { new Point(mid, top), new Point(mid, bottom), new Point(width - padX, height / 2) };

        DrawTriangle(dc, leftTriangle, mid, leftAmount, true, LeftFill);
        DrawTriangle(dc, rightTriangle, mid, rightAmount, false, RightFill);
        dc.DrawLine(new Pen(Outline, 1), new Point(mid, top - 1), new Point(mid, bottom + 1));

        var fontSize = Math.Clamp(height * 0.24, 12, 15);
        DrawPillText(dc, $"{leftAmount * 100:0}%", new Point(width * 0.25, height / 2), fontSize);
        DrawPillText(dc, $"{rightAmount * 100:0}%", new Point(width * 0.75, height / 2), fontSize);
    }

    private static void DrawTriangle(DrawingContext dc, Point[] outlinePoints, double centerX, double amount, bool left, Brush activeBrush)
    {
        var triangle = new StreamGeometry();
        using (var g = triangle.Open())
        {
            g.BeginFigure(outlinePoints[0], true, true);
            g.LineTo(outlinePoints[1], true, false);
            g.LineTo(outlinePoints[2], true, false);
        }
        triangle.Freeze();
        dc.DrawGeometry(IdleFill, null, triangle);

        if (amount > 0.001)
        {
            var innerX = left
                ? centerX - (centerX - outlinePoints[2].X) * amount
                : centerX + (outlinePoints[2].X - centerX) * amount;
            var fill = new StreamGeometry();
            using (var g = fill.Open())
            {
                g.BeginFigure(new Point(centerX, outlinePoints[0].Y), true, true);
                g.LineTo(new Point(centerX, outlinePoints[1].Y), true, false);
                g.LineTo(new Point(innerX, (outlinePoints[0].Y + outlinePoints[1].Y) / 2), true, false);
            }
            fill.Freeze();
            dc.DrawGeometry(activeBrush, null, fill);
        }
        dc.DrawGeometry(null, new Pen(Outline, 1.1), triangle);
    }

    private void DrawPillText(DrawingContext dc, string text, Point center, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, LabelText, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var pill = new Rect(center.X - formatted.Width / 2 - 8, center.Y - formatted.Height / 2 - 4, formatted.Width + 16, formatted.Height + 8);
        dc.DrawRoundedRectangle(LabelPill, new Pen(Outline, 0.8), pill, 6, 6);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
