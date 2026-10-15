using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Tweak.Core;

namespace Tweak.Runtime;

/// <summary>Stable snapshot reported by the injected DirectInput proxy.</summary>
public sealed record ProxyTelemetry(int ProcessId, int Status, uint ConfigurationSequence,
    long SampleCount, double Input, double Output, ulong TickCount);

/// <summary>Publishes the selected axis profile to the native DirectInput proxy.</summary>
public sealed class ProxyController : IDisposable
{
    public const int MappingSize = 4096;
    public const uint ConfigMagic = 0x31575443; // CTW1, little endian
    public const uint TelemetryMagic = 0x31575443;
    private const int ConfigSequenceOffset = 8;
    private readonly string _configMapName;
    private readonly string _mutexName;
    private readonly Mutex _writerMutex;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly object _gate = new();
    private uint _sequence;
    private bool _disposed;
    private static readonly ConcurrentDictionary<string, byte> ActiveWriters = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="namespaceName">Optional complete test namespace prefix; production uses Local\ControllerTweak.</param>
    public ProxyController(string? namespaceName = null)
    {
        var prefix = string.IsNullOrWhiteSpace(namespaceName) ? "Local\\ControllerTweak" : namespaceName.TrimEnd('\\', '.');
        _configMapName = prefix + ".Config.v1";
        _mutexName = prefix + ".Config.v1.Writer";
        if (!ActiveWriters.TryAdd(_mutexName, 0))
            throw new InvalidOperationException("Another Tweakmania editor is already publishing proxy settings.");
        try { _writerMutex = new Mutex(false, _mutexName); }
        catch { ActiveWriters.TryRemove(_mutexName, out _); throw; }
        bool acquired;
        try { acquired = _writerMutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        catch
        {
            _writerMutex.Dispose();
            ActiveWriters.TryRemove(_mutexName, out _);
            throw;
        }
        if (!acquired)
        {
            _writerMutex.Dispose();
            ActiveWriters.TryRemove(_mutexName, out _);
            throw new InvalidOperationException("Another Tweakmania editor is already publishing proxy settings.");
        }
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            mapping = MemoryMappedFile.CreateOrOpen(_configMapName, MappingSize, MemoryMappedFileAccess.ReadWrite);
            view = mapping.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.ReadWrite);
            _sequence = view.ReadUInt32(ConfigSequenceOffset);
            if (view.ReadUInt32(0) != ConfigMagic || view.ReadUInt32(4) != 1 || (_sequence & 1) != 0)
            {
                _sequence = 0;
                view.Write(0, ConfigMagic);
                view.Write(4, 1u);
                view.Write(ConfigSequenceOffset, 0u);
                view.Flush();
            }
            _mapping = mapping;
            _view = view;
        }
        catch
        {
            view?.Dispose();
            mapping?.Dispose();
            _writerMutex.ReleaseMutex();
            _writerMutex.Dispose();
            ActiveWriters.TryRemove(_mutexName, out _);
            throw;
        }
    }

    public string MappingName => _configMapName;
    public uint ConfigurationSequence => Volatile.Read(ref _sequence);

    /// <summary>Atomically publishes one profile. Curves are limited to the 32-point native ABI capacity.</summary>
    public void Publish(Guid deviceId, int axis, AxisProfile profile, bool enabled, bool bypass, bool monitor)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (axis is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(axis), "Axis index must be in the supported range 0 through 7.");
        if (profile.LeftCurve.Length > 32 || profile.RightCurve.Length > 32)
            throw new ArgumentException("Proxy curves support at most 32 points on each side.", nameof(profile));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stable = _sequence;
            if ((stable & 1) != 0) stable++;
            var writing = unchecked(stable + 1);
            var done = unchecked(stable + 2);
            _view.Write(ConfigSequenceOffset, writing);
            Thread.MemoryBarrier();
            _view.Write(0, ConfigMagic);
            _view.Write(4, 1u);
            var flags = (enabled ? 1u : 0u) | (bypass ? 2u : 0u) | (monitor ? 4u : 0u) |
                        (profile.Invert ? 8u : 0u) | (profile.Mirror ? 16u : 0u);
            _view.Write(12, flags);
            var guid = deviceId.ToByteArray();
            _view.WriteArray(16, guid, 0, guid.Length);
            _view.Write(32, (uint)axis);
            _view.Write(36, (uint)profile.LeftCurve.Length);
            _view.Write(40, (uint)profile.RightCurve.Length);
            _view.Write(44, 0u);
            _view.Write(48, profile.InputMinimum);
            _view.Write(56, profile.InputCenter);
            _view.Write(64, profile.InputMaximum);
            _view.Write(72, profile.Deadzone);
            _view.Write(80, profile.Saturation);
            _view.Write(88, 0UL);
            WriteCurve(96, profile.LeftCurve);
            WriteCurve(608, profile.RightCurve);
            Thread.MemoryBarrier();
            _view.Write(ConfigSequenceOffset, done);
            _view.Flush();
            _sequence = done;
        }
    }

    private void WriteCurve(long offset, CurvePoint[] points)
    {
        for (var i = 0; i < 32; i++)
        {
            var p = i < points.Length ? points[i] : new CurvePoint(0, 0);
            _view.Write(offset + i * 16L, p.Input);
            _view.Write(offset + i * 16L + 8, p.Output);
        }
    }

    public static bool TryReadTelemetry(int processId, out ProxyTelemetry telemetry)
    {
        telemetry = default!;
        if (processId <= 0) return false;
        try
        {
            using var map = MemoryMappedFile.OpenExisting($"Local\\ControllerTweak.Telemetry.v1.{processId}", MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.Read);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var before = view.ReadUInt32(8);
                if ((before & 1) != 0 || view.ReadUInt32(0) != TelemetryMagic || view.ReadUInt32(4) != 1) continue;
                var pid = view.ReadInt32(12);
                var status = view.ReadInt32(16);
                var config = view.ReadUInt32(20);
                var samples = view.ReadUInt64(24);
                var input = view.ReadDouble(32);
                var output = view.ReadDouble(40);
                var ticks = view.ReadUInt64(48);
                Thread.MemoryBarrier();
                var after = view.ReadUInt32(8);
                if (before != after || (after & 1) != 0 || pid != processId) continue;
                telemetry = new ProxyTelemetry(pid, status, config, unchecked((long)samples), input, output, ticks);
                return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        return false;
    }

    public static Process? FindGameProcess(string executablePath)
    {
        var expected = Path.GetFullPath(executablePath);
        var processName = Path.GetFileNameWithoutExtension(expected);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? ""), expected, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            process.Dispose();
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { Publish(Guid.Empty, 0, new AxisProfile { Mirror = false }, false, false, false); }
            finally
            {
                _disposed = true;
                _view.Dispose();
                _mapping.Dispose();
                _writerMutex.ReleaseMutex();
                _writerMutex.Dispose();
                ActiveWriters.TryRemove(_mutexName, out _);
            }
        }
    }
}
