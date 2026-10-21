namespace HVO.SkyMonitor.Deployment;

/// <summary>Recognizes configurations whose complete account and membership sources are local files.</summary>
internal static class NssFileEnumeration
{
    internal static bool IsComplete(IEnumerable<string> configuration)
    {
        var databases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in configuration)
        {
            // NSS scans byte whitespace, not Unicode whitespace, and does not strip inline # source tokens.
            var line = rawLine.Trim(' ', '\t', '\r', '\n', '\v', '\f');
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.Any(character => !char.IsAscii(character))) return false;
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || line.Contains('\\', StringComparison.Ordinal)) return false;
            var database = line[..separator].Trim();
            if (database.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
                return false;
            if (database is not ("passwd" or "group" or "initgroups")) continue;
            // Additional providers, action clauses and duplicate entries have no verified completeness contract.
            // initgroups can independently add supplementary membership even with files-only passwd/group.
            if (!databases.Add(database) || line[(separator + 1)..].Trim() != "files") return false;
        }
        return databases.Contains("passwd") && databases.Contains("group");
    }
}
