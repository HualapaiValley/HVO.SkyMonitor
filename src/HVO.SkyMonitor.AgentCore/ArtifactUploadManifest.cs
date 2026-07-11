using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>Versioned metadata required to upload one stored artifact idempotently.</summary>
public sealed record ArtifactUploadManifest(
    string SchemaVersion,
    string AgentId,
    Guid ArtifactId,
    Guid FrameId,
    FrameArtifactRole Role,
    string MediaType,
    long ByteLength,
    string ChecksumSha256,
    DateTimeOffset CapturedAtUtc,
    string RecipeVersion,
    string RelativeArtifactPath)
{
    /// <summary>Gets the deterministic idempotency key for this artifact and recipe.</summary>
    public string IdempotencyKey => ComputeIdempotencyKey(AgentId, FrameId, Role, RecipeVersion);

    /// <summary>Computes a stable SHA-256 idempotency key from immutable artifact identity fields.</summary>
    public static string ComputeIdempotencyKey(string agentId, Guid frameId, FrameArtifactRole role, string recipeVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeVersion);
        var bytes = Encoding.UTF8.GetBytes($"{agentId}\n{frameId:N}\n{role}\n{recipeVersion}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
