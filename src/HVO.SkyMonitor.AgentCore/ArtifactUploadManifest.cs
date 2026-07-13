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
    string RelativeArtifactPath,
    SceneProvenance? Scene = null)
{
    public const string CurrentSchemaVersion = "v1";

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

    /// <summary>Validates fields required by the current upload protocol.</summary>
    public void Validate()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(AgentId)
            || ArtifactId == Guid.Empty
            || FrameId == Guid.Empty
            || !Enum.IsDefined(Role)
            || string.IsNullOrWhiteSpace(MediaType)
            || ByteLength < 0
            || string.IsNullOrWhiteSpace(ChecksumSha256)
            || ChecksumSha256.Length != 64
            || ChecksumSha256.Any(static character => !Uri.IsHexDigit(character))
            || CapturedAtUtc.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(RecipeVersion)
            || !IsSafeRelativePath(RelativeArtifactPath))
        {
            throw new ArgumentException("Artifact manifest is invalid.", nameof(ArtifactUploadManifest));
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/')
            || path.StartsWith('\\')
            || path.Length >= 2 && path[1] == ':')
        {
            return false;
        }

        return !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment is "." or "..");
    }
}
