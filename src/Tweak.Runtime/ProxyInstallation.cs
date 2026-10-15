using System.Diagnostics;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tweak.Runtime;

public sealed record ProxyInstallationInfo(string GameExecutablePath, string ProxyPath, bool Installed,
    bool OwnedByControllerTweak, string Message);

/// <summary>Safely installs or removes only Tweakmania's adjacent dinput8 proxy.</summary>
public static class ProxyInstallation
{
    private const string ProxyFileName = "dinput8.dll";
    private const string ManifestFileName = "dinput8.dll.controllertweak.json";
    private static readonly string[] RequiredExports = ["DirectInput8Create", "DllCanUnloadNow", "DllGetClassObject", "DllRegisterServer", "DllUnregisterServer", "GetdfDIJoystick"];

    public static string? DefaultGameExecutablePath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) return null;
            var versions = Path.Combine(root, "TMLoader", "database", "TmForever", "products", "TmForever");
            var latestLoaderExecutable = Directory.Exists(versions)
                ? Directory.EnumerateDirectories(versions)
                .Select(path => new { Path = path, Version = ParseVersion(Path.GetFileName(path)) })
                .Where(v => v.Version is not null && File.Exists(Path.Combine(v.Path, "TmForever.exe")))
                .OrderByDescending(v => v.Version)
                .ThenBy(v => Path.GetFileName(v.Path).Contains('-', StringComparison.Ordinal))
                .Select(v => Path.Combine(v.Path, "TmForever.exe"))
                .FirstOrDefault()
                : null;
            return latestLoaderExecutable ?? FindOriginalGameExecutable();
        }
    }

    public static bool IsTMLoaderExecutable(string? path) => !string.IsNullOrWhiteSpace(path) &&
        path.Contains(Path.Combine("TMLoader", "database", "TmForever", "products", "TmForever"), StringComparison.OrdinalIgnoreCase);

    private static string? FindOriginalGameExecutable()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var subkeyName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subkeyName);
                    var displayName = entry?.GetValue("DisplayName") as string;
                    if (displayName is null || !displayName.Contains("TrackMania United Forever", StringComparison.OrdinalIgnoreCase)) continue;
                    var installLocation = entry?.GetValue("InstallLocation") as string;
                    if (string.IsNullOrWhiteSpace(installLocation)) continue;
                    var executable = Path.Combine(installLocation.Trim().Trim('"'), "TmForever.exe");
                    if (File.Exists(executable)) return executable;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Keep searching the other registry view and the standard install location.
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var standardInstall = Path.Combine(programFiles, "TmUnitedForever", "TmForever.exe");
        return File.Exists(standardInstall) ? standardInstall : null;
    }

    public static ProxyInstallationInfo Describe(string gameExePath)
    {
        var (game, proxy, manifest) = ResolvePaths(gameExePath);
        if (!File.Exists(proxy)) return new(game, proxy, false, false, "Tweakmania proxy is not installed.");
        var owned = ReadManifest(manifest) is { } m && HashMatches(proxy, m.Sha256);
        return owned
            ? new(game, proxy, true, true, "Tweakmania proxy is installed.")
            : new(game, proxy, true, false, "An unrecognized dinput8.dll exists. It was left untouched.");
    }

    public static ProxyInstallationInfo Install(string gameExePath, string bundledProxyPath)
    {
        var (game, proxy, manifest) = ResolvePaths(gameExePath);
        ValidateGame(game);
        var source = Path.GetFullPath(bundledProxyPath);
        if (!File.Exists(source)) throw new FileNotFoundException("The bundled DirectInput proxy was not found.", source);
        EnsureNoReparsePoints(source);
        var pe = PeImage.Read(source);
        if (pe.Machine != 0x14c) throw new InvalidDataException("The DirectInput proxy must be a 32-bit x86 DLL.");
        var missing = RequiredExports.Except(pe.Exports, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("The DirectInput proxy is missing required exports: " + string.Join(", ", missing));

        var existing = File.Exists(proxy);
        if (existing)
        {
            var owned = ReadManifest(manifest) is { } old && HashMatches(proxy, old.Sha256);
            if (!owned) throw new IOException($"Cannot install because {proxy} already exists and is not owned by Tweakmania.");
            if (ProxyController.FindGameProcess(game) is { } running)
            {
                var pid = running.Id;
                running.Dispose();
                throw new InvalidOperationException($"Close the running game (PID {pid}) and restart it before changing the loaded proxy.");
            }
        }
        EnsureNoReparsePoints(Path.GetDirectoryName(proxy)!);
        var temp = Path.Combine(Path.GetDirectoryName(proxy)!, $".dinput8.dll.{Guid.NewGuid():N}.tmp");
        var manifestTemp = manifest + ".tmp." + Guid.NewGuid().ToString("N");
        var proxyBackup = proxy + ".backup." + Guid.NewGuid().ToString("N");
        var proxyCommitted = false;
        try
        {
            File.Copy(source, temp, false);
            var hash = Sha256(temp);
            var content = JsonSerializer.Serialize(new OwnershipManifest(1, hash));
            File.WriteAllText(manifestTemp, content);
            if (existing) File.Replace(temp, proxy, proxyBackup, true);
            else File.Move(temp, proxy);
            proxyCommitted = true;
            if (File.Exists(manifest)) File.Replace(manifestTemp, manifest, null, true);
            else File.Move(manifestTemp, manifest);
            TryDelete(proxyBackup);
            return new(game, proxy, true, true, existing ? "Tweakmania proxy updated." : "Tweakmania proxy installed.");
        }
        catch
        {
            if (proxyCommitted)
            {
                if (existing && File.Exists(proxyBackup)) File.Replace(proxyBackup, proxy, null, true);
                else TryDelete(proxy);
            }
            throw;
        }
        finally
        {
            TryDelete(temp);
            TryDelete(manifestTemp);
            TryDelete(proxyBackup);
        }
    }

    public static ProxyInstallationInfo Remove(string gameExePath)
    {
        var (game, proxy, manifest) = ResolvePaths(gameExePath);
        ValidateGame(game);
        if (!File.Exists(proxy)) return new(game, proxy, false, false, "Tweakmania proxy is not installed.");
        var ownership = ReadManifest(manifest);
        if (ownership is null || !HashMatches(proxy, ownership.Sha256))
            throw new IOException("The adjacent dinput8.dll is not verified as a Tweakmania file; it was left untouched.");
        if (ProxyController.FindGameProcess(game) is { } running)
        {
            var pid = running.Id;
            running.Dispose();
            throw new InvalidOperationException($"Close the running game (PID {pid}) and restart it before removing the loaded proxy.");
        }
        EnsureNoReparsePoints(proxy);
        EnsureNoReparsePoints(manifest);
        File.Delete(proxy);
        File.Delete(manifest);
        return new(game, proxy, false, false, "Tweakmania proxy removed.");
    }

    private static (string Game, string Proxy, string Manifest) ResolvePaths(string gameExePath)
    {
        if (string.IsNullOrWhiteSpace(gameExePath)) throw new ArgumentException("Game executable path is required.", nameof(gameExePath));
        var game = Path.GetFullPath(gameExePath);
        var dir = Path.GetDirectoryName(game) ?? throw new ArgumentException("Game executable path has no parent directory.", nameof(gameExePath));
        return (game, Path.Combine(dir, ProxyFileName), Path.Combine(dir, ManifestFileName));
    }

    private static void ValidateGame(string game)
    {
        if (!File.Exists(game)) throw new FileNotFoundException("The selected game executable does not exist.", game);
        EnsureNoReparsePoints(game);
        if (PeImage.Read(game).Machine != 0x14c) throw new InvalidDataException("The selected game executable is not a 32-bit x86 game.");
    }

    private static Version? ParseVersion(string value)
    {
        var versionText = System.Text.RegularExpressions.Regex.Match(value, @"^\d+(?:\.\d+){1,3}").Value;
        return Version.TryParse(versionText, out var version) ? version : null;
    }
    private sealed record OwnershipManifest(int SchemaVersion, string Sha256);
    private static OwnershipManifest? ReadManifest(string path)
    {
        try
        {
            var value = JsonSerializer.Deserialize<OwnershipManifest>(File.ReadAllText(path));
            return value?.SchemaVersion == 1 && value.Sha256 is { Length: 64 } ? value : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static bool HashMatches(string path, string expected) => File.Exists(path) && string.Equals(Sha256(path), expected, StringComparison.OrdinalIgnoreCase);
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }

    private static void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        FileSystemInfo? current = File.Exists(full) ? new FileInfo(full) : new DirectoryInfo(full);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Refusing to modify a path that traverses a symbolic link or reparse point: {current.FullName}");
            var parentPath = Path.GetDirectoryName(current.FullName);
            current = string.IsNullOrEmpty(parentPath) || string.Equals(parentPath, current.FullName, StringComparison.OrdinalIgnoreCase)
                ? null
                : new DirectoryInfo(parentPath);
        }
    }

    /// <summary>Small PE reader used to verify x86 machine type and export names without loading the DLL.</summary>
    private sealed record PeImage(ushort Machine, HashSet<string> Exports)
    {
        public static PeImage Read(string path)
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException($"'{path}' is not a PE image.");
            stream.Position = 0x3c;
            var peOffset = reader.ReadInt32();
            if (peOffset < 0 || peOffset > stream.Length - 24) throw new InvalidDataException($"'{path}' has an invalid PE header.");
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException($"'{path}' has an invalid PE signature.");
            var machine = reader.ReadUInt16();
            var sectionCount = reader.ReadUInt16();
            stream.Position += 12;
            var optionalSize = reader.ReadUInt16();
            stream.Position += 2;
            var optionalStart = stream.Position;
            var magic = reader.ReadUInt16();
            var directoryOffset = magic switch { 0x10b => 96, 0x20b => 112, _ => throw new InvalidDataException("Unsupported PE optional header.") };
            stream.Position = optionalStart + directoryOffset;
            var exportRva = reader.ReadUInt32();
            var exportSize = reader.ReadUInt32();
            var sectionsStart = optionalStart + optionalSize;
            var sections = new List<(uint Va, uint Size, uint Raw)>(sectionCount);
            stream.Position = sectionsStart;
            for (var i = 0; i < sectionCount; i++)
            {
                stream.Position += 8;
                var virtualSize = reader.ReadUInt32(); var va = reader.ReadUInt32(); var rawSize = reader.ReadUInt32(); var raw = reader.ReadUInt32();
                sections.Add((va, Math.Max(virtualSize, rawSize), raw));
                stream.Position += 16;
            }
            uint Offset(uint rva)
            {
                foreach (var s in sections) if (rva >= s.Va && (ulong)rva < (ulong)s.Va + s.Size) return checked(s.Raw + rva - s.Va);
                throw new InvalidDataException("PE export data points outside its sections.");
            }
            var exports = new HashSet<string>(StringComparer.Ordinal);
            if (exportRva != 0 && exportSize >= 40)
            {
                stream.Position = Offset(exportRva);
                stream.Position += 24;
                var nameCount = reader.ReadUInt32();
                stream.Position += 4;
                var namesRva = reader.ReadUInt32();
                if (nameCount > 4096) throw new InvalidDataException("PE export table is unexpectedly large.");
                stream.Position = Offset(namesRva);
                var nameOffsets = Enumerable.Range(0, (int)nameCount).Select(_ => reader.ReadUInt32()).ToArray();
                foreach (var nameRva in nameOffsets)
                {
                    stream.Position = Offset(nameRva);
                    var chars = new List<byte>();
                    for (var n = 0; n < 512; n++) { var b = reader.ReadByte(); if (b == 0) break; chars.Add(b); }
                    exports.Add(System.Text.Encoding.ASCII.GetString(chars.ToArray()));
                }
            }
            return new(machine, exports);
        }
    }
}
