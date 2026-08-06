namespace HVO.SkyMonitor.LogicHost.Hosting;

internal enum LogicHostHostMode
{
    Runtime,
    DatabaseInitialize
}

internal sealed record LogicHostCommand(
    LogicHostHostMode Mode,
    IReadOnlyList<string> ForwardedArguments);

internal static class LogicHostCommandParser
{
    private const string Prefix = "--host-mode";
    private const string DatabaseInitializeArgument = "--host-mode=database-initialize";

    internal static LogicHostCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var mode = LogicHostHostMode.Runtime;
        var modeSpecified = false;
        var forwarded = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, DatabaseInitializeArgument, StringComparison.Ordinal))
            {
                if (modeSpecified)
                {
                    throw new ArgumentException("The LogicHost host mode can be specified only once.", nameof(arguments));
                }
                mode = LogicHostHostMode.DatabaseInitialize;
                modeSpecified = true;
                continue;
            }
            if (argument.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("The LogicHost host mode is invalid.", nameof(arguments));
            }
            forwarded.Add(argument);
        }
        return new(mode, forwarded);
    }
}
