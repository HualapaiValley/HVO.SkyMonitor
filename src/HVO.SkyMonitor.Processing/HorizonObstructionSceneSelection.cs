using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>Immutable source-bound eligibility carried to curated composition, including the replay runner.</summary>
public sealed class HorizonObstructionSceneSelection
{
    public const string CurrentSchemaVersion = "horizon-curated-selection-v1";
    public const string InputName = "horizon-curated-selection";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    [JsonConstructor]
    public HorizonObstructionSceneSelection(string schemaVersion, string sceneIdentitySha256,
        string eligibilityIdentitySha256, IReadOnlyList<string> eligibleObjectIds)
    {
        ArgumentNullException.ThrowIfNull(eligibleObjectIds);
        if (schemaVersion != CurrentSchemaVersion || !Sha256(sceneIdentitySha256) || !Sha256(eligibilityIdentitySha256) ||
            eligibleObjectIds.Count > ProjectedSceneDeepSkySelection.MaximumObjectLimit)
            throw new ArgumentException("Invalid horizon selection identity or bound.");
        var frozen = eligibleObjectIds.ToArray();
        for (var i = 0; i < frozen.Length; i++)
            if (string.IsNullOrWhiteSpace(frozen[i]) || frozen[i].Length > 128 || frozen[i].Any(char.IsControl) ||
                i > 0 && StringComparer.Ordinal.Compare(frozen[i - 1], frozen[i]) >= 0)
                throw new ArgumentException("Eligible object identifiers must be bounded, sorted and unique.", nameof(eligibleObjectIds));
        SchemaVersion = schemaVersion;
        SceneIdentitySha256 = sceneIdentitySha256;
        EligibilityIdentitySha256 = eligibilityIdentitySha256;
        EligibleObjectIds = Array.AsReadOnly(frozen);
    }

    public string SchemaVersion { get; }
    public string SceneIdentitySha256 { get; }
    public string EligibilityIdentitySha256 { get; }
    public IReadOnlyList<string> EligibleObjectIds { get; }

    public bool Contains(string objectId) => EligibleObjectIds.Contains(objectId, StringComparer.Ordinal);

    public void ValidateScene(ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.SceneIdentitySha256 != SceneIdentitySha256 || EligibleObjectIds.Any(id =>
                scene.DeepSky?.Objects.Any(item => item.Id == id) != true))
            throw new ArgumentException("Horizon selection does not describe this retained scene.", nameof(scene));
    }

    public ProcessingAuxiliaryInput ToAuxiliaryInput()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(this, JsonOptions)));
        return new(InputName, ProcessingAuxiliaryInputKind.CanonicalJson, SchemaVersion: CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(payload), Payload: payload);
    }

    public static HorizonObstructionSceneSelection Parse(ProcessingAuxiliaryInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Name != InputName || input.Kind != ProcessingAuxiliaryInputKind.CanonicalJson ||
            input.SchemaVersion != CurrentSchemaVersion || input.Payload.Length > 32_768 ||
            input.IdentitySha256 != ProcessingIdentity.ComputePayloadSha256(input.Payload))
            throw new ArgumentException("Invalid horizon selection input.", nameof(input));
        return JsonSerializer.Deserialize<HorizonObstructionSceneSelection>(input.Payload.Span, JsonOptions)
            ?? throw new ArgumentException("Horizon selection is missing.", nameof(input));
    }

    private static bool Sha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
