using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>A closed typed scalar and its retained provenance, never formatted display text.</summary>
public sealed record FrameFactsScalarV1(string Key, FrameFactsAvailability Availability, FrameFactsValueKind Kind,
    string? Text, long? WholeNumber, double? Number, DateTimeOffset? Timestamp, FrameFactsFactProvenance Provenance);

/// <summary>The complete frozen catalogue used by an opt-in Frame Facts producer on either host.</summary>
public sealed record FrameFactsSnapshotV1(string Version, string IdentitySha256, IReadOnlyList<FrameFactsScalarV1> Facts)
{
    public const string CurrentVersion = "frame-facts-snapshot-v1";

    public static FrameFactsSnapshotV1 Create(FrameFactsRetainedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var facts = FrameFactsRetainedCatalog.Entries.OrderBy(entry => entry.Definition.Key, StringComparer.Ordinal)
            .Select(entry =>
            {
                var fact = context.Tokens.Get(entry.Definition.Key);
                var value = fact.Value;
                return new FrameFactsScalarV1(entry.Definition.Key, fact.Availability, value.Kind,
                    value.Kind == FrameFactsValueKind.Text ? value.Text : null,
                    value.Kind == FrameFactsValueKind.WholeNumber ? value.WholeNumber : null,
                    value.Kind == FrameFactsValueKind.Number ? value.Number : null,
                    value.Kind == FrameFactsValueKind.Timestamp ? value.Timestamp : null,
                    context.Provenance[entry.Definition.Key]);
            }).ToArray();
        var snapshot = new FrameFactsSnapshotV1(CurrentVersion, string.Empty, Array.AsReadOnly(facts));
        return snapshot with { IdentitySha256 = snapshot.ComputeIdentity() };
    }

    public string ComputeIdentity() => CaptureContractJson.ComputeCanonicalJsonSha256(this with { IdentitySha256 = string.Empty });

    /// <summary>Validates the complete ordered snapshot before reconstituting existing evaluator contracts.</summary>
    public FrameFactsTokenContext ToContext()
    {
        if (Version != CurrentVersion || Facts is null || Facts.Count != FrameFactsRetainedCatalog.Entries.Length)
            throw new ArgumentException("Frame Facts snapshot identity or catalogue is invalid.");
        var entries = FrameFactsRetainedCatalog.Entries.OrderBy(entry => entry.Definition.Key, StringComparer.Ordinal).ToArray();
        var values = new List<KeyValuePair<string, FrameFactsFact>>(Facts.Count);
        for (var index = 0; index < Facts.Count; index++)
        {
            var fact = Facts[index];
            if (fact is null || fact.Key != entries[index].Definition.Key || !Enum.IsDefined(fact.Availability) ||
                fact.Provenance is not { } provenance || !Bounded(provenance.SourceContract, 128) ||
                !Bounded(provenance.SourceIdentity, 4096) || !Bounded(provenance.Nature, 512))
                throw new ArgumentException("Frame Facts scalar or provenance is invalid.");
            var present = (fact.Text is null ? 0 : 1) + (fact.WholeNumber.HasValue ? 1 : 0) +
                (fact.Number.HasValue ? 1 : 0) + (fact.Timestamp.HasValue ? 1 : 0);
            FrameFactsFact value;
            if (fact.Availability != FrameFactsAvailability.Available)
            {
                if (fact.Kind != FrameFactsValueKind.None || present != 0)
                    throw new ArgumentException("Unavailable Frame Facts scalars cannot carry values.");
                value = FrameFactsFact.Unavailable(fact.Availability);
            }
            else
            {
                if (fact.Kind != entries[index].Definition.Kind || present != 1)
                    throw new ArgumentException("Frame Facts scalar kind does not match its catalogue.");
                value = FrameFactsFact.Available(fact.Kind switch
                {
                    FrameFactsValueKind.Text when fact.Text is not null => FrameFactsValue.FromText(fact.Text),
                    FrameFactsValueKind.WholeNumber when fact.WholeNumber.HasValue => FrameFactsValue.FromWholeNumber(fact.WholeNumber.Value),
                    FrameFactsValueKind.Number when fact.Number.HasValue => FrameFactsValue.FromNumber(fact.Number.Value),
                    FrameFactsValueKind.Timestamp when fact.Timestamp.HasValue => FrameFactsValue.FromTimestamp(fact.Timestamp.Value),
                    _ => throw new ArgumentException("Frame Facts scalar value is invalid.")
                });
            }
            values.Add(new(fact.Key, value));
        }
        if (!string.Equals(IdentitySha256, ComputeIdentity(), StringComparison.Ordinal))
            throw new ArgumentException("Frame Facts snapshot identity does not match its content.");
        return new(values);
    }

    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}
