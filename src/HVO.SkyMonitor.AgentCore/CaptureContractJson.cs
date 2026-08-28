using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Deterministic JSON and hashing operations for versioned capture contracts.</summary>
public static class CaptureContractJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize(ArtifactManifestV2 manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.SerializeToUtf8Bytes(CanonicalizeManifest(manifest), SerializerOptions);
    }

    public static ArtifactManifestParseResult ParseManifest(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                schemaElement.ValueKind != JsonValueKind.String)
            {
                return Failure(CaptureContractReasonCodes.UnsupportedSchema, "schemaVersion");
            }

            return schemaElement.GetString() switch
            {
                ArtifactManifestV2.CurrentSchemaVersion => ParseCurrent(utf8Json.Span),
                _ => Failure(CaptureContractReasonCodes.UnsupportedSchema, "schemaVersion")
            };
        }
        catch (JsonException)
        {
            return Failure(CaptureContractReasonCodes.InvalidJson, "$");
        }
    }

    public static JsonElement Canonicalize(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, value);
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    public static string ComputeCanonicalJsonSha256(JsonElement value)
    {
        var canonical = JsonSerializer.SerializeToUtf8Bytes(Canonicalize(value));
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    public static string ComputeCanonicalJsonSha256<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ComputeCanonicalJsonSha256(SerializeToElement(value));
    }

    public static JsonElement SerializeToElement<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToElement(value, SerializerOptions);
    }

    public static string ComputeManifestSha256(ArtifactManifestV2 manifest)
        => Convert.ToHexString(SHA256.HashData(Serialize(manifest)));

    public static string ComputeManifestSha256(ReadOnlySpan<byte> manifestJson)
        => Convert.ToHexString(SHA256.HashData(manifestJson));

    public static string ComputeDescriptorSha256(ReconstructionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var canonical = CanonicalizeDescriptor(descriptor);
        return Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(canonical, SerializerOptions)));
    }

    private static ArtifactManifestParseResult ParseCurrent(ReadOnlySpan<byte> json)
    {
        var manifest = JsonSerializer.Deserialize<ArtifactManifestV2>(json, SerializerOptions);
        if (manifest is null)
        {
            return Failure(CaptureContractReasonCodes.InvalidJson, "$");
        }
        var validation = manifest.Validate();
        return validation.IsValid
            ? new(ArtifactManifestDocument.FromCurrent(manifest), validation)
            : new(null, validation);
    }

    private static ArtifactManifestParseResult Failure(string reasonCode, string path)
        => new(null, CaptureContractValidationResult.Failure(reasonCode, path));

    private static ArtifactManifestV2 CanonicalizeManifest(ArtifactManifestV2 manifest)
        => manifest with { Descriptor = CanonicalizeDescriptor(manifest.Descriptor) };

    private static ReconstructionDescriptor CanonicalizeDescriptor(ReconstructionDescriptor descriptor)
    {
        var profiles = descriptor.Profiles;
        var artifact = descriptor.Artifact;
        var recipe = artifact.Recipe;
        var cycleEvidence = descriptor.CycleEvidence;
        var scheduleAdmission = cycleEvidence?.ScheduleAdmission;
        return descriptor with
        {
            Profiles = profiles with
            {
                Rig = NormalizeProfile(profiles.Rig),
                Calibration = NormalizeProfile(profiles.Calibration),
                Mask = NormalizeProfile(profiles.Mask),
                Sensor = NormalizeProfile(profiles.Sensor),
                Processing = NormalizeProfile(profiles.Processing)
            },
            Artifact = artifact with
            {
                ChecksumSha256 = artifact.ChecksumSha256.ToUpperInvariant(),
                Recipe = recipe with
                {
                    Options = Canonicalize(recipe.Options),
                    OptionsSha256 = recipe.OptionsSha256.ToUpperInvariant()
                }
            },
            CycleEvidence = cycleEvidence is null || scheduleAdmission is null
                ? cycleEvidence
                : cycleEvidence with
                {
                    ScheduleAdmission = scheduleAdmission with
                    {
                        ScheduleRevisionSha256 = scheduleAdmission.ScheduleRevisionSha256.ToUpperInvariant(),
                        LocalProfileSha256 = scheduleAdmission.LocalProfileSha256.ToUpperInvariant(),
                        ExpansionSha256 = scheduleAdmission.ExpansionSha256.ToUpperInvariant(),
                        TimeZoneRuleSha256 = scheduleAdmission.TimeZoneRuleSha256.ToUpperInvariant()
                    }
                }
        };
    }

    private static ProfileIdentityDescriptor NormalizeProfile(ProfileIdentityDescriptor profile)
        => profile with { Sha256 = profile.Sha256.ToUpperInvariant() };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
