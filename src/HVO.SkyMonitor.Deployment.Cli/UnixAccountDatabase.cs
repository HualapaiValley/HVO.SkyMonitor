using System.Globalization;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Proves private-group membership from complete, injectable group and passwd enumerations.</summary>
internal sealed class UnixAccountDatabase(Func<IEnumerable<string>> groups, Func<IEnumerable<string>> accounts)
{
    // Enumerate through NSS, rather than assuming /etc contains every account on the host. Fixed commands and
    // arguments never pass through a shell; missing tools, unsupported enumeration and lookup failures refuse.
    internal static UnixAccountDatabase System { get; } = new(
        () => ReadDatabase("group"), () => ReadDatabase("passwd"));

    internal bool IsPrivateGroup(uint uid, uint primaryGid, uint directoryGid)
    {
        if (directoryGid != primaryGid) return false;
        try
        {
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
                                          OverflowException or InstallerException or OperationCanceledException)
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

    private static string[] ReadDatabase(string database)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = new ProcessRunner().RunAsync("/usr/bin/getent", [database], timeout.Token).GetAwaiter().GetResult();
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError))
            throw InstallerException.Refused("The account database could not be read.");
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
