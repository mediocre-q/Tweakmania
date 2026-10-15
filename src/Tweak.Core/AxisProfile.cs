namespace Tweak.Core;

/// <summary>A point in an axis response curve. Inputs and outputs are magnitudes in [0, 1].</summary>
public sealed record CurvePoint(double Input, double Output);

/// <summary>Calibration and response settings for one controller axis.</summary>
public sealed record AxisProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "Default";
    public double InputMinimum { get; init; } = -1;
    public double InputCenter { get; init; } = 0;
    public double InputMaximum { get; init; } = 1;
    public double Deadzone { get; init; }
    public double Saturation { get; init; } = 1;
    public bool Invert { get; init; }
    public bool Mirror { get; init; } = true;
    public CurvePoint[] LeftCurve { get; init; } = [new(0, 0), new(1, 1)];
    public CurvePoint[] RightCurve { get; init; } = [new(0, 0), new(1, 1)];

    /// <summary>Checks settings at the point where a profile is loaded or edited.</summary>
    public void Validate()
    {
        if (SchemaVersion != 1)
            throw new ArgumentException($"Unsupported axis profile schema version {SchemaVersion}; expected 1.", nameof(SchemaVersion));
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Profile name must not be empty.", nameof(Name));

        RequireFinite(InputMinimum, nameof(InputMinimum));
        RequireFinite(InputCenter, nameof(InputCenter));
        RequireFinite(InputMaximum, nameof(InputMaximum));
        if (InputMinimum < -2 || InputMinimum > 2 || InputCenter < -1 || InputCenter > 1 || InputMaximum < -2 || InputMaximum > 2)
            throw new ArgumentException("Calibration endpoints must be in [-200, 200] and center must be in [-100, 100].");
        if (!(InputMinimum < InputCenter && InputCenter < InputMaximum))
            throw new ArgumentException("Calibration must satisfy InputMinimum < InputCenter < InputMaximum.");

        RequireFinite(Deadzone, nameof(Deadzone));
        RequireFinite(Saturation, nameof(Saturation));
        if (Deadzone < 0 || Deadzone >= Saturation || Saturation > 1)
            throw new ArgumentException("Deadzone and Saturation must satisfy 0 <= Deadzone < Saturation <= 1.");

        ValidateCurve(LeftCurve, nameof(LeftCurve));
        ValidateCurve(RightCurve, nameof(RightCurve));
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException($"{name} must be a finite number.", name);
    }

    private static void ValidateCurve(CurvePoint[]? points, string name)
    {
        if (points is null)
            throw new ArgumentException($"{name} must contain at least two points.", name);
        if (points.Length < 2)
            throw new ArgumentException($"{name} must contain at least two points.", name);
        if (points[0] is null || points[^1] is null)
            throw new ArgumentException($"{name} cannot contain null points.", name);
        if (points[0].Input != 0 || points[0].Output != 0 || points[^1].Input != 1 || points[^1].Output != 1)
            throw new ArgumentException($"{name} must start at (0, 0) and end at (1, 1).", name);

        var previousInput = double.NegativeInfinity;
        var previousOutput = double.NegativeInfinity;
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            if (point is null)
                throw new ArgumentException($"{name} cannot contain null points.", name);
            if (!double.IsFinite(point.Input) || !double.IsFinite(point.Output))
                throw new ArgumentException($"Every {name} point must contain finite values.", name);
            if (point.Input < 0 || point.Input > 1 || point.Output < 0 || point.Output > 1)
                throw new ArgumentException($"Every {name} input and output must be in [0, 1].", name);
            if (point.Input <= previousInput)
                throw new ArgumentException($"{name} input values must be strictly increasing.", name);
            if (point.Output < previousOutput)
                throw new ArgumentException($"{name} output values must be nondecreasing.", name);
            previousInput = point.Input;
            previousOutput = point.Output;
        }
    }
}
