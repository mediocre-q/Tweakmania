namespace Tweak.Core;

/// <summary>Maps raw controller values through calibration and a response curve.</summary>
public static class AxisTransform
{
    /// <summary>
    /// Applies a validated profile. Call <see cref="AxisProfile.Validate"/> when settings are
    /// loaded or changed; this method is intended for the per-sample path.
    /// </summary>
    public static double Apply(double input, AxisProfile profile)
    {
        if (!double.IsFinite(input))
            return 0;

        var delta = input - profile.InputCenter;
        var positive = delta >= 0;
        var range = positive
            ? profile.InputMaximum - profile.InputCenter
            : profile.InputCenter - profile.InputMinimum;
        var magnitude = Math.Abs(delta) / range;
        if (magnitude >= 1)
            magnitude = 1;

        if (magnitude <= profile.Deadzone)
            magnitude = 0;
        else if (magnitude >= profile.Saturation)
            magnitude = 1;
        else
            magnitude = (magnitude - profile.Deadzone) / (profile.Saturation - profile.Deadzone);

        var curve = profile.Mirror ? profile.RightCurve : positive ? profile.RightCurve : profile.LeftCurve;
        var output = EvaluateCurve(magnitude, curve);
        if (!positive)
            output = -output;
        return profile.Invert ? -output : output;
    }

    /// <summary>Evaluates a piecewise-linear curve whose points are sorted by input.</summary>
    public static double EvaluateCurve(double magnitude, IReadOnlyList<CurvePoint> points)
    {
        if (!double.IsFinite(magnitude))
            return 0;
        if (points is null)
            throw new ArgumentNullException(nameof(points));
        if (points.Count == 0)
            throw new ArgumentException("A curve must contain at least one point.", nameof(points));

        if (magnitude <= points[0].Input)
            return points[0].Output;
        var last = points.Count - 1;
        if (magnitude >= points[last].Input)
            return points[last].Output;

        var low = 0;
        var high = last;
        while (high - low > 1)
        {
            var middle = low + ((high - low) / 2);
            if (magnitude < points[middle].Input)
                high = middle;
            else
                low = middle;
        }

        var left = points[low];
        var right = points[high];
        var fraction = (magnitude - left.Input) / (right.Input - left.Input);
        return left.Output + fraction * (right.Output - left.Output);
    }
}
