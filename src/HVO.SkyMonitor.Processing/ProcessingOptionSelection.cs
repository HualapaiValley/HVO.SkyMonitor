using System.Collections.Immutable;

namespace HVO.SkyMonitor.Processing;

/// <summary>An immutable ordered selection. Source tokens travel with untouched occurrences during an edit.</summary>
public sealed class ProcessingOptionSelection : IEquatable<ProcessingOptionSelection>
{
    public static ProcessingOptionSelection Empty { get; } = new([]);

    internal ProcessingOptionSelection(IEnumerable<string> items, IEnumerable<string?>? sourceTokens = null)
    {
        Items = items.ToImmutableArray();
        if (Items.Any(static item => item is null))
            throw new ArgumentException("Collection items cannot be null.", nameof(items));
        SourceTokens = sourceTokens?.ToImmutableArray() ?? [.. Items.Select(static _ => (string?)null)];
    }

    public ImmutableArray<string> Items { get; }
    internal ImmutableArray<string?> SourceTokens { get; }

    public ProcessingOptionSelection Set(int index, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Items[index] == value ? this : new(Items.SetItem(index, value), SourceTokens.SetItem(index, null));
    }

    public ProcessingOptionSelection Add(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(Items.Add(value), SourceTokens.Add(null));
    }

    public ProcessingOptionSelection RemoveAt(int index) => new(Items.RemoveAt(index), SourceTokens.RemoveAt(index));

    public ProcessingOptionSelection Move(int index, int destination)
        => new(Items.RemoveAt(index).Insert(destination, Items[index]),
            SourceTokens.RemoveAt(index).Insert(destination, SourceTokens[index]));

    public bool Equals(ProcessingOptionSelection? other)
        => other is not null && Items.SequenceEqual(other.Items, StringComparer.Ordinal);

    public override bool Equals(object? obj) => obj is ProcessingOptionSelection other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items) hash.Add(item, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
