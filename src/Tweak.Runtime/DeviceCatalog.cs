using System.Runtime.InteropServices;
using Tweak.Core;
using Vortice.DirectInput;

namespace Tweak.Runtime;

/// <summary>Enumerates XInput and DirectInput game controllers and opens live input sources.</summary>
public sealed class DeviceCatalog : IDisposable
{
    private IDirectInput8? _directInput;
    private bool _disposed;

    public InputDevice[] Enumerate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var devices = new List<InputDevice>();
        for (uint slot = 0; slot < 4; slot++)
        {
            if (XInputSource.IsConnected(slot))
                devices.Add(new InputDevice($"xinput:{slot}", $"Xbox Controller ({slot + 1})", InputApi.XInput,
                    ["LX", "LY", "LT", "RX", "RY", "RT", "", ""]));
        }

        try
        {
            _directInput ??= DInput.DirectInput8Create();
            foreach (var instance in _directInput.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                var axes = GetAxisNames(instance.InstanceGuid);
                devices.Add(new InputDevice($"directinput:{instance.InstanceGuid:D}",
                    string.IsNullOrWhiteSpace(instance.ProductName) ? instance.InstanceName : instance.ProductName,
                    InputApi.DirectInput, axes));
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            // XInput devices are still useful when DirectInput cannot be initialized.
        }
        return devices.ToArray();
    }

    public IInputSource Open(InputDevice device, nint windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(device);
        return device.Api switch
        {
            InputApi.XInput when TryParseXInput(device.Id, out var slot) => new XInputSource(device, slot),
            InputApi.DirectInput when TryParseDirectInput(device.Id, out var guid) => OpenDirectInput(device, guid, windowHandle),
            _ => throw new ArgumentException($"Unsupported or malformed input device identity '{device.Id}'.", nameof(device))
        };
    }

    private IInputSource OpenDirectInput(InputDevice device, Guid instanceGuid, nint windowHandle)
    {
        _directInput ??= DInput.DirectInput8Create();
        var native = CreateDirectInputDevice(instanceGuid, windowHandle);
        try
        {
            var axisObjects = native.GetObjects(DeviceObjectTypeFlags.Axis);
            var axes = AxisMap.Create(native, axisObjects);
            return new DirectInputSource(device, native, axes, () => CreateDirectInputDevice(instanceGuid, windowHandle));
        }
        catch
        {
            native.Dispose();
            throw;
        }
    }

    private IDirectInputDevice8 CreateDirectInputDevice(Guid instanceGuid, nint windowHandle)
    {
        _directInput ??= DInput.DirectInput8Create();
        var native = _directInput.CreateDevice(instanceGuid);
        try
        {
            native.SetDataFormat<RawJoystickState>().CheckError();
            native.SetCooperativeLevel(windowHandle, CooperativeLevel.NonExclusive | CooperativeLevel.Background).CheckError();
            foreach (var axis in native.GetObjects(DeviceObjectTypeFlags.Axis))
            {
                try { native.GetObjectPropertiesById(axis.ObjectId).Range = new InputRange(0, 65535); }
                catch (SharpGen.Runtime.SharpGenException) { }
            }
            native.Acquire().CheckError();
            return native;
        }
        catch { native.Dispose(); throw; }
    }

    private string[] GetAxisNames(Guid instanceGuid)
    {
        var names = new string[8];
        if (_directInput is null) return names;
        try
        {
            using var device = _directInput.CreateDevice(instanceGuid);
            var axisObjects = device.GetObjects(DeviceObjectTypeFlags.Axis);
            var map = AxisMap.Create(device, axisObjects);
            var sliderOccurrence = 0;
            foreach (var axis in axisObjects)
            {
                var index = AxisMap.IndexOf(axis.ObjectType, axis.ObjectType == ObjectGuid.Slider ? sliderOccurrence++ : 0);
                if (index >= 0 && map.Exists(index) && !string.IsNullOrWhiteSpace(axis.Name)) names[index] = axis.Name;
            }
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException) { }
        return names;
    }

    private static bool TryParseXInput(string id, out uint slot)
    {
        slot = 0;
        return id.StartsWith("xinput:", StringComparison.OrdinalIgnoreCase) &&
               uint.TryParse(id.AsSpan("xinput:".Length), out slot) && slot < 4;
    }

    private static bool TryParseDirectInput(string id, out Guid guid)
    {
        guid = default;
        return id.StartsWith("directinput:", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(id.AsSpan("directinput:".Length), out guid);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _directInput?.Dispose();
        _directInput = null;
    }

    internal sealed class AxisMap
    {
        private readonly bool[] _exists;
        private readonly int[] _minimums;
        private readonly int[] _maximums;
        private AxisMap(bool[] exists, int[] minimums, int[] maximums)
        { _exists = exists; _minimums = minimums; _maximums = maximums; }
        public static AxisMap Create(IDirectInputDevice8 native, IList<DeviceObjectInstance> objects)
        {
            var exists = new bool[8];
            var minimums = new int[8];
            var maximums = new int[8];
            var sliderOccurrence = 0;
            foreach (var item in objects)
            {
                var index = IndexOf(item.ObjectType, item.ObjectType == ObjectGuid.Slider ? sliderOccurrence++ : 0);
                if (index < 0) continue;
                try
                {
                    var properties = native.GetObjectPropertiesById(item.ObjectId);
                    try { properties.Range = new InputRange(0, 65535); }
                    catch (SharpGen.Runtime.SharpGenException) { }
                    var range = properties.Range;
                    if (range.Maximum <= range.Minimum) continue;
                    minimums[index] = range.Minimum;
                    maximums[index] = range.Maximum;
                    exists[index] = true;
                }
                catch (SharpGen.Runtime.SharpGenException) { }
            }
            return new AxisMap(exists, minimums, maximums);
        }
        public bool Exists(int index) => _exists[index];
        public int Minimum(int index) => _minimums[index];
        public int Maximum(int index) => _maximums[index];
        internal static double Normalize(int raw, int minimum, int maximum) => maximum <= minimum
            ? 0.0
            : Math.Clamp((raw - (double)minimum) * 2.0 / (maximum - (double)minimum) - 1.0, -1.0, 1.0);
        public static int IndexOf(Guid type, int sliderOccurrence = 0)
        {
            var ids = new[] { ObjectGuid.XAxis, ObjectGuid.YAxis, ObjectGuid.ZAxis, ObjectGuid.RxAxis,
                ObjectGuid.RyAxis, ObjectGuid.RzAxis };
            for (var i = 0; i < ids.Length; i++) if (type == ids[i]) return i;
            if (type == ObjectGuid.Slider && sliderOccurrence is >= 0 and <= 1) return 6 + sliderOccurrence;
            return -1;
        }
    }
}

internal sealed class XInputSource(InputDevice device, uint slot) : IInputSource
{
    private bool _disposed;
    public InputDevice Device { get; } = device;

    public static bool IsConnected(uint slot) => XInputGetState(slot, out _) == 0;

    public bool TryRead(out ControllerSnapshot snapshot)
    {
        snapshot = ControllerSnapshot.Neutral;
        if (_disposed || XInputGetState(slot, out var state) != 0) return false;
        var axes = new double[8];
        axes[0] = NormalizeSigned(state.Gamepad.LeftThumbX);
        axes[1] = NormalizeSigned(state.Gamepad.LeftThumbY);
        axes[2] = NormalizeTrigger(state.Gamepad.LeftTrigger);
        axes[3] = NormalizeSigned(state.Gamepad.RightThumbX);
        axes[4] = NormalizeSigned(state.Gamepad.RightThumbY);
        axes[5] = NormalizeTrigger(state.Gamepad.RightTrigger);
        var buttons = new bool[128];
        for (var i = 0; i < 16; i++) buttons[i] = (state.Gamepad.Buttons & (1 << i)) != 0;
        var hats = new[] { -1, -1, -1, -1 };
        hats[0] = MapDpad(state.Gamepad.Buttons);
        snapshot = new ControllerSnapshot(axes, buttons, hats);
        return true;
    }

    public void Dispose() => _disposed = true;
    internal static double NormalizeSigned(short value) => value < 0 ? value / 32768.0 : value / 32767.0;
    internal static double NormalizeTrigger(byte value) => value / 127.5 - 1.0;
    internal static int MapDpad(ushort buttons)
    {
        var up = (buttons & 0x0001) != 0;
        var down = (buttons & 0x0002) != 0;
        var left = (buttons & 0x0004) != 0;
        var right = (buttons & 0x0008) != 0;
        var vertical = up == down ? 0 : up ? -1 : 1;
        var horizontal = left == right ? 0 : left ? -1 : 1;
        return (vertical, horizontal) switch
        {
            (-1, 0) => 0, (-1, 1) => 1, (0, 1) => 2, (1, 1) => 3,
            (1, 0) => 4, (1, -1) => 5, (0, -1) => 6, (-1, -1) => 7,
            _ => -1
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftThumbX, LeftThumbY, RightThumbX, RightThumbY; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState", ExactSpelling = true)]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);
}

internal sealed class DirectInputSource : IInputSource
{
    private IDirectInputDevice8? _native;
    private readonly DeviceCatalog.AxisMap _axes;
    private readonly Func<IDirectInputDevice8> _reopen;
    private long _nextOpenAttempt;
    private int _consecutiveErrors;
    private bool _disposed;
    public DirectInputSource(InputDevice device, IDirectInputDevice8 native, DeviceCatalog.AxisMap axes, Func<IDirectInputDevice8> reopen)
    { Device = device; _native = native; _axes = axes; _reopen = reopen; }
    public InputDevice Device { get; }
    public bool TryRead(out ControllerSnapshot snapshot)
    {
        snapshot = ControllerSnapshot.Neutral;
        if (_disposed) return false;
        if (_native is null)
        {
            if (Environment.TickCount64 < _nextOpenAttempt) return false;
            _nextOpenAttempt = Environment.TickCount64 + 500;
            try { _native = _reopen(); }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException) { return false; }
        }
        try
        {
            _native.Poll().CheckError();
            var state = _native.GetCurrentJoystickState();
            var rawAxes = new[] { state.X, state.Y, state.Z, state.RotationX, state.RotationY, state.RotationZ,
                state.Sliders.ElementAtOrDefault(0), state.Sliders.ElementAtOrDefault(1) };
            var values = new double[8];
            for (var i = 0; i < values.Length; i++) values[i] = _axes.Exists(i)
                ? DeviceCatalog.AxisMap.Normalize(rawAxes[i], _axes.Minimum(i), _axes.Maximum(i)) : 0.0;
            var buttons = new bool[128];
            var rawButtons = state.Buttons;
            Array.Copy(rawButtons, buttons, Math.Min(rawButtons.Length, buttons.Length));
            var hats = new int[4];
            var rawHats = state.PointOfViewControllers;
            for (var i = 0; i < hats.Length; i++) hats[i] = i < rawHats.Length ? ConvertPov(rawHats[i]) : -1;
            snapshot = new ControllerSnapshot(values, buttons, hats);
            _consecutiveErrors = 0;
            return true;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            try { _native.Unacquire().CheckError(); } catch { }
            try { _native.Acquire().CheckError(); }
            catch
            {
                try { _native.Dispose(); } catch { }
                _native = null;
                _nextOpenAttempt = Environment.TickCount64 + 500;
            }
            if (++_consecutiveErrors >= 3 && _native is not null)
            {
                try { _native.Dispose(); } catch { }
                _native = null;
                _nextOpenAttempt = Environment.TickCount64 + 500;
            }
            return false;
        }
    }
    internal static int ConvertPov(int hundredths)
    {
        if (hundredths < 0 || hundredths >= 36000) return -1;
        return (int)Math.Round(hundredths / 4500.0, MidpointRounding.AwayFromZero) % 8;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_native is null) return;
        try { _native.Unacquire().CheckError(); } catch { }
        _native.Dispose();
        _native = null;
    }
}
