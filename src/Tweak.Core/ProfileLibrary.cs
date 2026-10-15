using System.Text.Json;

namespace Tweak.Core;

/// <summary>Stable-ID local library for portable axis-profile baselines.</summary>
public sealed class ProfileLibrary
{
    private readonly string _directory;
    private readonly List<LibraryEntry> _entries = [];
    private readonly Dictionary<string, AxisProfile> _memoryProfiles = new(StringComparer.Ordinal);
    private readonly bool _memoryOnly;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public IReadOnlyList<LibraryEntry> Entries => _entries;
    public string DirectoryPath => _directory;

    public ProfileLibrary(string directory, bool memoryOnly = false) { _directory = Path.GetFullPath(directory); _memoryOnly = memoryOnly; }

    public void Initialize(string? legacyPath = null)
    {
        if (_memoryOnly)
        {
            _entries.Clear(); _memoryProfiles.Clear();
            Add(new AxisProfile(), "Default"); return;
        }
        Directory.CreateDirectory(_directory);
        var indexPath = Path.Combine(_directory, "profiles.json");
        _entries.Clear();
        if (File.Exists(indexPath))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<List<LibraryEntry>>(File.ReadAllText(indexPath), Options) ?? [];
                foreach (var entry in loaded)
                {
                    if (!Guid.TryParse(entry.Id, out _) || _entries.Any(x => x.Id == entry.Id)) continue;
                    try { var saved = ProfileStore.Load(ProfilePath(entry.Id)); ValidateCompatible(saved); _entries.Add(entry with { Name = saved.Name }); }
                    catch { /* A corrupt entry is isolated from the rest of the library. */ }
                }
            }
            catch { /* A damaged index is recoverable from the legacy profile or Default. */ }
        }
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParse(id, out _) || _entries.Any(e => e.Id == id)) continue;
            try { var profile = ProfileStore.Load(path); ValidateCompatible(profile); _entries.Add(new(id, profile.Name)); }
            catch { /* A damaged profile does not hide other valid profile files. */ }
        }
        var initializedPath = Path.Combine(_directory, ".profiles-initialized");
        var initialized = File.Exists(initializedPath);
        if (_entries.Count == 0 && !initialized && !string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath))
        {
            try
            {
                var legacy = ProfileStore.Load(legacyPath);
                Add(legacy, legacy.Name);
            }
            catch { }
        }
        if (_entries.Count == 0) Add(new AxisProfile(), "Default");
        SaveIndex();
        if (!initialized) File.WriteAllText(initializedPath, "1");
    }

    public AxisProfile Load(string id)
    {
        if (_memoryOnly) return Copy(_memoryProfiles.TryGetValue(id, out var memory) ? memory : throw new FileNotFoundException("Profile not found."));
        if (!Guid.TryParse(id, out _) || (!_entries.Any(x => x.Id == id) && !File.Exists(ProfilePath(id))))
            throw new FileNotFoundException("Profile not found.");
        var profile = ProfileStore.Load(ProfilePath(id)); ValidateCompatible(profile); return profile;
    }

    public LibraryEntry Add(AxisProfile profile, string name)
    {
        var normalized = UniqueName(name);
        var id = Guid.NewGuid().ToString("D");
        var saved = profile with { Name = normalized };
        ValidateCompatible(saved);
        if (_memoryOnly) _memoryProfiles[id] = Copy(saved); else ProfileStore.Save(ProfilePath(id), saved);
        var entry = new LibraryEntry(id, normalized);
        _entries.Add(entry);
        SaveIndex();
        return entry;
    }

    public void Save(string id, AxisProfile profile)
    {
        var index = _entries.FindIndex(x => x.Id == id);
        if (index < 0) throw new FileNotFoundException("Profile not found.");
        var normalized = UniqueName(profile.Name, id);
        var saved = profile with { Name = normalized };
        ValidateCompatible(saved);
        if (_memoryOnly) _memoryProfiles[id] = Copy(saved); else ProfileStore.Save(ProfilePath(id), saved);
        _entries[index] = _entries[index] with { Name = normalized };
        if (!_memoryOnly) SaveIndex();
    }

    public string Rename(string id, string name)
    {
        var index = _entries.FindIndex(x => x.Id == id);
        if (index < 0) throw new FileNotFoundException("Profile not found.");
        var normalized = UniqueName(name, id);
        var profile = Load(id) with { Name = normalized };
        Save(id, profile);
        return normalized;
    }

    public void Delete(string id)
    {
        if (_entries.Count == 0) throw new InvalidOperationException("No profiles are available.");
        var entry = _entries.First(x => x.Id == id);
        if (_memoryOnly) _memoryProfiles.Remove(id); else File.Delete(ProfilePath(id));
        _entries.Remove(entry);
        if (!_memoryOnly) SaveIndex();
        if (_entries.Count == 0) Add(new AxisProfile(), "Default");
        if (_memoryOnly) return;
        var draftPath = Path.Combine(_directory, "drafts.json");
        try
        {
            if (File.Exists(draftPath))
            {
                var drafts = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(draftPath), Options) ?? [];
                drafts.Remove(id);
                AtomicWrite(draftPath, JsonSerializer.Serialize(drafts, Options));
            }
        }
        catch { }
    }

    public string UniqueName(string name, string? exceptId = null)
    {
        var baseName = name.Trim();
        if (string.IsNullOrWhiteSpace(baseName)) throw new ArgumentException("Profile name must not be empty.", nameof(name));
        var used = _entries.Where(x => x.Id != exceptId).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(baseName)) return baseName;
        for (var n = 2; ; n++) { var candidate = $"{baseName} ({n})"; if (!used.Contains(candidate)) return candidate; }
    }

    private string ProfilePath(string id) => Path.Combine(_directory, id + ".json");
    private static AxisProfile Copy(AxisProfile profile) => profile with { LeftCurve = profile.LeftCurve.ToArray(), RightCurve = profile.RightCurve.ToArray() };
    private static void ValidateCompatible(AxisProfile profile)
    {
        profile.Validate();
        if (profile.LeftCurve.Length > 32 || profile.RightCurve.Length > 32)
            throw new ArgumentException("The in-game proxy supports at most 32 curve points per side.");
    }

    // The index is only a cache: GUID-named profile files are authoritative and recovered by scanning.
    private void SaveIndex()
    {
        if (_memoryOnly) return;
        try { AtomicWrite(Path.Combine(_directory, "profiles.json"), JsonSerializer.Serialize(_entries, Options)); }
        catch { /* Index failure must not turn a successfully committed profile into a reported failed save. */ }
    }
    private static void AtomicWrite(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, content); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record LibraryEntry(string Id, string Name);
