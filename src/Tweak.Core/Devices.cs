namespace Tweak.Core;

public enum InputApi { DirectInput, XInput, Demo }

public sealed record InputDevice(string Id, string Name, InputApi Api, string[] AxisNames)
{
    public override string ToString() => Name;
}

// Standard order: X, Y, Z, Rx, Ry, Rz, Slider 1, Slider 2. Values are [-1, 1].
// XInput: LX, LY, LT, RX, RY, RT, unused, unused; triggers rest at -1.
// Hats: -1 neutral, then eight clockwise directions 0=N,1=NE,2=E,...,7=NW.
public sealed record ControllerSnapshot(double[] Axes, bool[] Buttons, int[] Hats)
{
    public static ControllerSnapshot Neutral => new(new double[8], new bool[128], [-1, -1, -1, -1]);
}

public interface IInputSource : IDisposable
{
    InputDevice Device { get; }
    bool TryRead(out ControllerSnapshot snapshot);
}

public interface IControllerOutput : IDisposable
{
    void Submit(ControllerSnapshot snapshot);
    void Neutralize();
}
