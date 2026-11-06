using System.Globalization;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>
/// Recognizes the durable run keys of explicitly requested run-now occurrences. A run-now key has the
/// form <c>{definitionId}|{revision16}|n{sequence}</c>, where the sequence is the journal row it was
/// queued as, so it is unique within the definition's revision line and is never reused.
/// </summary>
public static class LocalAutomationRunKeys
{
    private const char RunNowDiscriminator = 'n';
    private const int RevisionPrefixLength = 16;

    /// <summary>
    /// True only for an occurrence run key created by an explicit run-now. A retry's attempt journal
    /// key is not itself a run-now key; the retried occurrence keeps the original run key.
    /// </summary>
    public static bool IsRunNow(string runKey)
    {
        if (string.IsNullOrEmpty(runKey) || runKey.Length > 128)
        {
            return false;
        }
        var parts = runKey.Split('|');
        return parts.Length == 3 &&
            LocalAutomationDefinitionValidator.IsIdentifier(parts[0], LocalAutomationContract.MaximumDefinitionIdLength) &&
            parts[1].Length == RevisionPrefixLength && parts[1].All(char.IsAsciiHexDigit) &&
            parts[2].Length is >= 2 and <= 20 && parts[2][0] == RunNowDiscriminator && parts[2][1] != '0' &&
            long.TryParse(parts[2].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    internal static string CreateRunNow(LocalAutomationRunnerEntry entry, long sequence)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.Definition.DefinitionId}|{entry.RevisionSha256[..RevisionPrefixLength]}|{RunNowDiscriminator}{sequence}");
    }

    /// <summary>The occurrence run key of a journal row, without a retry attempt suffix.</summary>
    internal static string OccurrenceRunKey(string journalRunKey)
    {
        var retry = journalRunKey.LastIndexOf(":retry", StringComparison.Ordinal);
        return retry < 0 ? journalRunKey : journalRunKey[..retry];
    }
}
