using Microsoft.Win32;

namespace Lunalyr.Install;

/// <summary>
/// The current user's registry (<c>HKCU</c>), as far as Lunalyr touches it. Paths are relative to <c>HKCU</c>; a null
/// value name is the key's default value. Tests use <see cref="MemoryRegistryHive"/>.
/// </summary>
public interface IRegistryHive
{
    string? GetString(string key, string? name);

    byte[]? GetBinary(string key, string? name);

    void SetString(string key, string? name, string value);

    void SetDword(string key, string? name, int value);

    /// <summary>Removes one value; nothing happens when it isn't there.</summary>
    void DeleteValue(string key, string name);

    /// <summary>Removes a key and everything under it; nothing happens when it isn't there.</summary>
    void DeleteTree(string key);

    bool KeyExists(string key);
}

/// <summary>
/// <c>HKCU</c>, or a key under it standing in for <c>HKCU</c> (<c>--registry-base</c>, for checking the installer
/// without touching the real entries). Writes are refused outside the keys <see cref="Product"/> names.
/// </summary>
public sealed class WindowsRegistryHive : IRegistryHive
{
    private readonly string _base;

    /// <param name="baseKey">Empty for <c>HKCU</c> itself, or a key under <c>HKCU\Software\Lunalyr\</c>.</param>
    public WindowsRegistryHive(string baseKey = "")
    {
        if (baseKey.Length > 0 && !baseKey.StartsWith(@"Software\Lunalyr\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(@"A stand-in registry base must be under HKCU\Software\Lunalyr\.", nameof(baseKey));
        _base = baseKey.TrimEnd('\\');
    }

    private string Full(string key) => _base.Length == 0 ? key : _base + "\\" + key;

    public string? GetString(string key, string? name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Full(key));
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public byte[]? GetBinary(string key, string? name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Full(key));
        return k?.GetValue(name) as byte[];
    }

    public void SetString(string key, string? name, string value)
    {
        OwnedEntries.Check(key, name);
        using var k = Registry.CurrentUser.CreateSubKey(Full(key), writable: true);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public void SetDword(string key, string? name, int value)
    {
        OwnedEntries.Check(key, name);
        using var k = Registry.CurrentUser.CreateSubKey(Full(key), writable: true);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void DeleteValue(string key, string name)
    {
        OwnedEntries.Check(key, name);
        using var k = Registry.CurrentUser.OpenSubKey(Full(key), writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void DeleteTree(string key)
    {
        OwnedEntries.CheckTree(key);
        Registry.CurrentUser.DeleteSubKeyTree(Full(key), throwOnMissingSubKey: false);
    }

    public bool KeyExists(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Full(key));
        return k is not null;
    }
}

/// <summary>The only registry entries Lunalyr may change: its link scheme, its Apps entry and its own Run value.</summary>
public static class OwnedEntries
{
    public static bool IsOwned(string key, string? name)
    {
        if (IsOwnedTree(key)) return true;
        return string.Equals(key, Product.RunKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(name, Product.RunValue, StringComparison.Ordinal);
    }

    public static bool IsOwnedTree(string key) =>
        IsUnder(key, Product.LinkKey) || IsUnder(key, Product.UninstallKey);

    public static void Check(string key, string? name)
    {
        if (!IsOwned(key, name))
            throw new InvalidOperationException($@"Lunalyr doesn't write HKCU\{key}{(name is null ? "" : " › " + name)}: it isn't one of its own entries.");
    }

    public static void CheckTree(string key)
    {
        if (!IsOwnedTree(key))
            throw new InvalidOperationException($@"Lunalyr doesn't remove HKCU\{key}: it isn't one of its own keys.");
    }

    private static bool IsUnder(string key, string owned) =>
        string.Equals(key, owned, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(owned + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>An in-memory <c>HKCU</c> for tests, with the same ownership checks as the real one.</summary>
public sealed class MemoryRegistryHive : IRegistryHive
{
    private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Every write, in order ("set key › name = value", "delete key › name", "delete tree key").</summary>
    public List<string> Writes { get; } = [];

    private static string Name(string? name) => name ?? "";

    public string? GetString(string key, string? name)
    {
        lock (_gate) return _keys.TryGetValue(key, out var v) && v.TryGetValue(Name(name), out var o) ? o as string : null;
    }

    public byte[]? GetBinary(string key, string? name)
    {
        lock (_gate) return _keys.TryGetValue(key, out var v) && v.TryGetValue(Name(name), out var o) ? o as byte[] : null;
    }

    public int? GetDword(string key, string? name)
    {
        lock (_gate) return _keys.TryGetValue(key, out var v) && v.TryGetValue(Name(name), out var o) ? o as int? : null;
    }

    public void SetString(string key, string? name, string value)
    {
        OwnedEntries.Check(key, name);
        Put(key, name, value);
    }

    public void SetDword(string key, string? name, int value)
    {
        OwnedEntries.Check(key, name);
        Put(key, name, value);
    }

    /// <summary>Seeds a value as Windows or another program would have, without the ownership check.</summary>
    public void Seed(string key, string? name, object value)
    {
        lock (_gate) Values(key)[Name(name)] = value;
    }

    private void Put(string key, string? name, object value)
    {
        lock (_gate)
        {
            Values(key)[Name(name)] = value;
            Writes.Add($"set {key} › {Name(name)} = {value}");
        }
    }

    private Dictionary<string, object> Values(string key)
    {
        // Creating a key creates its parents, as the registry does.
        var parts = key.Split('\\');
        Dictionary<string, object>? values = null;
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts[..i]);
            if (!_keys.TryGetValue(path, out values)) _keys[path] = values = new(StringComparer.Ordinal);
        }
        return values!;
    }

    public void DeleteValue(string key, string name)
    {
        OwnedEntries.Check(key, name);
        lock (_gate)
        {
            if (_keys.TryGetValue(key, out var v)) v.Remove(name);
            Writes.Add($"delete {key} › {name}");
        }
    }

    public void DeleteTree(string key)
    {
        OwnedEntries.CheckTree(key);
        lock (_gate)
        {
            foreach (var k in _keys.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)
                         || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
                _keys.Remove(k);
            Writes.Add($"delete tree {key}");
        }
    }

    public bool KeyExists(string key)
    {
        lock (_gate) return _keys.ContainsKey(key);
    }

    /// <summary>Every key, for checking nothing was left behind.</summary>
    public IReadOnlyCollection<string> Keys
    {
        get { lock (_gate) return _keys.Keys.ToList(); }
    }
}
