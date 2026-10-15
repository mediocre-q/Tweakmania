using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Tweak.Core;
using Tweak.Runtime;
using InputDevice = Tweak.Core.InputDevice;

namespace Tweak.App;

public sealed record AxisChoice(int Index, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class ProfileChoice(string id, string name) : NotifyObject
{
    public string Id { get; } = id;
    private string _name = name;
    private bool _dirty;
    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(DisplayName)); } }
    public bool IsDirty { get => _dirty; set { if (Set(ref _dirty, value)) Raise(nameof(DisplayName)); } }
    public string DisplayName => Name + (IsDirty ? " *" : "");
}

public sealed class CurvePointRow : NotifyObject
{
    private string _inputPercent, _outputPercent;
    public string InputPercent { get => _inputPercent; set => Set(ref _inputPercent, value); }
    public string OutputPercent { get => _outputPercent; set => Set(ref _outputPercent, value); }
    public CurvePointRow(string inputPercent, string outputPercent) { _inputPercent = inputPercent; _outputPercent = outputPercent; }
    public CurvePointRow(double inputPercent, double outputPercent) : this(inputPercent.ToString("0.###", CultureInfo.InvariantCulture), outputPercent.ToString("0.###", CultureInfo.InvariantCulture)) { }
}

public abstract class NotifyObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(propertyName); return true;
    }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class MainViewModel : NotifyObject
{
    private static readonly string StateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControllerTweak");
    private readonly ProfileLibrary _library;
    private readonly Dictionary<string, ProfileDraft> _drafts = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _draftTimer;
    private static readonly string[] StandardAxes = ["X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2"];
    private readonly DeviceCatalog _catalog = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly Func<nint> _windowHandle;
    private readonly bool _persistSettings;
    private Task<InputDevice[]>? _enumerationTask;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private RoutingSession? _session;
    private bool _loading, _transitioning, _disposed;
    private InputDevice? _selectedDevice;
    private AxisChoice? _selectedInputAxis;
    private bool _applyInGame = true, _isRunning, _isDemo, _bypass, _mirror = true, _invert, _editLeftCurve;
    private bool _monitorEnabled, _previewVisible = true;
    private ProxyController? _proxy;
    private readonly Func<ProxyController> _proxyControllerFactory;
    private int? _gameProcessId;
    private string? _legacyRecoveryNotice;
    private bool _proxyInstalled, _proxyPresent, _proxyStatusKnown;
    private string _proxyActionError = "";
    private string _gameExecutablePath = ProxyInstallation.DefaultGameExecutablePath ?? "";
    private string _profileName = "Default", _minimumText = "-100", _centerText = "0", _maximumText = "100", _deadzoneText = "0", _saturationText = "100";
    private string? _selectedProfileId;
    private AxisProfile _savedProfile = new();
    private string _status = "Ready", _detail = "Choose a DirectInput controller, or try the demo to explore the curve editor.", _outputStatus = "Tweakmania proxy is not installed.", _validationMessage = "";
    private double _rawInput, _outputValue;
    private AxisProfile _activeProfile = new();
    private CurvePoint[] _leftCurve = [new(0, 0), new(1, 1)], _rightCurve = [new(0, 0), new(1, 1)];
    private CurveDraft[]? _leftRawRows, _rightRawRows;
    private bool _leftRowsEdited, _rightRowsEdited;
    private CurvePointRow? _selectedPoint;
    private RoutingConfiguration _routingConfiguration = new(new(), 0, 0, false);
    private string? _preferredSourceId;

    public ObservableCollection<InputDevice> Devices { get; } = [];
    public ObservableCollection<InputDevice> DeviceChoices { get; } = [];
    public ObservableCollection<AxisChoice> InputAxes { get; } = [];
    public ObservableCollection<CurvePointRow> CurvePoints { get; } = [];
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StartStopCommand { get; }
    public RelayCommand DemoCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand LoadCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand AddPointCommand { get; }
    public RelayCommand RemovePointCommand { get; }
    public RelayCommand CenterCommand { get; }
    public RelayCommand BrowseGameCommand { get; }
    public RelayCommand InstallProxyCommand { get; }
    public RelayCommand RemoveProxyCommand { get; }
    public RelayCommand NewProfileCommand { get; }
    public RelayCommand DuplicateProfileCommand { get; }
    public RelayCommand RenameProfileCommand { get; }
    public RelayCommand DeleteProfileCommand { get; }
    public RelayCommand RevertProfileCommand { get; }
    public RelayCommand ImportProfilesCommand { get; }
    public RelayCommand ExportProfileCommand { get; }
    public RelayCommand SetupCommand { get; }
    public ObservableCollection<ProfileChoice> Profiles { get; } = [];
    public ObservableCollection<string> CurveSides { get; } = ["Right", "Left"];
    public string EditingSide { get => EditLeftCurve ? "Left" : "Right"; set => EditLeftCurve = value == "Left"; }
    public string SelectedProfileId { get => _selectedProfileId ?? ""; set { if (value != _selectedProfileId) SelectProfile(value); } }
    public bool IsDirty => !ProfilesEqual(_activeProfile, _savedProfile) || !string.IsNullOrEmpty(ValidationMessage);
    public bool CanSave => IsDirty && string.IsNullOrEmpty(ValidationMessage);
    public bool CanExport => string.IsNullOrEmpty(ValidationMessage);
    public string SelectedProfileDisplayName => Profiles.FirstOrDefault(p => p.Id == _selectedProfileId)?.DisplayName ?? _profileName + (IsDirty ? " *" : "");
    public bool CanDeleteProfile => Profiles.Count > 0;

    public InputDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (IsRunning || !Set(ref _selectedDevice, value)) return;
            if (value is not null) _preferredSourceId = value.Id;
            InputAxes.Clear();
            if (value is not null)
                for (var i = 0; i < Math.Min(value.AxisNames.Length, 8); i++)
                    if (!string.IsNullOrWhiteSpace(value.AxisNames[i])) InputAxes.Add(new(i, value.AxisNames[i]));
            SelectedInputAxis = InputAxes.FirstOrDefault();
            StartStopCommand.Refresh();
        }
    }
    public AxisChoice? SelectedInputAxis { get => _selectedInputAxis; set { if (IsRunning) return; if (Set(ref _selectedInputAxis, value)) PublishConfiguration(); } }
    public bool ApplyInGame { get => _applyInGame; set { if (!IsRunning && Set(ref _applyInGame, value)) { RefreshDeviceChoices(); Raise(nameof(StartStopLabel)); Raise(nameof(RunningLabel)); RefreshCommands(); } } }
    public string GameExecutablePath { get => _gameExecutablePath; set { if (!IsRunning && Set(ref _gameExecutablePath, value)) { _gameProcessId = null; ProxyActionError = ""; RefreshProxyStatus(); } } }
    public bool IsProxyInstalled => _proxyInstalled;
    public string ProxyStatusColor => IsProxyInstalled ? "#8FD3A7" : "#FF7F72";
    public bool CanInstallProxy => !_disposed && IsIdle && _proxyStatusKnown && !_proxyPresent && File.Exists(GameExecutablePath);
    public bool CanRemoveProxy => !_disposed && IsIdle && IsProxyInstalled && File.Exists(GameExecutablePath);
    public string ProxyActionError { get => _proxyActionError; private set => Set(ref _proxyActionError, value); }
    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) { Raise(nameof(IsIdle)); Raise(nameof(StartStopLabel)); Raise(nameof(RunningLabel)); RefreshCommands(); RefreshMonitoring(); } } }
    public bool IsIdle => !IsRunning && !_transitioning;
    public bool IsDemo { get => _isDemo; private set { if (Set(ref _isDemo, value)) { Raise(nameof(RunningLabel)); DemoCommand.Refresh(); RefreshMonitoring(); } } }
    public bool MonitorEnabled { get => _monitorEnabled; set { if (Set(ref _monitorEnabled, value)) RefreshMonitoring(); } }
    public bool PreviewVisible { get => _previewVisible; set { if (Set(ref _previewVisible, value)) RefreshMonitoring(); } }
    public bool IsMonitoringActive => IsRunning && PreviewVisible && MonitorEnabled;
    public string PreviewStatus => !IsRunning ? "Preview is idle. Apply a profile or try the demo."
        : !PreviewVisible ? "Preview paused while the window is minimized."
        : IsMonitoringActive ? "Live input and transformed output."
        : ApplyInGame ? "Live preview off · routing continues without visual monitoring." : "Live preview is paused.";
    public bool Bypass { get => _bypass; set { if (Set(ref _bypass, value)) PublishConfiguration(); } }
    public bool Mirror
    {
        get => _mirror;
        set
        {
            if (value == _mirror) return;
            if (value && EditLeftCurve) CaptureVisibleRows();
            if (Set(ref _mirror, value))
            {
                if (value && Set(ref _editLeftCurve, false)) { PopulatePoints(); Raise(nameof(EditLeftCurve)); }
                Raise(nameof(EditingSide)); TryApplyEdits();
            }
        }
    }
    public bool Invert { get => _invert; set { if (Set(ref _invert, value)) TryApplyEdits(); } }
    public bool EditLeftCurve { get => _editLeftCurve; set { value &= !Mirror; if (value != _editLeftCurve) CaptureVisibleRows(); if (Set(ref _editLeftCurve, value)) { PopulatePoints(); Raise(nameof(EditingSide)); MarkDraftChanged(); } } }
    public string ProfileName { get => _profileName; set { if (Set(ref _profileName, value)) TryApplyEdits(); } }
    // The editor displays calibration as percentages of the source axis's advertised range.
    public string MinimumText { get => _minimumText; set { if (Set(ref _minimumText, value)) TryApplyEdits(); } }
    public string CenterText { get => _centerText; set { if (Set(ref _centerText, value)) TryApplyEdits(); } }
    public string MaximumText { get => _maximumText; set { if (Set(ref _maximumText, value)) TryApplyEdits(); } }
    public string DeadzoneText { get => _deadzoneText; set { if (Set(ref _deadzoneText, value)) TryApplyEdits(); } }
    public string SaturationText { get => _saturationText; set { if (Set(ref _saturationText, value)) TryApplyEdits(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public string OutputStatus { get => _outputStatus; private set => Set(ref _outputStatus, value); }
    public string ValidationMessage { get => _validationMessage; private set { if (Set(ref _validationMessage, value)) { SaveCommand.Refresh(); Raise(nameof(IsDirty)); Raise(nameof(CanSave)); Raise(nameof(CanExport)); Raise(nameof(SelectedProfileDisplayName)); } } }
    public double RawInput { get => _rawInput; private set => Set(ref _rawInput, value); }
    public double OutputValue { get => _outputValue; private set => Set(ref _outputValue, value); }
    public AxisProfile ActiveProfile => _activeProfile;
    public CurvePointRow? SelectedPoint { get => _selectedPoint; set { if (Set(ref _selectedPoint, value)) RemovePointCommand.Refresh(); } }
    public string StartStopLabel => IsRunning ? "Stop" : ApplyInGame ? "Apply in game" : "Start monitoring";
    public string RunningLabel => !IsRunning ? "IDLE" : IsDemo ? "DEMO" : ApplyInGame ? "IN-GAME" : "MONITOR";

    internal string? ActiveProxyMappingName => _proxy?.MappingName;
    internal bool HasStandaloneSession => _session is not null;

    public MainViewModel(Func<nint> windowHandle, bool persistSettings = true, Func<ProxyController>? proxyControllerFactory = null, string? storageDirectory = null)
    {
        _windowHandle = windowHandle;
        _persistSettings = persistSettings;
        _library = new ProfileLibrary(storageDirectory ?? StateDirectory, memoryOnly: !persistSettings);
        _proxyControllerFactory = proxyControllerFactory ?? (() => new ProxyController());
        RefreshCommand = new(() => _ = RefreshAsync(), () => IsIdle);
        StartStopCommand = new(() => _ = ToggleAsync(false), () => !_transitioning && (IsRunning || SelectedDevice is not null));
        DemoCommand = new(() => _ = ToggleAsync(true), () => !_transitioning);
        SaveCommand = new(SaveProfile, () => CanSave);
        LoadCommand = new(LoadProfile);
        ResetCommand = new(ResetDraft);
        AddPointCommand = new(AddPoint);
        RemovePointCommand = new(RemovePoint, () => SelectedPoint is not null && CurvePoints.IndexOf(SelectedPoint) > 0 && CurvePoints.IndexOf(SelectedPoint) < CurvePoints.Count - 1);
        CenterCommand = new(() => CenterText = Percent(RawInput), () => IsMonitoringActive);
        BrowseGameCommand = new(BrowseGame, () => !IsRunning);
        InstallProxyCommand = new(InstallProxy, () => CanInstallProxy);
        RemoveProxyCommand = new(RemoveProxy, () => CanRemoveProxy);
        NewProfileCommand = new(CreateNewProfile);
        DuplicateProfileCommand = new(DuplicateProfile, () => CanExport);
        RenameProfileCommand = new(RenameProfile);
        DeleteProfileCommand = new(DeleteProfile, () => CanDeleteProfile);
        RevertProfileCommand = new(RevertProfile, () => IsDirty);
        ImportProfilesCommand = new(ImportProfiles);
        ExportProfileCommand = new(ExportProfile, () => CanExport);
        SetupCommand = new(() => { });
        PopulatePoints();
        _previewTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _previewTimer.Tick += (_, _) => UpdateTelemetry();
        _statusTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateRouteStatus();
        _draftTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _draftTimer.Tick += (_, _) => { _draftTimer.Stop(); PersistDrafts(); };
    }

    public async Task InitializeAsync()
    {
        try
        {
            var directory = _library.DirectoryPath;
            _library.Initialize(_persistSettings ? Path.Combine(directory, "last-profile.json") : null);
            Profiles.Clear(); foreach (var entry in _library.Entries) Profiles.Add(new(entry.Id, entry.Name));
            if (_persistSettings)
            {
                LoadDraftsFromDisk();
                var settingsPath = Path.Combine(directory, "settings.json");
                if (File.Exists(settingsPath))
                {
                    var settings = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(settingsPath));
                    _preferredSourceId = settings?.SourceId;
                    if (!string.IsNullOrWhiteSpace(settings?.GameExecutablePath) && !ProxyInstallation.IsTMLoaderExecutable(settings.GameExecutablePath))
                        GameExecutablePath = settings.GameExecutablePath;
                }
            }
            var selectionPath = Path.Combine(directory, "selected-profile.json");
            var savedId = _persistSettings && File.Exists(selectionPath) ? JsonSerializer.Deserialize<SelectedProfile>(File.ReadAllText(selectionPath))?.Id : null;
            SelectProfile(Profiles.Any(p => p.Id == savedId) ? savedId! : Profiles[0].Id);
        }
        catch (Exception ex) { Detail = $"Saved settings could not be loaded: {ex.Message}"; }
        if (_disposed) return;
        try
        {
            if (_persistSettings && await Task.Run(HidHideService.RecoverStaleLease))
                _legacyRecoveryNotice = "Recovered settings left by a previous source-hiding session.";
        }
        catch (Exception ex)
        {
            _legacyRecoveryNotice = $"Previous source-hiding settings could not be recovered: {ex.Message}";
        }
        await RefreshAsync();
        if (_legacyRecoveryNotice is not null) Detail = $"{Detail} {_legacyRecoveryNotice}";
    }

    private async Task RefreshAsync()
    {
        if (!IsIdle || _disposed) return;
        _transitioning = true; Raise(nameof(IsIdle)); RefreshCommands();
        try
        {
            _enumerationTask = Task.Run(_catalog.Enumerate);
            var devices = await _enumerationTask;
            if (_disposed) return;
            Devices.Clear();
            foreach (var device in devices) Devices.Add(device);
            _selectedDevice = null;
            InputAxes.Clear(); _selectedInputAxis = null;
            RefreshDeviceChoices();
            SelectedDevice = DeviceChoices.FirstOrDefault(d => d.Id == _preferredSourceId) ?? DeviceChoices.FirstOrDefault();
            Raise(nameof(SelectedDevice));
            RefreshProxyStatus();
            Status = devices.Length == 0 ? "No controllers found" : "Ready";
            Detail = devices.All(d => d.Api != InputApi.DirectInput) ? "Connect a DirectInput controller. In-game routing uses its DirectInput instance identity; XInput devices are available for standalone monitoring." : "Install the in-game proxy, choose a DirectInput controller and axis, then apply the profile.";
        }
        catch (Exception ex) { Status = "Device discovery failed"; Detail = ex.Message; }
        finally { _transitioning = false; Raise(nameof(IsIdle)); RefreshCommands(); }
    }

    private async Task ToggleAsync(bool demo)
    {
        if (_transitioning || _disposed) return;
        _transitioning = true; Raise(nameof(IsIdle)); RefreshCommands();
        try
        {
            var wasRunning = IsRunning;
            var wasDemo = IsDemo;
            await StopInternalAsync();
            if (_disposed) return;
            if (wasRunning && (!demo || wasDemo)) return;
            if (!demo && (SelectedDevice is null || SelectedInputAxis is null))
                throw new InvalidOperationException("Choose a source and an input axis first.");
            if (!string.IsNullOrEmpty(ValidationMessage))
                throw new InvalidOperationException("Correct the highlighted profile settings before starting.");
            var device = SelectedDevice;
            IsDemo = demo;
            if (demo || !ApplyInGame)
            {
                if (!demo && device is null) throw new InvalidOperationException("Choose a source before standalone monitoring.");
                MonitorEnabled = true;
                var handle = _windowHandle();
                _session = new RoutingSession(
                    demo ? () => new DemoSource() : () => _catalog.Open(device!, handle),
                    null, () => Volatile.Read(ref _routingConfiguration));
                var cancellation = new CancellationTokenSource();
                _runCancellation = cancellation;
                IsRunning = true;
                Status = demo ? "Demo · no controller output" : "Starting standalone monitor…";
                Detail = demo ? "Simulated steering. Edit the curve and watch the output change." : "Reading the selected controller for the preview only.";
                _runTask = _session.RunAsync(cancellation.Token);
                RefreshMonitoring();
                _statusTimer.Start();
                _ = ObserveRunAsync(_runTask, cancellation);
                return;
            }
            if (device?.Api != InputApi.DirectInput)
                throw new InvalidOperationException("In-game routing requires a DirectInput controller instance. Select the DirectInput entry for this controller.");
            if (!File.Exists(GameExecutablePath)) throw new FileNotFoundException("Choose the game's executable file.", GameExecutablePath);
            if (!Guid.TryParse(device.Id["directinput:".Length..], out var deviceId))
                throw new InvalidOperationException("The selected DirectInput device has no valid instance GUID.");
            var axis = SelectedInputAxis?.Index ?? throw new InvalidOperationException("Choose the steering axis first.");
            _proxy?.Dispose();
            _proxy = _proxyControllerFactory();
            PublishConfiguration();
            _proxy.Publish(deviceId, axis, ActiveProfile, true, Bypass, MonitorEnabled && PreviewVisible);
            IsRunning = true;
            Status = "Waiting for game";
            Detail = "The profile is published. Start or restart the game to load the proxy; routing is active only after game telemetry appears.";
            RefreshMonitoring();
            _statusTimer.Start();
            UpdateRouteStatus();
        }
        catch (Exception ex) { Status = "Could not start"; Detail = ex.Message; }
        finally { _transitioning = false; Raise(nameof(IsIdle)); RefreshCommands(); }
    }

    private async Task ObserveRunAsync(Task task, CancellationTokenSource cancellation)
    {
        var wasDemo = IsDemo;
        string? failure = null;
        try { await task; }
        catch (Exception ex) { failure = ex.Message; }
        if (_runCancellation != cancellation) return;
        _previewTimer.Stop();
        _statusTimer.Stop();
        _runCancellation = null; _runTask = null; _session = null;
        cancellation.Dispose();
        RawInput = 0; OutputValue = 0; IsRunning = false; IsDemo = false;
        Status = failure is null ? "Stopped" : "Monitoring stopped";
        Detail = failure ?? (wasDemo ? "Demo stopped." : "Standalone monitor stopped and its source released.");
    }

    private async Task StopInternalAsync()
    {
        var cancellation = _runCancellation;
        var task = _runTask;
        if (cancellation is null || task is null)
        {
            if (_proxy is not null)
            {
                _proxy.Dispose(); _proxy = null;
                _gameProcessId = null;
                _statusTimer.Stop(); _previewTimer.Stop();
                IsRunning = false; RawInput = 0; OutputValue = 0;
                Status = "Stopped"; Detail = "In-game profile disabled. The game may need restarting to unload the proxy.";
            }
            return;
        }
        var wasDemo = IsDemo;
        cancellation.Cancel();
        Exception? stopFailure = null;
        try { await task; } catch (Exception ex) { stopFailure = ex; }
        // ObserveRunAsync is also awaiting this task; yield so its UI continuation can finish.
        if (_runCancellation == cancellation)
        {
            _runCancellation = null; _runTask = null; _session = null;
            cancellation.Dispose();
            _previewTimer.Stop();
            _statusTimer.Stop();
            IsRunning = false; IsDemo = false; RawInput = 0; OutputValue = 0;
            Status = stopFailure is null ? "Stopped" : "Monitoring stopped";
            Detail = stopFailure?.Message ?? (wasDemo ? "Demo stopped." : "Standalone monitor stopped and its source released.");
        }
    }

    private void UpdateTelemetry()
    {
        if (_proxy is not null) { UpdateProxyTelemetry(); return; }
        if (_session is null) return;
        var state = _session.Latest;
        RawInput = state.Input; OutputValue = state.Output;
        Status = IsDemo ? "Demo · no controller output" : state.Message;
        Detail = IsDemo ? "Simulated steering · live edits apply immediately · no controller output" : state.Connected
            ? $"{state.Samples:N0} samples · standalone preview only"
            : state.Message;
    }

    private void UpdateRouteStatus()
    {
        if (_proxy is not null) { UpdateProxyStatus(); return; }
        if (_session is null || IsMonitoringActive) return;
        var state = _session.State;
        Status = state.Message;
        Detail = state.Connected ? "Standalone monitoring continues · live preview is off." : state.Message;
    }

    private void RefreshMonitoring()
    {
        Raise(nameof(IsMonitoringActive)); Raise(nameof(PreviewStatus));
        PublishConfiguration();
        // During construction the timer has not been initialized yet.
        if (_previewTimer is not null)
        {
            if (IsMonitoringActive) _previewTimer.Start();
            else { _previewTimer.Stop(); RawInput = 0; OutputValue = 0; }
        }
        CenterCommand?.Refresh();
    }

    private void TryApplyEdits()
    {
        if (_loading) return;
        try
        {
            CaptureVisibleRows();
            var currentRowsEdited = EditLeftCurve ? _leftRowsEdited : _rightRowsEdited;
            var editedCurve = currentRowsEdited
                ? CurvePoints.Select(p => new CurvePoint(ParseNumber(p.InputPercent) / 100, ParseNumber(p.OutputPercent) / 100)).ToArray()
                : EditLeftCurve ? _leftCurve : _rightCurve;
            var leftCurve = EditLeftCurve ? editedCurve : _leftRowsEdited ? ParseRawCurve(_leftRawRows) ?? _leftCurve : _leftCurve;
            var rightCurve = EditLeftCurve ? _rightRowsEdited ? ParseRawCurve(_rightRawRows) ?? _rightCurve : _rightCurve : editedCurve;
            var next = new AxisProfile
            {
                Name = ProfileName.Trim(), InputMinimum = ParsePercent(MinimumText), InputCenter = ParsePercent(CenterText), InputMaximum = ParsePercent(MaximumText),
                Deadzone = ParsePercent(DeadzoneText), Saturation = ParsePercent(SaturationText), Invert = Invert, Mirror = Mirror,
                LeftCurve = leftCurve,
                RightCurve = rightCurve
            };
            if (next.LeftCurve.Length > 32 || next.RightCurve.Length > 32)
                throw new ArgumentException("The in-game proxy supports at most 32 curve points per side.");
            next.Validate();
            _leftCurve = next.LeftCurve; _rightCurve = next.RightCurve;
            _leftRowsEdited = false; _rightRowsEdited = false;
            _activeProfile = next;
            ValidationMessage = "";
            Raise(nameof(ActiveProfile)); MarkDraftChanged(); PublishConfiguration();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        { ValidationMessage = $"Edits not applied: {ex.Message}"; MarkDraftChanged(); }
    }

    private static CurvePoint[]? ParseRawCurve(CurveDraft[]? rows) => rows is null || rows.Length == 0 ? null : rows.Select(p => new CurvePoint(ParseNumber(p.Input) / 100, ParseNumber(p.Output) / 100)).ToArray();
    private static CurveDraft[] ToCurveDrafts(IEnumerable<CurvePoint> points) => points.Select(p => new CurveDraft((p.Input * 100).ToString("R", CultureInfo.InvariantCulture), (p.Output * 100).ToString("R", CultureInfo.InvariantCulture))).ToArray();

    private static double ParsePercent(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            throw new FormatException("Enter a number in each calibration field.");
        return value / 100;
    }
    private static double ParseNumber(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            throw new FormatException("Enter a number for every curve point.");
        return value;
    }
    private static string Percent(double value) => (value * 100).ToString("R", CultureInfo.CurrentCulture);
    private void PublishConfiguration()
    {
        Volatile.Write(ref _routingConfiguration,
            new(_activeProfile, IsDemo ? 0 : SelectedInputAxis?.Index ?? 0, IsDemo ? 0 : SelectedInputAxis?.Index ?? 0, Bypass, IsMonitoringActive));
        if (_proxy is not null && IsRunning)
        {
            try { _proxy.Publish(ParseSelectedDeviceGuid(), SelectedInputAxis?.Index ?? 0, ActiveProfile, true, Bypass, IsMonitoringActive); }
            catch (Exception ex) { Status = "Profile publish failed"; Detail = ex.Message; }
        }
    }

    private Guid ParseSelectedDeviceGuid()
    {
        var id = SelectedDevice?.Id;
        return id is not null && id.StartsWith("directinput:", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(id["directinput:".Length..], out var guid) ? guid : Guid.Empty;
    }

    private void UpdateProxyTelemetry()
    {
        if (!IsMonitoringActive) return;
        if (_gameProcessId is not { } processId || !ProxyController.TryReadTelemetry(processId, out var telemetry) ||
            telemetry.ConfigurationSequence != _proxy?.ConfigurationSequence || telemetry.Status != 3 || telemetry.SampleCount == 0 ||
            unchecked((ulong)Environment.TickCount64 - telemetry.TickCount) > 1000)
        {
            RawInput = 0; OutputValue = 0;
            return;
        }
        RawInput = telemetry.Input; OutputValue = telemetry.Output;
    }

    private void UpdateProxyStatus()
    {
        if (string.IsNullOrWhiteSpace(GameExecutablePath))
        {
            _gameProcessId = null;
            RawInput = 0; OutputValue = 0;
            Status = IsRunning ? "Waiting for game" : "Ready";
            Detail = "Choose the game's executable.";
            return;
        }
        using var process = ProxyController.FindGameProcess(GameExecutablePath);
        if (process is null)
        {
            _gameProcessId = null;
            RawInput = 0; OutputValue = 0;
            Status = IsRunning ? "Waiting for game" : "Ready";
            Detail = IsRunning ? "The profile is published. Launch the configured game to load the proxy." : "The game is not running.";
            return;
        }
        var processId = process.Id;
        if (_gameProcessId != processId) { RawInput = 0; OutputValue = 0; }
        _gameProcessId = processId;
        if (!ProxyController.TryReadTelemetry(processId, out var telemetry))
        {
            Status = IsRunning ? "Game running · proxy not loaded" : "Game running · proxy not detected";
            Detail = "If the game was already open, restart it so DirectInput loads the proxy.";
            return;
        }
        if (_proxy is not null && telemetry.ConfigurationSequence != _proxy.ConfigurationSequence)
        {
            Status = "Applying profile…";
            Detail = "The proxy is loaded. Waiting for it to acknowledge the latest settings.";
            return;
        }
        switch (telemetry.Status)
        {
            case 1:
                Status = "Proxy loaded";
                Detail = telemetry.SampleCount > 0 ? $"Game input sampled · {telemetry.SampleCount:N0} samples · profile edits apply live." : "Proxy is loaded. Waiting for the selected controller to be created by the game.";
                break;
            case 2:
                Status = "Selected controller created";
                Detail = "Waiting for successful input samples from the selected controller.";
                break;
            case 3:
                Status = "In-game routing active";
                Detail = IsMonitoringActive ? $"{telemetry.SampleCount:N0} preview samples · profile edits apply live."
                    : "The game is reading the selected axis · live preview is off.";
                break;
            case 4:
                Status = "Proxy loaded · unsupported mode";
                Detail = "The proxy encountered an unsupported DirectInput path. That path passes through; supported device input may still be transformed.";
                break;
            case 5:
                Status = "Controller input unavailable";
                Detail = "Waiting for the game to acquire the controller again. Check its connection and game focus.";
                break;
            default:
                Status = "Proxy loaded · waiting for device";
                Detail = "Waiting for the selected DirectInput controller to be created by the game.";
                break;
        }
    }

    internal void RefreshProxyStatus()
    {
        _proxyInstalled = false; _proxyPresent = false; _proxyStatusKnown = false;
        if (string.IsNullOrWhiteSpace(GameExecutablePath)) OutputStatus = "Choose the game's executable.";
        else
        {
            try
            {
                var installation = ProxyInstallation.Describe(GameExecutablePath);
                _proxyInstalled = installation.Installed && installation.OwnedByControllerTweak;
                _proxyPresent = installation.Installed;
                _proxyStatusKnown = true;
                OutputStatus = installation.Message;
            }
            catch (Exception ex) { OutputStatus = $"Proxy status unavailable: {ex.Message}"; }
        }
        Raise(nameof(IsProxyInstalled)); Raise(nameof(ProxyStatusColor));
        RefreshProxyCommands();
    }

    private void RefreshProxyCommands()
    {
        Raise(nameof(CanInstallProxy)); Raise(nameof(CanRemoveProxy));
        InstallProxyCommand?.Refresh(); RemoveProxyCommand?.Refresh();
    }

    private void RefreshDeviceChoices()
    {
        DeviceChoices.Clear();
        foreach (var device in Devices.Where(d => !ApplyInGame || d.Api == InputApi.DirectInput))
            DeviceChoices.Add(device);
        if (SelectedDevice is not null && !DeviceChoices.Contains(SelectedDevice))
        {
            _selectedDevice = null;
            InputAxes.Clear(); _selectedInputAxis = null;
            Raise(nameof(SelectedDevice)); Raise(nameof(SelectedInputAxis));
            if (DeviceChoices.Count > 0) SelectedDevice = DeviceChoices[0];
        }
    }

    private void BrowseGame()
    {
        var dialog = new OpenFileDialog { Filter = "Game executable (*.exe)|*.exe|All files (*.*)|*.*", CheckFileExists = true, FileName = GameExecutablePath };
        if (dialog.ShowDialog() == true) GameExecutablePath = dialog.FileName;
    }

    private void InstallProxy()
    {
        if (!CanInstallProxy) return;
        ProxyActionError = "";
        try
        {
            var bundledProxyPath = Path.Combine(AppContext.BaseDirectory, "native", "x86", "dinput8.dll");
            var status = ProxyInstallation.Install(GameExecutablePath, bundledProxyPath);
            OutputStatus = status.Message;
            Status = "Proxy installed";
            Detail = status.Message;
        }
        catch (Exception ex) { ProxyActionError = $"Could not install proxy: {ex.Message}"; Status = "Proxy install failed"; Detail = ProxyActionError; }
        RefreshProxyStatus();
    }

    private void RemoveProxy()
    {
        if (!CanRemoveProxy) return;
        ProxyActionError = "";
        try
        {
            var status = ProxyInstallation.Remove(GameExecutablePath);
            OutputStatus = status.Message;
            Status = "Proxy removed";
            Detail = status.Message;
        }
        catch (Exception ex) { ProxyActionError = $"Could not remove proxy: {ex.Message}"; Status = "Proxy removal failed"; Detail = ProxyActionError; }
        RefreshProxyStatus();
    }

    internal void ApplyProfile(AxisProfile profile)
    {
        LoadEditorSnapshot(profile, true);
    }

    private void LoadEditorSnapshot(AxisProfile profile, bool publish)
    {
        profile.Validate();
        if (profile.LeftCurve.Length > 32 || profile.RightCurve.Length > 32)
            throw new ArgumentException("The in-game proxy supports at most 32 curve points per side.", nameof(profile));
        _loading = true;
        try
        {
            _activeProfile = CopyProfile(profile);
            _savedProfile = _selectedProfileId is null ? CopyProfile(profile) : _library.Load(_selectedProfileId);
            _leftCurve = profile.LeftCurve.ToArray(); _rightCurve = profile.RightCurve.ToArray();
            _leftRawRows = null; _rightRawRows = null;
            _leftRowsEdited = false; _rightRowsEdited = false;
            ProfileName = profile.Name; MinimumText = Percent(profile.InputMinimum); CenterText = Percent(profile.InputCenter); MaximumText = Percent(profile.InputMaximum);
            DeadzoneText = Percent(profile.Deadzone); SaturationText = Percent(profile.Saturation);
            // Loading a different profile must not run public edit setters: Mirror can
            // capture the previous profile's visible raw rows when leaving Left edit mode.
            _mirror = profile.Mirror; _invert = profile.Invert;
            if (_mirror) _editLeftCurve = false;
            PopulatePoints();
            ValidationMessage = ""; Raise(nameof(Mirror)); Raise(nameof(Invert)); Raise(nameof(ActiveProfile)); Raise(nameof(EditLeftCurve)); Raise(nameof(EditingSide)); PublishConfiguration();
            Raise(nameof(IsDirty)); Raise(nameof(CanSave)); Raise(nameof(CanExport)); Raise(nameof(SelectedProfileDisplayName));
        }
        finally { _loading = false; }
    }

    private void PopulatePoints()
    {
        var wasLoading = _loading; _loading = true;
        try
        {
            foreach (var row in CurvePoints) row.PropertyChanged -= OnPointChanged;
            CurvePoints.Clear();
            var rawRows = EditLeftCurve ? _leftRawRows : _rightRawRows;
            foreach (var point in rawRows ?? ToCurveDrafts(EditLeftCurve ? _leftCurve : _rightCurve))
            {
                var row = new CurvePointRow(point.Input, point.Output);
                row.PropertyChanged += OnPointChanged; CurvePoints.Add(row);
            }
            SelectedPoint = CurvePoints.Count > 2 ? CurvePoints[1] : null;
        }
        finally { _loading = wasLoading; }
    }
    private void OnPointChanged(object? sender, PropertyChangedEventArgs args) { CaptureVisibleRows(); if (EditLeftCurve && !Mirror) _leftRowsEdited = true; else _rightRowsEdited = true; TryApplyEdits(); }
    private void CaptureVisibleRows()
    {
        var rows = CurvePoints.Select(p => new CurveDraft(p.InputPercent, p.OutputPercent)).ToArray();
        if (EditLeftCurve && !Mirror) _leftRawRows = rows; else _rightRawRows = rows;
    }
    public void UpdateCurveFromGraph(CurvePoint[] points)
    {
        if (!string.IsNullOrEmpty(ValidationMessage)) return;
        var profile = EditLeftCurve ? _activeProfile with { LeftCurve = points } : _activeProfile with { RightCurve = points };
        try
        {
            profile.Validate();
            _leftCurve = profile.LeftCurve.ToArray(); _rightCurve = profile.RightCurve.ToArray();
            _activeProfile = profile;
            if (EditLeftCurve && !Mirror) { _leftRawRows = null; _leftRowsEdited = false; }
            else { _rightRawRows = null; _rightRowsEdited = false; }
            PopulatePoints(); Raise(nameof(ActiveProfile)); ValidationMessage = ""; MarkDraftChanged(); PublishConfiguration();
        }
        catch (ArgumentException ex) { ValidationMessage = ex.Message; }
    }
    private void AddPoint()
    {
        if (CurvePoints.Count >= 32) { ValidationMessage = "The in-game proxy supports at most 32 points per curve side."; return; }
        (double Input, double Output)[] valid;
        try { valid = CurvePoints.Select(p => (Input: ParseNumber(p.InputPercent), Output: ParseNumber(p.OutputPercent))).ToArray(); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { ValidationMessage = ex.Message; MarkDraftChanged(); return; }
        var best = Enumerable.Range(0, CurvePoints.Count - 1).MaxBy(i => valid[i + 1].Input - valid[i].Input);
        var a = valid[best]; var b = valid[best + 1];
        var row = new CurvePointRow((a.Input + b.Input) / 2, (a.Output + b.Output) / 2);
        row.PropertyChanged += OnPointChanged;
        CurvePoints.Insert(best + 1, row); SelectedPoint = row; if (EditLeftCurve && !Mirror) _leftRowsEdited = true; else _rightRowsEdited = true; TryApplyEdits();
    }
    private void RemovePoint()
    {
        if (!RemovePointCommand.CanExecute(null) || SelectedPoint is null) return;
        var row = SelectedPoint; row.PropertyChanged -= OnPointChanged; CurvePoints.Remove(row); SelectedPoint = null; if (EditLeftCurve && !Mirror) _leftRowsEdited = true; else _rightRowsEdited = true; TryApplyEdits();
    }

    private void SaveProfile()
    {
        if (_selectedProfileId is null) return;
        CommitPendingGridEdit();
        if (!CanSave) return;
        try
        {
            var name = _library.UniqueName(ProfileName, _selectedProfileId);
            var profile = _activeProfile with { Name = name };
            _library.Save(_selectedProfileId, profile);
            _savedProfile = CopyProfile(profile); _activeProfile = profile; _profileName = name;
            Raise(nameof(ProfileName)); _drafts[_selectedProfileId] = CaptureDraft();
            var draftsPersisted = PersistDrafts();
            NotifyProfileState();
            if (draftsPersisted) { Status = "Profile saved"; Detail = $"Saved {name}."; }
        }
        catch (Exception ex) { Status = "Save failed"; Detail = ex.Message; }
    }
    private void LoadProfile()
    {
        var dialog = new OpenFileDialog { Filter = "Tweakmania profile (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        try { ApplyProfile(ProfileStore.Load(dialog.FileName)); Status = "Profile loaded"; Detail = dialog.FileName; }
        catch (Exception ex) { Status = "Load failed"; Detail = ex.Message; }
    }

    private void SelectProfile(string id)
    {
        if (!Profiles.Any(e => e.Id == id)) return;
        CommitPendingGridEdit();
        if (_selectedProfileId is not null) SaveCurrentDraft();
        try
        {
            var saved = CopyProfile(_library.Load(id));
            var draft = _drafts.GetValueOrDefault(id);
            if (draft is not null)
            {
                draft.LastValidProfile.Validate();
                if (draft.LastValidProfile.LeftCurve.Length > 32 || draft.LastValidProfile.RightCurve.Length > 32) throw new ArgumentException("The saved draft exceeds the proxy's 32-point limit.");
            }
            _selectedProfileId = id;
            _savedProfile = saved;
            LoadEditorSnapshot(draft?.LastValidProfile ?? _savedProfile, false);
            if (draft is not null) RestoreDraftFields(draft);
            else { _profileName = _savedProfile.Name; _drafts[id] = CaptureDraft(); }
            Raise(nameof(SelectedProfileId)); NotifyProfileState();
            if (PersistSelectedProfileId())
            { Status = "Profile selected"; Detail = IsDirty ? $"{ProfileName} has unsaved changes." : $"Editing {ProfileName}."; }
        }
        catch (Exception ex) { Raise(nameof(SelectedProfileId)); Status = "Profile load failed"; Detail = ex.Message; }
    }

    private void SaveCurrentDraft()
    {
        if (_selectedProfileId is null) return;
        _drafts[_selectedProfileId] = CaptureDraft();
        ScheduleDraftSave();
    }

    private ProfileDraft CaptureDraft() => new(
        _activeProfile, ProfileName, MinimumText, CenterText, MaximumText, DeadzoneText, SaturationText,
        Mirror, Invert, EditLeftCurve, _leftRawRows ?? ToCurveDrafts(_leftCurve), _rightRawRows ?? ToCurveDrafts(_rightCurve), _leftRowsEdited, _rightRowsEdited, ValidationMessage);

    private void RestoreDraftFields(ProfileDraft draft)
    {
        _loading = true;
        try
        {
            _profileName = draft.Name;
            _minimumText = draft.MinimumText; _centerText = draft.CenterText; _maximumText = draft.MaximumText;
            _deadzoneText = draft.DeadzoneText; _saturationText = draft.SaturationText;
            _mirror = draft.Mirror; _invert = draft.Invert; _editLeftCurve = draft.Mirror ? false : draft.EditLeftCurve;
            _leftCurve = draft.LastValidProfile.LeftCurve.ToArray(); _rightCurve = draft.LastValidProfile.RightCurve.ToArray();
            _leftRawRows = draft.LeftRows.Length == 0 ? ToCurveDrafts(draft.LastValidProfile.LeftCurve) : draft.LeftRows.ToArray();
            _rightRawRows = draft.RightRows.Length == 0 ? ToCurveDrafts(draft.LastValidProfile.RightCurve) : draft.RightRows.ToArray();
            _leftRowsEdited = draft.LeftRowsEdited; _rightRowsEdited = draft.RightRowsEdited;
            CurvePoints.Clear();
            foreach (var item in draft.Mirror ? _rightRawRows : draft.EditLeftCurve ? _leftRawRows : _rightRawRows)
            {
                var row = new CurvePointRow(item.Input, item.Output); row.PropertyChanged += OnPointChanged; CurvePoints.Add(row);
            }
            _activeProfile = CopyProfile(draft.LastValidProfile);
            ValidationMessage = draft.ValidationMessage;
            Raise(nameof(ProfileName)); Raise(nameof(MinimumText)); Raise(nameof(CenterText)); Raise(nameof(MaximumText)); Raise(nameof(DeadzoneText)); Raise(nameof(SaturationText));
            Raise(nameof(Mirror)); Raise(nameof(Invert)); Raise(nameof(EditLeftCurve)); Raise(nameof(ActiveProfile)); PublishConfiguration();
        }
        finally { _loading = false; }
    }

    private void MarkDraftChanged()
    {
        if (_loading || _selectedProfileId is null) return;
        _drafts[_selectedProfileId] = CaptureDraft();
        NotifyProfileState(); ScheduleDraftSave();
    }

    private void ScheduleDraftSave()
    {
        if (!_persistSettings) return;
        _draftTimer.Stop(); _draftTimer.Start();
    }

    private bool PersistDrafts()
    {
        if (!_persistSettings) return true;
        try
        {
            Directory.CreateDirectory(_library.DirectoryPath);
            foreach (var (id, draft) in _drafts)
                if (Guid.TryParse(id, out _) && _library.Entries.Any(entry => entry.Id == id))
                    AtomicWrite(Path.Combine(_library.DirectoryPath, $"draft-{id}.json"), JsonSerializer.Serialize(draft));
            return true;
        }
        catch (Exception ex) { Status = "Draft recovery save failed"; Detail = ex.Message; return false; }
    }

    private bool PersistSelectedProfileId()
    {
        if (!_persistSettings || _selectedProfileId is null) return true;
        try
        {
            Directory.CreateDirectory(_library.DirectoryPath);
            AtomicWrite(Path.Combine(_library.DirectoryPath, "selected-profile.json"), JsonSerializer.Serialize(new SelectedProfile(_selectedProfileId)));
            return true;
        }
        catch (Exception ex) { Status = "Profile selection could not be saved"; Detail = ex.Message; return false; }
    }

    private static void AtomicWrite(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, text); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void CommitPendingGridEdit()
    {
        if (Application.Current?.MainWindow is MainWindow window) window.CommitCurveGridEdit();
    }

    private void NotifyProfileState()
    {
        Raise(nameof(IsDirty)); Raise(nameof(CanSave)); Raise(nameof(CanExport)); Raise(nameof(SelectedProfileDisplayName)); Raise(nameof(CanDeleteProfile));
        SaveCommand.Refresh(); ExportProfileCommand.Refresh(); DuplicateProfileCommand.Refresh(); RevertProfileCommand.Refresh(); DeleteProfileCommand.Refresh();
        RefreshProfileEntries();
    }

    private void RefreshProfileEntries()
    {
        foreach (var entry in _library.Entries)
        {
            var choice = Profiles.FirstOrDefault(p => p.Id == entry.Id);
            if (choice is null) { choice = new(entry.Id, entry.Name); Profiles.Add(choice); }
            choice.Name = entry.Id == _selectedProfileId ? ProfileName : _drafts.TryGetValue(entry.Id, out var draft) ? draft.Name : entry.Name;
            choice.IsDirty = entry.Id == _selectedProfileId ? IsDirty : _drafts.TryGetValue(entry.Id, out draft) &&
                (!string.IsNullOrEmpty(draft.ValidationMessage) || !ProfilesEqual(draft.LastValidProfile, _library.Load(entry.Id)));
        }
        foreach (var stale in Profiles.Where(p => !_library.Entries.Any(e => e.Id == p.Id)).ToArray()) Profiles.Remove(stale);
    }

    private static AxisProfile CopyProfile(AxisProfile profile) => profile with { LeftCurve = profile.LeftCurve.ToArray(), RightCurve = profile.RightCurve.ToArray() };
    private static bool ProfilesEqual(AxisProfile a, AxisProfile b) =>
        a.Name == b.Name && a.SchemaVersion == b.SchemaVersion && a.InputMinimum == b.InputMinimum && a.InputCenter == b.InputCenter && a.InputMaximum == b.InputMaximum &&
        a.Deadzone == b.Deadzone && a.Saturation == b.Saturation && a.Invert == b.Invert && a.Mirror == b.Mirror && a.LeftCurve.SequenceEqual(b.LeftCurve) && a.RightCurve.SequenceEqual(b.RightCurve);

    private void CreateNewProfile()
    {
        var name = NameDialog.Show("New profile", "Profile name", "New profile", Application.Current?.MainWindow, "Create");
        if (name is null) return;
        try { var entry = _library.Add(new AxisProfile(), name); RefreshProfileEntries(); SelectProfile(entry.Id); }
        catch (Exception ex) { Status = "Profile creation failed"; Detail = ex.Message; }
    }

    private void DuplicateProfile()
    {
        CommitPendingGridEdit();
        if (!CanExport) return;
        var initial = $"{ProfileName} copy";
        var name = NameDialog.Show("Duplicate profile", "Profile name", initial, Application.Current?.MainWindow, "Duplicate");
        if (name is null) return;
        try { var entry = _library.Add(_activeProfile with { Name = name }, name); RefreshProfileEntries(); SelectProfile(entry.Id); }
        catch (Exception ex) { Status = "Profile duplication failed"; Detail = ex.Message; }
    }

    private void RenameProfile()
    {
        if (_selectedProfileId is null) return;
        var name = NameDialog.Show("Rename profile", "Profile name", ProfileName, Application.Current?.MainWindow, "Rename");
        if (name is null) return;
        try { ProfileName = _library.UniqueName(name, _selectedProfileId); MarkDraftChanged(); Status = "Profile renamed"; Detail = "Save to keep the new name."; }
        catch (Exception ex) { Status = "Rename failed"; Detail = ex.Message; }
    }

    private void DeleteProfile()
    {
        if (_selectedProfileId is null || !CanDeleteProfile) return;
        var id = _selectedProfileId;
        if (MessageBox.Show(Application.Current?.MainWindow, $"Delete '{ProfileName}' and its unsaved changes?", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { _library.Delete(id); _drafts.Remove(id); if (_persistSettings) { try { File.Delete(Path.Combine(_library.DirectoryPath, $"draft-{id}.json")); } catch { } } RefreshProfileEntries(); SelectProfile(_library.Entries[0].Id); Status = "Profile deleted"; }
        catch (Exception ex) { Status = "Delete failed"; Detail = ex.Message; }
    }

    private void RevertProfile()
    {
        if (_selectedProfileId is null || !IsDirty) return;
        if (MessageBox.Show(Application.Current?.MainWindow, $"Discard unsaved changes to '{ProfileName}'?", "Revert profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        LoadEditorSnapshot(_library.Load(_selectedProfileId), false); _drafts[_selectedProfileId] = CaptureDraft();
        NotifyProfileState();
        if (PersistDrafts()) { Status = "Changes reverted"; Detail = $"Restored {ProfileName}."; }
    }

    private void ResetDraft()
    {
        var name = string.IsNullOrWhiteSpace(ProfileName) ? _savedProfile.Name : ProfileName;
        LoadEditorSnapshot(new AxisProfile { Name = name }, false);
        MarkDraftChanged(); Status = "Settings reset"; Detail = "Default calibration and curves are in the selected profile draft.";
    }

    internal void InitializeProfileLibraryForTest()
    {
        _library.Initialize(); Profiles.Clear(); foreach (var entry in _library.Entries) Profiles.Add(new(entry.Id, entry.Name));
        if (_persistSettings)
        {
            LoadDraftsFromDisk();
            var savedPath = Path.Combine(_library.DirectoryPath, "selected-profile.json");
            try
            {
                var id = File.Exists(savedPath) ? JsonSerializer.Deserialize<SelectedProfile>(File.ReadAllText(savedPath))?.Id : null;
                SelectProfile(Profiles.Any(e => e.Id == id) ? id! : _library.Entries[0].Id);
            }
            catch { SelectProfile(_library.Entries[0].Id); }
        }
        else SelectProfile(_library.Entries[0].Id);
    }

    private void LoadDraftsFromDisk()
    {
        foreach (var profile in Profiles)
        {
            var draftPath = Path.Combine(_library.DirectoryPath, $"draft-{profile.Id}.json");
            try
            {
                if (!File.Exists(draftPath)) continue;
                var draft = JsonSerializer.Deserialize<ProfileDraft>(File.ReadAllText(draftPath));
                if (draft?.LastValidProfile is null || draft.LeftRows is null || draft.RightRows is null) continue;
                draft.LastValidProfile.Validate();
                if (draft.LastValidProfile.LeftCurve.Length <= 32 && draft.LastValidProfile.RightCurve.Length <= 32) _drafts[profile.Id] = draft;
            }
            catch (Exception ex) { Detail = $"Draft for {profile.Name} could not be loaded: {ex.Message}"; }
        }
    }

    internal string AddProfileForTest(string name, AxisProfile? profile = null)
    {
        var entry = _library.Add(profile ?? new AxisProfile { Name = name }, name);
        RefreshProfileEntries(); return entry.Id;
    }

    private void ImportProfiles()
    {
        var dialog = new OpenFileDialog { Filter = "Tweakmania profiles (*.json)|*.json|All files (*.*)|*.*", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;
        var result = ImportProfileFiles(dialog.FileNames);
        var message = $"Imported {result.Imported} profile(s)." + (result.Failures.Length == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, result.Failures));
        MessageBox.Show(Application.Current?.MainWindow, message, "Import profiles", MessageBoxButton.OK, result.Failures.Length == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        Status = result.Failures.Length == 0 ? "Profiles imported" : "Some profiles could not be imported"; Detail = message;
    }

    internal (int Imported, string[] Failures) ImportProfileFiles(IEnumerable<string> files)
    {
        var failures = new List<string>(); var imported = 0;
        foreach (var file in files)
        {
            try
            {
                var profile = ProfileStore.Load(file);
                if (profile.LeftCurve.Length > 32 || profile.RightCurve.Length > 32) throw new ArgumentException("More than 32 points per side is unsupported.");
                _library.Add(profile, profile.Name); imported++;
            }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
        }
        RefreshProfileEntries();
        return (imported, failures.ToArray());
    }

    private void ExportProfile()
    {
        CommitPendingGridEdit();
        if (!CanExport) return;
        var safe = string.Concat(ProfileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new SaveFileDialog { Filter = "Tweakmania profile (*.json)|*.json", DefaultExt = ".json", AddExtension = true, FileName = (string.IsNullOrWhiteSpace(safe) ? "Profile" : safe) + ".json" };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;
        try { ExportCurrentProfileToFile(dialog.FileName); Status = "Profile exported"; Detail = dialog.FileName; }
        catch (Exception ex) { Status = "Export failed"; Detail = ex.Message; }
    }

    internal void ExportCurrentProfileToFile(string path)
    {
        CommitPendingGridEdit();
        if (!CanExport) throw new InvalidOperationException("Correct the invalid profile edits before exporting.");
        ProfileStore.Save(path, _activeProfile with { Name = ProfileName.Trim() });
    }

    private void RefreshCommands()
    { RefreshCommand.Refresh(); StartStopCommand.Refresh(); DemoCommand.Refresh(); BrowseGameCommand?.Refresh(); RefreshProxyCommands(); Raise(nameof(IsIdle)); }

    public async Task ShutdownAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopInternalAsync();
        _previewTimer.Stop();
        _statusTimer.Stop();
        if (_enumerationTask is not null)
        {
            try { await _enumerationTask; }
            catch (Exception) { /* The refresh path reports discovery errors. */ }
        }
        try
        {
            if (_persistSettings)
            {
                CommitPendingGridEdit(); SaveCurrentDraft(); _draftTimer.Stop(); PersistDrafts();
                Directory.CreateDirectory(_library.DirectoryPath);
                AtomicWrite(Path.Combine(_library.DirectoryPath, "selected-profile.json"), JsonSerializer.Serialize(new SelectedProfile(_selectedProfileId)));
                AtomicWrite(Path.Combine(_library.DirectoryPath, "settings.json"), JsonSerializer.Serialize(new SavedSettings(_preferredSourceId, GameExecutablePath)));
            }
        }
        catch (Exception ex) { Debug.WriteLine($"Could not persist settings: {ex.Message}"); }
        _catalog.Dispose();
        _proxy?.Dispose();
        _proxy = null;
        _gameProcessId = null;
    }

    internal void SetScreenshotState()
    {
        _library.Initialize(); Profiles.Clear(); foreach (var entry in _library.Entries) Profiles.Add(new(entry.Id, entry.Name));
        _selectedProfileId = _library.Entries[0].Id; _savedProfile = _library.Load(_selectedProfileId);
        var exampleDevice = new InputDevice("directinput:00000000-0000-0000-0000-000000000001", "Controller (XBOX 360 For Windows)", InputApi.DirectInput,
            ["X Axis", "Y Axis", "Z Axis", "X Rotation", "Y Rotation", "", "", ""]);
        Devices.Add(exampleDevice); DeviceChoices.Add(exampleDevice); SelectedDevice = exampleDevice;
        var screenshotProfile = new AxisProfile { Name = "Precision steering", RightCurve = [new(0, 0), new(.25, .10), new(.5, .32), new(.75, .64), new(1, 1)], Deadzone = .02 };
        _library.Save(_selectedProfileId, screenshotProfile); _savedProfile = CopyProfile(screenshotProfile);
        ApplyProfile(screenshotProfile);
        CenterText = "-2"; TryApplyEdits();
        RawInput = -.56; OutputValue = AxisTransform.Apply(RawInput, ActiveProfile);
        Status = "Demo · no controller output"; Detail = "Simulated steering · edit the curve to tune the response";
        MonitorEnabled = true;
        _isDemo = true; _isRunning = true;
        _previewVisible = true;
        Raise(nameof(IsRunning)); Raise(nameof(IsDemo)); Raise(nameof(IsIdle)); Raise(nameof(RunningLabel));
        Raise(nameof(IsMonitoringActive)); Raise(nameof(PreviewStatus));
        RawInput = -.56; OutputValue = AxisTransform.Apply(RawInput, ActiveProfile);
    }

    private sealed record SavedSettings(string? SourceId, string? GameExecutablePath);
    private sealed record SelectedProfile(string? Id);
    private sealed record CurveDraft(string Input, string Output);
    private sealed record ProfileDraft(AxisProfile LastValidProfile, string Name, string MinimumText, string CenterText, string MaximumText, string DeadzoneText, string SaturationText, bool Mirror, bool Invert, bool EditLeftCurve, CurveDraft[] LeftRows, CurveDraft[] RightRows, bool LeftRowsEdited, bool RightRowsEdited, string ValidationMessage);
    private sealed class DemoSource : IInputSource
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public InputDevice Device { get; } = new("demo", "Simulated steering", InputApi.Demo, StandardAxes);
        public bool TryRead(out ControllerSnapshot snapshot)
        {
            snapshot = ControllerSnapshot.Neutral;
            snapshot.Axes[0] = Math.Sin(_watch.Elapsed.TotalSeconds * .7) * .92;
            return true;
        }
        public void Dispose() { }
    }
}
