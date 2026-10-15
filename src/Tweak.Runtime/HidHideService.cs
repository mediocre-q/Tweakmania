using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Tweak.Runtime;

/// <summary>A user-selected HidHide target. XInput user slots are deliberately not inferred from PnP identity.</summary>
public sealed record HidHideTarget(string Id, string DisplayName, IReadOnlyList<string> InstancePaths)
{
    public ushort? VendorId { get; init; }
    public ushort? ProductId { get; init; }
    internal bool IsValidatedHidInterface { get; init; }
}

/// <summary>Transactional, temporary HidHide configuration for a routing session.</summary>
public sealed class HidHideService
{
    private readonly IHidHideBackend backend;
    private readonly IHidHideTargetEnumerator targetEnumerator;
    private readonly string journalPath;
    private readonly Func<string, string?> queryDrive;
    public static string JournalPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ControllerTweak", "hidhide-lease.json");

    public HidHideService() : this(new HidHideNativeBackend(), new SetupApiHidHideTargetEnumerator(), JournalPath, QueryDosDeviceTarget) { }
    internal HidHideService(IHidHideBackend backend, IHidHideTargetEnumerator targetEnumerator, string? journalPath = null, Func<string, string?>? queryDrive = null)
    { this.backend = backend; this.targetEnumerator = targetEnumerator; this.journalPath = journalPath ?? JournalPath; this.queryDrive = queryDrive ?? QueryDosDeviceTarget; }

    public IReadOnlyList<HidHideTarget> EnumerateTargets() => targetEnumerator.EnumerateTargets();

    public static string DescribeAvailability()
    {
        if (!OperatingSystem.IsWindows()) return "HidHide is available only on Windows.";
        try
        {
            using var session = new HidHideNativeBackend().OpenExclusive();
            _ = session.Active; _ = session.Inverse;
            return "HidHide driver is available.";
        }
        catch (Exception ex) { return $"HidHide is unavailable: {ex.Message}"; }
    }

    public IDisposable BeginHiding(HidHideTarget target, string appPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(appPath);
        if (!target.IsValidatedHidInterface) throw new InvalidOperationException("Choose a source game controller from the HidHide target list before starting routing.");
        if (target.InstancePaths.Count == 0) throw new ArgumentException("The selected device has no usable PnP instance paths.", nameof(target));
        var identity = SetupApiHidHideTargetEnumerator.GetVidPid(target.Id) ??
            (target.VendorId is { } vid && target.ProductId is { } pid ? ($"{vid:X4}", $"{pid:X4}") : null);
        if (SetupApiHidHideTargetEnumerator.IsVJoy(target.Id) ||
            (target.VendorId is { } vendor && target.ProductId is { } product && SetupApiHidHideTargetEnumerator.IsVJoy(vendor, product)) ||
            target.DisplayName.Contains("vJoy", StringComparison.OrdinalIgnoreCase) ||
            target.InstancePaths.Any(SetupApiHidHideTargetEnumerator.IsUnsafeInstancePath))
            throw new InvalidOperationException("The selected HidHide target is not a source game controller. vJoy devices, hubs, and non-controller paths cannot be hidden here.");
        if (identity is null || !SetupApiHidHideTargetEnumerator.IsControllerPath(target.Id) ||
            target.InstancePaths.Any(path =>
            {
                var pathIdentity = SetupApiHidHideTargetEnumerator.GetVidPid(path);
                return pathIdentity is null
                    ? !path.Equals(target.Id, StringComparison.OrdinalIgnoreCase)
                    : pathIdentity != identity;
            }))
            throw new InvalidOperationException("The selected HidHide target contains a PnP path from a different USB device.");
        var normalizedApp = ToDevicePath(Path.GetFullPath(appPath), queryDrive);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var lockPath = journalPath + ".lock";
        FileStream leaseLock;
        try { leaseLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("Another controller-hiding session or recovery helper is active.", ex); }

        try
        {
            if (File.Exists(journalPath)) throw new InvalidOperationException("A HidHide recovery journal already exists. Restart the application to recover it before beginning another hiding session.");
            using var session = backend.OpenExclusive();
            if (session.Inverse) throw new InvalidOperationException("HidHide inverse mode is enabled. Turn it off in HidHide Configuration before routing.");
            var existingBlack = Distinct(session.Blacklist);
            var targetPaths = Distinct(target.InstancePaths);
            if (!session.Active && existingBlack.Any(p => !Contains(targetPaths, p)))
                throw new InvalidOperationException("HidHide is inactive and has other devices on its blacklist. Enable HidHide manually first so routing does not activate hiding for those devices.");

            var addedBlack = targetPaths.Where(p => !Contains(existingBlack, p)).ToArray();
            var currentWhite = Distinct(session.Whitelist);
            var addedWhite = Contains(currentWhite, normalizedApp) ? Array.Empty<string>() : [normalizedApp];
            var journal = new LeaseJournal(1, Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                addedWhite, addedBlack, targetPaths, session.Active, !session.Active);
            WriteJournal(journalPath, journal); // durable before the first HidHide mutation
            try
            {
                if (addedWhite.Length != 0) session.SetWhitelist(Union(currentWhite, addedWhite));
                if (addedBlack.Length != 0) session.SetBlacklist(Union(existingBlack, addedBlack));
                if (!session.Active) session.SetActive(true);
            }
            catch
            {
                RestoreWithSession(session, journal);
                TryDeleteJournal(journalPath);
                throw;
            }
            return new HidingLease(backend, journalPath, journal, leaseLock);
        }
        catch { leaseLock.Dispose(); throw; }
    }

    /// <summary>Restore a journal left by a crashed owner. Returns false while its recorded owner is still alive.</summary>
    public static bool RecoverLease(string path)
        => RecoverLease(path, new HidHideNativeBackend());

    internal static bool RecoverLease(string path, IHidHideBackend backend)
    {
        if (!File.Exists(path)) return false;
        var journal = ReadJournal(path);
        if (OwnerIsAlive(journal)) return false;
        var lockPath = path + ".lock";
        FileStream guard;
        try { guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return false; }
        using (guard)
        {
            if (!File.Exists(path)) return false;
            journal = ReadJournal(path);
            if (OwnerIsAlive(journal)) return false;
            RestoreWithBackend(backend, journal);
            TryDeleteJournal(path);
            return true;
        }
    }

    public static bool RecoverStaleLease() => RecoverLease(JournalPath);

    private static void RestoreWithBackend(IHidHideBackend backend, LeaseJournal journal)
    {
        using var session = backend.OpenExclusive();
        RestoreWithSession(session, journal);
    }
    private static void RestoreWithSession(IHidHideSession session, LeaseJournal journal)
    {
        var white = Distinct(session.Whitelist);
        var black = Distinct(session.Blacklist);
        var nextWhite = white.Where(p => !Contains(journal.AddedWhitelist, p)).ToArray();
        var nextBlack = black.Where(p => !Contains(journal.AddedBlacklist, p)).ToArray();
        if (!SameSet(white, nextWhite)) session.SetWhitelist(nextWhite);
        if (!SameSet(black, nextBlack)) session.SetBlacklist(nextBlack);
        // Only turn off what this lease turned on, and only if no other blacklist entries remain.
        if (journal.EnabledActive && nextBlack.All(p => Contains(journal.TargetPaths, p)) && session.Active) session.SetActive(false);
    }

    private static void WriteJournal(string path, LeaseJournal journal)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(journal));
        using (var stream = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
        File.Move(temp, path, true);
    }
    private static LeaseJournal ReadJournal(string path) => JsonSerializer.Deserialize<LeaseJournal>(File.ReadAllText(path))
        ?? throw new InvalidDataException("HidHide recovery journal is empty or invalid.");
    private static void TryDeleteJournal(string path) { try { File.Delete(path); } catch { } }
    internal static string ToDevicePath(string dosPath, Func<string, string?> queryDrive)
    {
        if (dosPath.Length < 3 || dosPath[1] != ':' || dosPath[2] != Path.DirectorySeparatorChar) return dosPath;
        var device = queryDrive(dosPath[..2]);
        return string.IsNullOrEmpty(device) ? dosPath : device.TrimEnd('\\') + dosPath[2..].Replace(Path.DirectorySeparatorChar, '\\');
    }
    internal static string[] DecodeMultiSz(ReadOnlySpan<char> chars)
    {
        var values = new List<string>();
        var start = 0;
        while (start < chars.Length)
        {
            var relative = chars[start..].IndexOf('\0');
            if (relative < 0) { if (start < chars.Length) values.Add(new string(chars[start..])); break; }
            if (relative == 0) break;
            values.Add(new string(chars.Slice(start, relative)));
            start += relative + 1;
        }
        return values.ToArray();
    }
    private static string? QueryDosDeviceTarget(string drive)
    {
        var buffer = new char[1024];
        var length = QueryDosDevice(drive, buffer, (uint)buffer.Length);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not map the application path to a Windows device path for HidHide.");
        var end = Array.IndexOf(buffer, '\0', 0, (int)length);
        return new string(buffer, 0, end < 0 ? (int)length : end);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint QueryDosDevice(string name, [Out] char[] target, uint max);
    private static bool OwnerIsAlive(LeaseJournal j)
    {
        try { using var p = Process.GetProcessById(j.ProcessId); return p.StartTime.ToUniversalTime().Ticks == j.ProcessStartUtcTicks; }
        catch { return false; }
    }
    private static string[] Distinct(IEnumerable<string> values) => values.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool Contains(IEnumerable<string> values, string item) => values.Contains(item, StringComparer.OrdinalIgnoreCase);
    private static string[] Union(IEnumerable<string> left, IEnumerable<string> right) => Distinct(left.Concat(right));
    private static bool SameSet(IEnumerable<string> a, IEnumerable<string> b) => new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);

    private sealed class HidingLease(IHidHideBackend backend, string path, LeaseJournal journal, FileStream lockFile) : IDisposable
    {
        private bool disposed;
        private readonly object gate = new();
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                for (var attempt = 0; ; attempt++)
                {
                    try { RestoreWithBackend(backend, journal); break; }
                    catch (Exception ex) when (attempt < 9 && ex is Win32Exception or IOException) { Thread.Sleep(100); }
                }
                TryDeleteJournal(path);
                disposed = true;
                lockFile.Dispose();
            }
        }
    }
    internal sealed record LeaseJournal(int Version, int ProcessId, long ProcessStartUtcTicks,
        string[] AddedWhitelist, string[] AddedBlacklist, string[] TargetPaths, bool PreviousActive, bool EnabledActive);
}

internal interface IHidHideBackend { IHidHideSession OpenExclusive(); }
internal interface IHidHideSession : IDisposable
{
    IReadOnlyList<string> Whitelist { get; }
    IReadOnlyList<string> Blacklist { get; }
    bool Active { get; }
    bool Inverse { get; }
    void SetWhitelist(IReadOnlyList<string> values);
    void SetBlacklist(IReadOnlyList<string> values);
    void SetActive(bool value);
}
internal interface IHidHideTargetEnumerator { IReadOnlyList<HidHideTarget> EnumerateTargets(); }

internal sealed class HidHideNativeBackend : IHidHideBackend
{
    public IHidHideSession OpenExclusive() => new Session();
    private sealed class Session : IHidHideSession
    {
        private const uint GenericRead = 0x80000000, OpenExisting = 3;
        private readonly SafeFileHandle handle;
        public Session()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HidHide is supported only on Windows.");
            handle = CreateFile("\\\\.\\HidHide", GenericRead, 0, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open HidHide control device. Is HidHide installed and running?");
        }
        public IReadOnlyList<string> Whitelist => GetList(2048);
        public IReadOnlyList<string> Blacklist => GetList(2050);
        public bool Active => GetBool(2052);
        public bool Inverse => GetBool(2054);
        public void SetWhitelist(IReadOnlyList<string> values) => SetList(2049, values);
        public void SetBlacklist(IReadOnlyList<string> values) => SetList(2051, values);
        public void SetActive(bool value) => SetBool(2053, value);
        public void Dispose() => handle.Dispose();
        private uint Ioctl(int function) => (32769u << 16) | (1u << 14) | ((uint)function << 2);
        private IReadOnlyList<string> GetList(int fn)
        {
            // The stable driver rejects oversized whitelist buffers. Query the
            // required byte length first, as its official configuration client does.
            if (!DeviceIoControl(handle, Ioctl(fn), IntPtr.Zero, 0, IntPtr.Zero, 0, out var needed, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide could not size its configured device list.");
            if (needed is < 2 or > 1048576 || (needed & 1) != 0)
                throw new InvalidDataException("HidHide returned an invalid configured list size.");
            var bytes = new byte[needed];
            if (!DeviceIoControl(handle, Ioctl(fn), IntPtr.Zero, 0, ref bytes[0], (uint)bytes.Length, out var returned, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide could not return its configured device list.");
            var chars = Encoding.Unicode.GetChars(bytes, 0, Math.Min((int)returned, bytes.Length) & ~1);
            return HidHideService.DecodeMultiSz(chars);
        }
        private void SetList(int fn, IReadOnlyList<string> values)
        {
            var text = string.Join('\0', values) + "\0\0";
            var bytes = Encoding.Unicode.GetBytes(text);
            if (!DeviceIoControl(handle, Ioctl(fn), ref bytes[0], (uint)bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide rejected its configured device list.");
        }
        private bool GetBool(int fn)
        {
            byte value = 0;
            if (!DeviceIoControl(handle, Ioctl(fn), IntPtr.Zero, 0, ref value, 1, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide state query failed.");
            return value != 0;
        }
        private void SetBool(int fn, bool value)
        {
            byte input = value ? (byte)1 : (byte)0;
            if (!DeviceIoControl(handle, Ioctl(fn), ref input, 1, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "HidHide state update failed.");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, ref byte output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref byte input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
    }
}

internal sealed class SetupApiHidHideTargetEnumerator : IHidHideTargetEnumerator
{
    private static readonly Guid HidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");
    private const uint Present = 0x2, DeviceInterface = 0x10;
    private const uint FriendlyName = 0x0c, DeviceDescription = 0;
    public IReadOnlyList<HidHideTarget> EnumerateTargets()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<HidHideTarget>();
        var hidInterface = HidInterface;
        using var set = SetupDiGetClassDevs(ref hidInterface, null, IntPtr.Zero, Present | DeviceInterface);
        if (set.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enumerate HID devices.");
        var targets = new List<HidHideTarget>();
        for (uint index = 0; ; index++)
        {
            var iface = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidInterface, index, ref iface))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 259) break;
                throw new Win32Exception(error, "Could not enumerate HID interfaces.");
            }
            _ = SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var needed, IntPtr.Zero);
            if (needed == 0) continue;
            var detail = Marshal.AllocHGlobal((int)needed);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                var info = new SpDevInfoData { cbSize = Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out _, ref info)) continue;
                var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? "";
                if (path.Length == 0 || !IsGameController(path, out var vendorId, out var productId, out var productName)) continue;
                var instance = GetInstanceId(set, ref info);
                if (instance.Length == 0) continue;
                var paths = new List<string> { instance };
                if (IsVJoy(instance) || IsVJoy(vendorId, productId)) continue;
                var identity = (vendorId.ToString("X4"), productId.ToString("X4"));
                var matchingParents = GetRelevantParents(info.DevInst, identity).ToArray();
                paths.AddRange(matchingParents);
                var label = GetProperty(set, ref info, FriendlyName);
                if (string.IsNullOrWhiteSpace(label) || label.Contains("HID-compliant", StringComparison.OrdinalIgnoreCase)) label = productName;
                if (string.IsNullOrWhiteSpace(label)) label = GetProperty(set, ref info, DeviceDescription);
                if (string.IsNullOrWhiteSpace(label)) label = instance;
                if (label.Contains("vJoy", StringComparison.OrdinalIgnoreCase)) continue;
                label += $" [{vendorId:X4}:{productId:X4} · {instance[^Math.Min(18, instance.Length)..]}]";
                targets.Add(new HidHideTarget(instance, label, paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                    { VendorId = vendorId, ProductId = productId, IsValidatedHidInterface = true });
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        return targets
            .GroupBy(t => t.InstancePaths.Skip(1).FirstOrDefault() ?? t.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HidHideTarget(group.Key, group.First().DisplayName,
                group.SelectMany(t => t.InstancePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                { VendorId = group.First().VendorId, ProductId = group.First().ProductId, IsValidatedHidInterface = true })
            .ToArray();
    }
    private static bool IsGameController(string path, out ushort vendorId, out ushort productId, out string productName)
    {
        vendorId = productId = 0;
        productName = "";
        using var device = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (device.IsInvalid) return false;
        var attributes = new HidAttributes { Size = Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(device, ref attributes)) return false;
        vendorId = attributes.VendorId;
        productId = attributes.ProductId;
        if (vendorId == 0 || productId == 0) return false;
        var name = new char[512];
        if (HidD_GetProductString(device, name, (uint)(name.Length * sizeof(char))))
        {
            var end = Array.IndexOf(name, '\0');
            productName = new string(name, 0, end < 0 ? name.Length : end).Trim();
        }
        if (!HidD_GetPreparsedData(device, out var data)) return false;
        try
        {
            if (HidP_GetCaps(data, out var caps) < 0 || caps.UsagePage != 1) return false;
            return caps.Usage is 4 or 5 or 8; // joystick, game pad, multi-axis controller
        }
        finally { HidD_FreePreparsedData(data); }
    }
    private static string GetInstanceId(SafeDeviceInfoSet set, ref SpDevInfoData info)
    {
        var buffer = new char[1024];
        if (!SetupDiGetDeviceInstanceId(set, ref info, buffer, (uint)buffer.Length, out _)) return "";
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }
    private static string GetProperty(SafeDeviceInfoSet set, ref SpDevInfoData info, uint property)
    {
        var buffer = new char[512];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buffer, (uint)(buffer.Length * 2), out _)) return "";
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }
    private static IEnumerable<string> GetRelevantParents(uint devInst, (string Vid, string Pid) identity)
    {
        for (var depth = 0; depth < 12 && CM_Get_Parent(out var parent, devInst, 0) == 0; depth++, devInst = parent)
        {
            var id = new char[1024];
            if (CM_Get_Device_ID(parent, id, id.Length, 0) != 0) break;
            var end = Array.IndexOf(id, '\0');
            var text = new string(id, 0, end < 0 ? id.Length : end);
            if (IsMatchingControllerParent(text, identity))
            {
                yield return text;
                yield break; // nearest exact device identity only; never include its hub/root hub or adapter
            }
        }
    }
    internal static (string Vid, string Pid)? GetVidPid(string id)
    {
        var match = Regex.Match(id, @"VID_([0-9A-F]{4}).*?PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? (match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value.ToUpperInvariant()) : null;
    }
    internal static bool IsVJoy(ushort vendorId, ushort productId) => vendorId == 0x1234 && productId is 0xBEAD or 0x0FFB or 0x1FFB;
    internal static bool IsControllerPath(string path) => path.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) || path.StartsWith("XUSB\\VID_", StringComparison.OrdinalIgnoreCase);
    internal static bool IsMatchingControllerParent(string path, (string Vid, string Pid) identity) =>
        (path.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("XUSB\\", StringComparison.OrdinalIgnoreCase)) && GetVidPid(path) is { } pair && pair == identity;
    internal static bool IsVJoy(string id) => GetVidPid(id) is { } pair && pair.Vid == "1234" && pair.Pid is "BEAD" or "0FFB" or "1FFB";
    internal static bool IsUnsafeInstancePath(string path) => IsVJoy(path) ||
        path.StartsWith("USB\\ROOT_HUB", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("USB\\ROOT_HUB20", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("BTH\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("BTHENUM\\", StringComparison.OrdinalIgnoreCase) ||
        !(path.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) ||
          path.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) ||
          path.StartsWith("XUSB\\VID_", StringComparison.OrdinalIgnoreCase));
    [StructLayout(LayoutKind.Sequential)] private struct SpDeviceInterfaceData { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct SpDevInfoData { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct HidAttributes { public int Size; public ushort VendorId, ProductId, VersionNumber; }
    [StructLayout(LayoutKind.Sequential)] private struct HidCaps { public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved; public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices; }
    private sealed class SafeDeviceInfoSet : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid { public SafeDeviceInfoSet() : base(true) { } public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1); protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle); }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeDeviceInfoSet SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(SafeDeviceInfoSet set, IntPtr info, ref Guid guid, uint index, ref SpDeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(SafeDeviceInfoSet set, ref SpDeviceInterfaceData iface, IntPtr detail, uint size, out uint needed, IntPtr info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(SafeDeviceInfoSet set, ref SpDeviceInterfaceData iface, IntPtr detail, uint size, out uint needed, ref SpDevInfoData info);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceId(SafeDeviceInfoSet set, ref SpDevInfoData info, [Out] char[] id, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceRegistryProperty(SafeDeviceInfoSet set, ref SpDevInfoData info, uint property, out uint type, [Out] char[] buffer, uint size, out uint required);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")] private static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)] private static extern int CM_Get_Device_ID(uint devInst, [Out] char[] id, int size, uint flags);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle file, ref HidAttributes attributes);
    [DllImport("hid.dll", EntryPoint = "HidD_GetProductString", ExactSpelling = true, CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle file, [Out] char[] buffer, uint bufferLength);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
