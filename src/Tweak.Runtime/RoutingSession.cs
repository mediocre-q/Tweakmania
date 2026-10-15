using System.Diagnostics;
using System.Runtime.InteropServices;
using Tweak.Core;

namespace Tweak.Runtime;

public sealed record RoutingConfiguration(AxisProfile Profile, int InputAxis, int OutputAxis, bool Bypass, bool Monitor = true);
public sealed record RoutingTelemetry(bool Connected, double Input, double Output, long Samples, double IntervalMilliseconds, string Message);
public sealed record RoutingState(bool Connected, string Message);

/// <summary>One source and one optional output. All device calls run on the same worker thread.</summary>
public sealed class RoutingSession
{
    private readonly Func<IInputSource> _openSource;
    private readonly Func<IControllerOutput>? _openOutput;
    private readonly Func<RoutingConfiguration> _configuration;
    private readonly Func<IDisposable>? _hideSource;
    private RoutingTelemetry _latest = new(false, 0, 0, 0, 0, "Starting…");
    public RoutingTelemetry Latest => Volatile.Read(ref _latest);
    private RoutingState _state = new(false, "Starting…");
    public RoutingState State => Volatile.Read(ref _state);

    public RoutingSession(Func<IInputSource> openSource, Func<IControllerOutput>? openOutput, Func<RoutingConfiguration> configuration, Func<IDisposable>? hideSource = null)
    {
        _openSource = openSource;
        _openOutput = openOutput;
        _configuration = configuration;
        _hideSource = hideSource;
    }

    public Task RunAsync(CancellationToken cancellationToken) => Task.Factory.StartNew(
        () => Run(cancellationToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken cancellationToken)
    {
        IInputSource? source = null;
        IControllerOutput? output = null;
        IDisposable? visibilityLease = null;
        var timingEnabled = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
        var samples = 0L;
        var connected = false;
        var monitoring = false;
        try
        {
            source = _openSource();
            output = _openOutput?.Invoke();
            // Clear all controls on initial ownership before sending any input.
            output?.Neutralize();
            if (cancellationToken.IsCancellationRequested) return;
            // Hiding is scoped to an acquired output; monitoring never hides a device.
            if (output is not null) visibilityLease = _hideSource?.Invoke();
            var watch = Stopwatch.StartNew();
            var lastRead = watch.Elapsed.TotalMilliseconds;
            var nextRead = lastRead;
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = watch.Elapsed.TotalMilliseconds;
                if (now < nextRead)
                {
                    cancellationToken.WaitHandle.WaitOne(Math.Max(1, (int)Math.Ceiling(nextRead - now)));
                    continue;
                }
                var interval = now - lastRead;
                lastRead = now;
                nextRead = Math.Max(nextRead + 4, now); // Target 250 Hz; never replay missed ticks.
                var config = _configuration();
                if (monitoring != config.Monitor)
                {
                    monitoring = config.Monitor;
                    // A disabled preview has no per-sample telemetry allocations/publication.
                    if (!monitoring) Volatile.Write(ref _latest, new(false, 0, 0, samples, 0, "Live preview off"));
                }
                if (config.InputAxis is < 0 or >= 8 || config.OutputAxis is < 0 or >= 8)
                    throw new InvalidOperationException("Choose an input and output axis.");
                if (!source.TryRead(out var snapshot))
                {
                    if (connected) output?.Neutralize();
                    if (connected || State.Message == "Starting…")
                        Volatile.Write(ref _state, new(false, "Source disconnected · waiting for input"));
                    connected = false;
                    if (monitoring && Latest.Connected)
                        Volatile.Write(ref _latest, new(false, 0, 0, samples, interval, "Source disconnected · waiting for input"));
                    continue;
                }
                if (snapshot.Axes.Length <= config.InputAxis || snapshot.Axes.Length <= config.OutputAxis)
                    throw new InvalidOperationException("The source did not supply the selected axis.");
                var input = snapshot.Axes[config.InputAxis];
                var transformed = config.Bypass ? Math.Clamp(double.IsFinite(input) ? input : 0, -1, 1) : AxisTransform.Apply(input, config.Profile);
                var axes = (double[])snapshot.Axes.Clone();
                axes[config.OutputAxis] = transformed;
                output?.Submit(snapshot with { Axes = axes });
                if (!connected)
                    Volatile.Write(ref _state, new(true, output is null ? "Monitoring · output disabled" : "Routing output"));
                connected = true;
                samples++;
                if (monitoring)
                    Volatile.Write(ref _latest, new(true, input, transformed, samples, interval, State.Message));
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _latest, new(false, 0, 0, samples, 0, ex.Message));
            Volatile.Write(ref _state, new(false, ex.Message));
            throw;
        }
        finally
        {
            // Even if neutralization fails, release the native output and input handles.
            try { output?.Neutralize(); }
            catch (Exception) { /* Device removal may make the last reset impossible. */ }
            try { output?.Dispose(); }
            finally
            {
                try { source?.Dispose(); }
                finally
                {
                    try { visibilityLease?.Dispose(); }
                    finally { if (timingEnabled) TimeEndPeriod(1); }
                }
            }
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")] private static extern uint TimeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")] private static extern uint TimeEndPeriod(uint milliseconds);
}
