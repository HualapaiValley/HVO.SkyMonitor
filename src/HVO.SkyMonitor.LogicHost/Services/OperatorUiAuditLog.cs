namespace HVO.SkyMonitor.LogicHost.Services;

internal static partial class OperatorUiAuditLog
{
    [LoggerMessage(7500, LogLevel.Information, "Operator membership mutation: Operation={Operation}, Outcome={Outcome}, Role={Role}")]
    internal static partial void Membership(ILogger logger, string operation, string outcome, string role);

    [LoggerMessage(7501, LogLevel.Information, "Operator invitation mutation: Operation={Operation}, Outcome={Outcome}, Role={Role}")]
    internal static partial void Invitation(ILogger logger, string operation, string outcome, string role);

    [LoggerMessage(7502, LogLevel.Information, "Operator publication mutation: Operation={Operation}, Outcome={Outcome}, State={State}")]
    internal static partial void Publication(ILogger logger, string operation, string outcome, string state);

    [LoggerMessage(7503, LogLevel.Information, "Operator location disclosure mutation: Outcome={Outcome}, State={State}")]
    internal static partial void Location(ILogger logger, string outcome, string state);

    [LoggerMessage(7504, LogLevel.Information, "Operator installation mutation: Operation={Operation}, Outcome={Outcome}")]
    internal static partial void Installation(ILogger logger, string operation, string outcome);

    [LoggerMessage(7505, LogLevel.Information, "Operator raw download authorization: Outcome={Outcome}, Role={Role}, Range={Range}")]
    internal static partial void RawDownload(ILogger logger, string outcome, string role, string range);

    [LoggerMessage(7506, LogLevel.Information, "Operator central override mutation: Operation={Operation}, Outcome={Outcome}")]
    internal static partial void CentralOverride(ILogger logger, string operation, string outcome);

    [LoggerMessage(7507, LogLevel.Information, "Operator personal preference mutation: Operation={Operation}, Outcome={Outcome}")]
    internal static partial void PersonalPreference(ILogger logger, string operation, string outcome);

    [LoggerMessage(7508, LogLevel.Information, "Operator editorial placement mutation: Operation={Operation}, Outcome={Outcome}, State={State}")]
    internal static partial void EditorialPlacement(ILogger logger, string operation, string outcome, string state);
}
