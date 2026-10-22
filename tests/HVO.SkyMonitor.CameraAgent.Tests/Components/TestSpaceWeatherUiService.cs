using System.Text.RegularExpressions;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>
/// Replaceable handlers for the space-weather operator service, with the read count and the recorded keys and
/// requests. Every view comes from the real projection builder over synthetic records; nothing reads a store.
/// </summary>
internal sealed class TestSpaceWeatherUiService : ICameraAgentSpaceWeatherUiService
{
    internal static readonly DateTimeOffset Now = SpaceWeatherUiTestData.Now;

    /// <summary>A halt start with sub-minute ticks, so a resume that sent the displayed minute would not match.</summary>
    internal const string HaltSinceText = "2026-10-20T13:07:42.1234567Z";

    internal Func<CancellationToken, ValueTask<OperatorUiResult<SpaceWeatherUiView>>> GetHandler { get; set; } =
        _ => Success(View());

    internal Func<string, ValueTask<OperatorUiResult<SpaceWeatherCommandView>>> RefreshHandler { get; set; } =
        _ => ValueTask.FromResult(Command(OperatorUiResultKind.Success, SpaceWeatherOperations.Disposition.Accepted,
            "space-weather.refresh-queued"));

    internal Func<SpaceWeatherResumeRequest, string, ValueTask<OperatorUiResult<SpaceWeatherCommandView>>> ResumeHandler { get; set; } =
        (_, _) => ValueTask.FromResult(Command(OperatorUiResultKind.Success, SpaceWeatherOperations.Disposition.Applied, "resumed"));

    internal Func<string, string, ValueTask<OperatorUiResult<SpaceWeatherCommandView>>> AcknowledgeHandler { get; set; } =
        (_, _) => ValueTask.FromResult(Command(OperatorUiResultKind.Success, SpaceWeatherOperations.Disposition.Applied,
            "acknowledged"));

    internal int Reads { get; private set; }

    internal List<string> RefreshKeys { get; } = [];

    internal List<(SpaceWeatherResumeRequest Request, string Key)> Resumes { get; } = [];

    internal List<(string Group, string Key)> Acknowledgements { get; } = [];

    public ValueTask<OperatorUiResult<SpaceWeatherUiView>> GetAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return GetHandler(cancellationToken);
    }

    public ValueTask<OperatorUiResult<SpaceWeatherCommandView>> RefreshAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        RefreshKeys.Add(idempotencyKey);
        return RefreshHandler(idempotencyKey);
    }

    public ValueTask<OperatorUiResult<SpaceWeatherCommandView>> ResumeAsync(
        SpaceWeatherResumeRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Resumes.Add((request, idempotencyKey));
        return ResumeHandler(request, idempotencyKey);
    }

    public ValueTask<OperatorUiResult<SpaceWeatherCommandView>> AcknowledgeAsync(
        string group,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        Acknowledgements.Add((group, idempotencyKey));
        return AcknowledgeHandler(group, idempotencyKey);
    }

    internal static ValueTask<OperatorUiResult<SpaceWeatherUiView>> Success(SpaceWeatherUiView view)
        => ValueTask.FromResult(OperatorUiResult<SpaceWeatherUiView>.Success(view));

    /// <summary>A command outcome carrying the service's fixed text for <paramref name="reasonCode"/>.</summary>
    internal static OperatorUiResult<SpaceWeatherCommandView> Command(
        OperatorUiResultKind kind,
        SpaceWeatherOperations.Disposition disposition,
        string reasonCode)
        => new(kind, new SpaceWeatherCommandView(disposition, reasonCode, "synthetic", SpaceWeatherUiTestData.Status()),
            SpaceWeatherOperatorText.Describe(reasonCode));

    internal static OperatorUiResult<SpaceWeatherCommandView> Unavailable()
        => OperatorUiResult<SpaceWeatherCommandView>.Failure(OperatorUiResultKind.Unavailable,
            "The space-weather command could not be completed.");

    internal static SpaceWeatherUiView View(SpaceWeatherUiProjection? projection = null, bool canMutate = true)
        => new(projection ?? Projection(), canMutate);

    /// <summary>
    /// By default one active G5 warning that reaches the site and one pending G1 warning that does not, both
    /// unacknowledged, from a fresh alerts product with the refresh schedule defined and enabled.
    /// </summary>
    internal static SpaceWeatherUiProjection Projection(
        SpaceWeatherOperations.Status? status = null,
        IReadOnlyList<SwpcMessageRecord>? messages = null)
        => SpaceWeatherUiTestData.Project(SpaceWeatherUiTestData.View(messages ?? Notices()), status: status);

    internal static SwpcMessageRecord[] Notices() =>
    [
        SpaceWeatherUiTestData.Warning(1, Now.AddHours(-1), Now.AddHours(2), code: "WARK09", level: "G5"),
        SpaceWeatherUiTestData.Warning(2, Now.AddHours(1), Now.AddHours(4), level: "G1", issued: Now.AddMinutes(-30)),
    ];

    /// <summary>A projection with no data, as the shared source returns for an unreadable store or a failed read.</summary>
    internal static SpaceWeatherUiProjection Unreadable(SpaceWeatherUiState state, bool enabled = true)
        => SpaceWeatherUiProjectionBuilder.Unavailable(state, enabled, SpaceWeatherUiTestData.Site(), Now);

    /// <summary>The default notices after the active G5 warning was acknowledged half an hour ago, with no rise since.</summary>
    internal static SpaceWeatherUiProjection Acknowledged()
        => SpaceWeatherUiTestData.Project(SpaceWeatherUiTestData.View(Notices(),
            acknowledgements: new Dictionary<SwpcMessageNode, SwpcAcknowledgement>
            {
                [SpaceWeatherUiTestData.Node("WARK09", 1)] = new("owner", Now.AddMinutes(-30)),
            }));

    /// <summary>
    /// Fails when <paramref name="text"/> uses wording that claims something can be seen, or calls the absence of
    /// retained notices an absence of space weather. "Unlikely" is a relevance category and stays allowed.
    /// </summary>
    internal static void AssertCompliantWording(string text)
    {
        var banned = Regex.Match(text, @"\b(aurora|auroral|visible|visibility|observable|likely|meteor)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Assert.IsFalse(banned.Success, $"Banned wording '{banned.Value}' in: {text}");
        Assert.IsFalse(text.Contains("no space weather", StringComparison.OrdinalIgnoreCase), text);
    }

    /// <summary>A provider halt whose start keeps the unformatted sub-minute text.</summary>
    internal static SpaceWeatherOperations.HaltStatus Halt(string reasonCode = "rate-limited") => new(reasonCode, HaltSinceText);
}
