using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Tweak.Core;

namespace Tweak.App.Controls;

/// <summary>Compact magnitude response graph with directly draggable interior points.</summary>
public sealed class CurveEditor : FrameworkElement
{
    public static readonly DependencyProperty ProfileProperty = DependencyProperty.Register(
        nameof(Profile), typeof(AxisProfile), typeof(CurveEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty InputValueProperty = DependencyProperty.Register(
        nameof(InputValue), typeof(double), typeof(CurveEditor), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutputValueProperty = DependencyProperty.Register(
        nameof(OutputValue), typeof(double), typeof(CurveEditor), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EditLeftCurveProperty = DependencyProperty.Register(
        nameof(EditLeftCurve), typeof(bool), typeof(CurveEditor), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowLiveMarkerProperty = DependencyProperty.Register(
        nameof(ShowLiveMarker), typeof(bool), typeof(CurveEditor), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush PlotBackground = Freeze(new SolidColorBrush(Color.FromRgb(17, 27, 35)));
    private static readonly Brush GridBrush = Freeze(new SolidColorBrush(Color.FromRgb(42, 57, 68)));
    private static readonly Brush IdentityBrush = Freeze(new SolidColorBrush(Color.FromRgb(101, 120, 132)));
    private static readonly Brush CurveBrush = Freeze(new SolidColorBrush(Color.FromRgb(57, 213, 208)));
    private static readonly Brush LeftCurveBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 127, 114)));
    private static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(234, 242, 247)));
    private static readonly Brush TextBrush = Freeze(new SolidColorBrush(Color.FromRgb(145, 162, 175)));
    private static readonly Brush MarkerBrush = Freeze(new SolidColorBrush(Color.FromRgb(250, 210, 118)));
    private const double Left = 34;
    private const double Right = 16;
    private const double Top = 12;
    private const double Bottom = 27;
    private int dragIndex = -1;

    public event Action<CurvePoint[]>? CurveEdited;

    public AxisProfile? Profile
    {
        get => (AxisProfile?)GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public double InputValue
    {
        get => (double)GetValue(InputValueProperty);
        set => SetValue(InputValueProperty, value);
    }

    public double OutputValue
    {
        get => (double)GetValue(OutputValueProperty);
        set => SetValue(OutputValueProperty, value);
    }

    public bool EditLeftCurve
    {
        get => (bool)GetValue(EditLeftCurveProperty);
        set => SetValue(EditLeftCurveProperty, value);
    }

    public bool ShowLiveMarker
    {
        get => (bool)GetValue(ShowLiveMarkerProperty);
        set => SetValue(ShowLiveMarkerProperty, value);
    }

    public CurveEditor()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var rect = PlotRect;
        if (rect.Width < 20 || rect.Height < 20)
            return;

        dc.DrawRoundedRectangle(PlotBackground, new Pen(GridBrush, 1), new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
        for (var i = 0; i <= 4; i++)
        {
            var fraction = i / 4d;
            var x = rect.Left + fraction * rect.Width;
            var y = rect.Bottom - fraction * rect.Height;
            dc.DrawLine(new Pen(GridBrush, i is 0 or 4 ? 1.1 : 0.65), new Point(x, rect.Top), new Point(x, rect.Bottom));
            dc.DrawLine(new Pen(GridBrush, i is 0 or 4 ? 1.1 : 0.65), new Point(rect.Left, y), new Point(rect.Right, y));
            DrawLabel(dc, $"{i * 25}", new Point(x, rect.Bottom + 7), 9, TextBrush, center: true);
        }
        DrawLabel(dc, "OUTPUT", new Point(8, rect.Top + rect.Height / 2), 9, TextBrush, center: true, rotate: -90);
        DrawLabel(dc, "INPUT  %", new Point(rect.Right, ActualHeight - 8), 9, TextBrush, center: true);

        var identity = new Pen(IdentityBrush, 1) { DashStyle = DashStyles.Dash };
        dc.DrawLine(identity, Map(0, 0), Map(1, 1));

        var curve = CurrentCurve;
        if (curve is { Length: >= 2 })
        {
            var color = IsEditingLeft ? LeftCurveBrush : CurveBrush;
            var pen = new Pen(color, 2.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Map(curve[0].Input, curve[0].Output), false, false);
                for (var i = 1; i < curve.Length; i++)
                    g.LineTo(Map(curve[i].Input, curve[i].Output), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);

            for (var i = 0; i < curve.Length; i++)
            {
                var p = Map(curve[i].Input, curve[i].Output);
                var radius = i == dragIndex ? 6 : 4.4;
                dc.DrawEllipse(i is 0 || i == curve.Length - 1 ? color : HandleBrush, new Pen(color, 1.5), p, radius, radius);
            }
        }

        if (ShowLiveMarker)
        {
            var (normalizedInput, positiveInput) = CalibratedMagnitude(InputValue, Profile);
            var activeCurve = ProfileValue is null || ProfileValue.Mirror || (EditLeftCurve ? !positiveInput : positiveInput);
            if (activeCurve)
            {
                var output = double.IsFinite(OutputValue) ? Math.Clamp(Math.Abs(OutputValue), 0, 1) : 0;
                var marker = Map(normalizedInput, output);
                dc.DrawEllipse(MarkerBrush, new Pen(PlotBackground, 2), marker, 5.2, 5.2);
                DrawLabel(dc, "LIVE", new Point(rect.Left + 22, rect.Top + 5), 8, MarkerBrush, center: false);
            }
            else
            {
                DrawLabel(dc, EditLeftCurve ? "EDIT LEFT" : "EDIT RIGHT", new Point(rect.Left + 22, rect.Top + 5), 8, TextBrush, center: false);
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var curve = CurrentCurve;
        if (curve is null || curve.Length <= 2)
            return;
        var position = e.GetPosition(this);
        var nearestDistance = 144d;
        var nearest = -1;
        for (var i = 1; i < curve.Length - 1; i++)
        {
            var p = Map(curve[i].Input, curve[i].Output);
            var dx = p.X - position.X;
            var dy = p.Y - position.Y;
            var distance = dx * dx + dy * dy;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = i;
            }
        }

        if (nearest >= 0)
        {
            dragIndex = nearest;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
            return;

        var curve = CurrentCurve;
        if (curve is null || dragIndex >= curve.Length - 1)
            return;

        var position = e.GetPosition(this);
        var rect = PlotRect;
        var previousInput = curve[dragIndex - 1].Input;
        var nextInput = curve[dragIndex + 1].Input;
        var inputMargin = Math.Min(0.005, (nextInput - previousInput) / 3);
        var pointInput = Math.Clamp((position.X - rect.Left) / rect.Width, previousInput + inputMargin, nextInput - inputMargin);
        var pointOutput = Math.Clamp((rect.Bottom - position.Y) / rect.Height, curve[dragIndex - 1].Output, curve[dragIndex + 1].Output);
        curve[dragIndex] = new CurvePoint(pointInput, pointOutput);
        CurveEdited?.Invoke(curve);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (dragIndex >= 0)
        {
            dragIndex = -1;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private AxisProfile? ProfileValue => Profile;

    private bool IsEditingLeft => EditLeftCurve && ProfileValue is { Mirror: false };

    private CurvePoint[]? CurrentCurve
    {
        get
        {
            if (ProfileValue is null)
                return null;
            return IsEditingLeft ? ProfileValue.LeftCurve.ToArray() : ProfileValue.RightCurve.ToArray();
        }
    }

    private (double Magnitude, bool Positive) CalibratedMagnitude(double raw, AxisProfile? profile)
    {
        if (profile is null || !double.IsFinite(raw))
            return (Math.Clamp(Math.Abs(raw), 0, 1), raw >= 0);

        var positive = raw >= profile.InputCenter;
        var range = positive ? profile.InputMaximum - profile.InputCenter : profile.InputCenter - profile.InputMinimum;
        var magnitude = Math.Clamp(Math.Abs(raw - profile.InputCenter) / range, 0, 1);
        if (magnitude <= profile.Deadzone)
            magnitude = 0;
        else if (magnitude >= profile.Saturation)
            magnitude = 1;
        else
            magnitude = (magnitude - profile.Deadzone) / (profile.Saturation - profile.Deadzone);
        return (magnitude, positive);
    }

    private Rect PlotRect => new(Left, Top, Math.Max(1, ActualWidth - Left - Right), Math.Max(1, ActualHeight - Top - Bottom));

    private Point Map(double x, double y)
    {
        var rect = PlotRect;
        return new Point(rect.Left + Math.Clamp(x, 0, 1) * rect.Width, rect.Bottom - Math.Clamp(y, 0, 1) * rect.Height);
    }

    private void DrawLabel(DrawingContext dc, string text, Point point, double size, Brush brush, bool center, double rotate = 0, bool rightAligned = false)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var origin = new Point(center ? point.X - formatted.Width / 2 : rightAligned ? point.X - formatted.Width : point.X, point.Y - formatted.Height / 2);
        if (rotate != 0)
        {
            dc.PushTransform(new RotateTransform(rotate, point.X, point.Y));
            dc.DrawText(formatted, origin);
            dc.Pop();
        }
        else
        {
            dc.DrawText(formatted, origin);
        }
    }

    private static Brush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
