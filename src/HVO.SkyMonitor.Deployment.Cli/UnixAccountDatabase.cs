using System.Globalization;
using System.Text;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Proves private-group membership from complete, injectable group and passwd enumerations.</summary>
internal sealed class UnixAccountDatabase(Func<IEnumerable<string>> groups, Func<IEnumerable<string>> accounts,
    Func<bool> hasCompleteSources)
{
    // getent success does not prove complete NSS enumeration (for example, SSSD disables enumeration by default).
    // Only verified files-only configurations are supported. Read the complete files directly so a read failure
    // cannot be mistaken for end-of-enumeration; reject invalid encoding rather than replacing account characters.
    internal static UnixAccountDatabase System { get; } = FromFileSources(
        () => ReadFile("/etc/nsswitch.conf"), () => ReadFile("/etc/group"), () => ReadFile("/etc/passwd"));

    internal static UnixAccountDatabase FromFileSources(Func<IEnumerable<string>> configuration,
        Func<IEnumerable<string>> groups, Func<IEnumerable<string>> accounts)
        => new(groups, accounts, () => NssFileEnumeration.IsComplete(configuration()));

    internal bool IsPrivateGroup(uint uid, uint primaryGid, uint directoryGid)
    {
        if (directoryGid != primaryGid) return false;
        try
        {
            if (!hasCompleteSources()) return false;
            var groupEntries = groups().Select(line => Fields(line, 4)).ToArray();
            var accountEntries = accounts().Select(line => Fields(line, 7)).ToArray();
            if (groupEntries.Length == 0 || accountEntries.Length == 0) return false;
            foreach (var group in groupEntries)
            {
                _ = Id(group[2]);
                if (group[3].Length > 0 && group[3].Split(',').Any(member => !ValidName(member))) return false;
            }
            foreach (var account in accountEntries)
            {
                _ = Id(account[2]);
                _ = Id(account[3]);
            }
            if (groupEntries.Select(group => group[0]).Distinct(StringComparer.Ordinal).Count() != groupEntries.Length ||
                groupEntries.Select(group => Id(group[2])).Distinct().Count() != groupEntries.Length ||
                accountEntries.Select(account => account[0]).Distinct(StringComparer.Ordinal).Count() != accountEntries.Length ||
                accountEntries.Select(account => Id(account[2])).Distinct().Count() != accountEntries.Length)
                return false;
            var user = accountEntries.SingleOrDefault(account => Id(account[2]) == uid);
            var privateGroup = groupEntries.SingleOrDefault(group => Id(group[2]) == directoryGid);
            return user is not null && privateGroup is not null && Id(user[3]) == primaryGid &&
                   privateGroup[0] == user[0] &&
                   (privateGroup[3].Length == 0 || privateGroup[3].Split(',').All(member => member == user[0])) &&
                   accountEntries.All(account => Id(account[3]) != directoryGid || Id(account[2]) == uid);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or
                                          OverflowException or InstallerException or OperationCanceledException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static string[] Fields(string line, int count)
    {
        var fields = line.Split(':');
        return fields.Length == count && ValidName(fields[0])
            ? fields : throw new FormatException("The account database could not be parsed.");
    }

    private static bool ValidName(string name)
        => name.Length > 0 && name[0] is not ('+' or '-') && !name.Any(character => char.IsWhiteSpace(character) || character is ':' or ',');

    private static uint Id(string value) => uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);

    internal static string[] ReadFile(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        // NSS reads physical LF-delimited lines. ReadLine would turn a bare CR into a separate entry.
        var lines = reader.ReadToEnd().Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
