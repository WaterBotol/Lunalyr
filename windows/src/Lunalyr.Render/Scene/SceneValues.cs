// Collections for snapshot members. Records compare collection members by reference, and the app builds a new
// LyricStyle from the settings on every change, so a plain list or dictionary would make every style "new" and set
// the lyrics again. These compare by their contents.

using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Lunalyr.Render.Scene;

/// <summary>An immutable list of strings that compares by its items, in order.</summary>
public sealed class ValueList : IReadOnlyList<string>, IEquatable<ValueList>
{
    private readonly string[] _items;

    public ValueList(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    public static ValueList Empty { get; } = new([]);

    public string this[int index] => _items[index];

    public int Count => _items.Length;

    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(ValueList? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items);

    public override bool Equals(object? obj) => Equals(obj as ValueList);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(",", _items);
}

/// <summary>An immutable string-to-string map that compares by its entries (ordinal keys).</summary>
public sealed class ValueMap : IReadOnlyDictionary<string, string>, IEquatable<ValueMap>
{
    private readonly Dictionary<string, string> _entries;

    public ValueMap(IEnumerable<KeyValuePair<string, string>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries) _entries[key] = value;
    }

    public static ValueMap Empty { get; } = new([]);

    public string this[string key] => _entries[key];

    public IEnumerable<string> Keys => _entries.Keys;

    public IEnumerable<string> Values => _entries.Values;

    public int Count => _entries.Count;

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => _entries.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _entries.GetEnumerator();

    public bool Equals(ValueMap? other)
    {
        if (other is null || other.Count != Count) return false;
        foreach (var (key, value) in _entries)
            if (!other._entries.TryGetValue(key, out var found) || !string.Equals(found, value, StringComparison.Ordinal)) return false;
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ValueMap);

    public override int GetHashCode()
    {
        // Order-independent: XOR of each entry's hash.
        var hash = Count;
        foreach (var (key, value) in _entries) hash ^= HashCode.Combine(StringComparer.Ordinal.GetHashCode(key), StringComparer.Ordinal.GetHashCode(value));
        return hash;
    }
}
