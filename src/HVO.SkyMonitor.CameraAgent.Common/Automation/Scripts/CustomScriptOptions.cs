using System.Globalization;
using System.Text;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

/// <summary>Owner-controlled startup registration, never writable through automation commands.</summary>
public sealed class CustomScriptOptions
{
    public bool Enabled { get; set; }
    public bool OwnerAuthorized { get; set; }
    public IReadOnlyList<CustomScriptRegistration> Scripts { get; set; } = [];
}

/// <summary>The only qualified interpreter; executable paths and shebang selection are not inputs.</summary>
public enum CustomScriptInterpreter { PosixShell }
public enum CustomScriptArgumentKind { Text, WholeNumber, Boolean }

/// <summary>Non-secret positional data, passed as one argv entry without interpolation.</summary>
public sealed record CustomScriptArgument(CustomScriptArgumentKind Kind, string Value)
{
    internal bool IsValid() => Value is not null && Value.Length <= 256 && !Value.Any(char.IsControl) && Kind switch
    {
        CustomScriptArgumentKind.Text => true,
        CustomScriptArgumentKind.WholeNumber => long.TryParse(Value, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out var number) && number.ToString(CultureInfo.InvariantCulture) == Value,
        CustomScriptArgumentKind.Boolean => Value is "true" or "false",
        _ => false
    };
}

/// <summary>Content, typed arguments and all execution limits form one immutable registered target.</summary>
public sealed class CustomScriptRegistration
{
    public string Id { get; set; } = string.Empty;
    public CustomScriptInterpreter Interpreter { get; set; }
    public string Content { get; set; } = string.Empty;
    public IReadOnlyList<CustomScriptArgument> Arguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
    public int ScratchBytes { get; set; } = 1_048_576;
    public int OutputBytes { get; set; } = 65_536;

    internal bool IsValid() => LocalAutomationDefinitionValidator.IsIdentifier(Id, 48) &&
        Interpreter == CustomScriptInterpreter.PosixShell && !string.IsNullOrWhiteSpace(Content) &&
        !Content.Contains('\0', StringComparison.Ordinal) && Encoding.UTF8.GetByteCount(Content) <= 65_536 &&
        Arguments is { Count: <= 16 } && Arguments.All(static argument => argument is not null && argument.IsValid()) &&
        TimeoutSeconds is >= 1 and <= 300 && ScratchBytes is >= 4096 and <= 8_388_608 && ScratchBytes >= Environment.SystemPageSize &&
        OutputBytes is >= 256 and <= 1_048_576;

    /// <summary>Opaque identity only. No script text, command, path or argument is stored in TaskTarget.</summary>
    public string Target => string.Concat(Id, ".", CaptureContractJson.ComputeCanonicalJsonSha256(new
    {
        Policy = "hvo-custom-script-linux-x64-v4",
        Id,
        Interpreter,
        Content,
        Arguments,
        TimeoutSeconds,
        ScratchBytes,
        OutputBytes
    }));

    internal CustomScriptRegistration Snapshot() => new()
    {
        Id = Id,
        Interpreter = Interpreter,
        Content = Content,
        Arguments = [.. Arguments],
        TimeoutSeconds = TimeoutSeconds,
        ScratchBytes = ScratchBytes,
        OutputBytes = OutputBytes
    };
}
