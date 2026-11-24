using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// How a Silo-layout bootstrap creates or adopts its bucket and writer (#1966 review). Every decision is made by the
/// installer from <c>mcli</c>'s machine-readable answers, parsed whole and strictly: every bucket row, every object
/// version, the versioning answer, every access key row and each candidate key's own answer, with its effective policy.
/// Nothing is created, enabled or removed unless every row is an empty store or exactly this instance's own earlier
/// attempt; the one changing script then proves the store is still byte for byte what was approved before it acts.
/// </summary>
internal static class SiloBootstrapStore
{
    /// <summary>A store's raw inventory as <c>mcli</c> answered it, and what the installer read from it.</summary>
    internal sealed record Inventory(string Buckets, string Objects, string Versioning, string Keys)
    {
        public IReadOnlyList<string> BucketNames { get; init; } = [];
        public bool BucketPresent { get; init; }
        public bool VersioningEnabled { get; init; }
        public IReadOnlyList<string> AccessKeys { get; init; } = [];
        public bool AnyInheritsPolicy { get; init; }
        public bool AnyTemporaryKey { get; init; }
    }

    /// <summary>A fresh bootstrap's read-only preflight: the store must hold no bucket and no access key.</summary>
    public static async Task<Inventory> ProveEmptyAsync(Func<string, Task<ProcessResult>> execute, string bucket, CancellationToken cancellationToken)
    {
        var inventory = await ReadAsync(execute, bucket, cancellationToken).ConfigureAwait(false);
        if (inventory.BucketNames.Count != 0)
            throw Refused("store-not-empty");
        if (inventory.AccessKeys.Count != 0 || inventory.AnyTemporaryKey)
            throw Refused("store-has-access-keys");
        return inventory;
    }

    /// <summary>Creates the bucket and the writer in a store <see cref="ProveEmptyAsync"/> proved empty, if it still is.</summary>
    public static async Task<(string AccessKey, string SecretKey)> CreateAsync(
        Func<string, Task<ProcessResult>> execute, string bucket, int generation, Inventory empty, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(empty);
        return Created(await execute(SiloRuntimeProfile.ApplyBootstrapScript(bucket, generation, Sha256(empty), [], "create", []))
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Resumes an interrupted creation under this instance's own bootstrap intent. The store may hold nothing but this
    /// instance's bucket, empty, and earlier writer keys of this generation: each with the shape Silo generates, the root
    /// user as its parent, the writer's name and description, no inherited policy, and exactly the writer's
    /// least-privilege policy. Anything else is refused with nothing changed.
    /// </summary>
    public static async Task<(string AccessKey, string SecretKey)> AdoptAsync(
        Func<string, Task<ProcessResult>> execute, string bucket, int generation, string rootUser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        var inventory = await ReadAsync(execute, bucket, cancellationToken).ConfigureAwait(false);
        if (inventory.BucketNames.Any(name => name != bucket + "/"))
            throw Refused("store-holds-foreign-content");
        if (inventory.BucketPresent && inventory.Objects.Length != 0)
            throw Refused("bucket-not-empty");
        // A temporary key, or a key that inherits its parent's (the root user's) policy, has more than a writer's grants.
        if (inventory.AnyTemporaryKey || inventory.AnyInheritsPolicy)
            throw Refused("store-has-foreign-access-key");
        if (inventory.AccessKeys.Count != 0)
        {
            if (inventory.AccessKeys.Any(static key => !SiloRuntimeProfile.IsGeneratedKey(key)))
                throw Refused("store-has-foreign-access-key");
            var answer = SiloRuntimeProfile.ParseScriptResult(
                await execute(SiloRuntimeProfile.KeyInfoScript(inventory.AccessKeys)).ConfigureAwait(false), "key-info", "bucket bootstrap");
            if (!answer.TryGetProperty("info", out var encoded) || encoded.ValueKind != JsonValueKind.Array ||
                encoded.GetArrayLength() != inventory.AccessKeys.Count)
                throw Refused("keys-unreadable");
            var index = 0;
            foreach (var item in encoded.EnumerateArray())
            {
                var info = Decode(item.ValueKind == JsonValueKind.String ? item.GetString() : null);
                if (!IsOwnWriter(info, inventory.AccessKeys[index], bucket, generation, rootUser))
                    throw Refused("store-has-foreign-access-key");
                index++;
            }
        }
        var action = !inventory.BucketPresent ? "create" : inventory.VersioningEnabled ? "none" : "enable-versioning";
        return Created(await execute(SiloRuntimeProfile.ApplyBootstrapScript(
            bucket, generation, Sha256(inventory), inventory.AccessKeys, action, inventory.AccessKeys)).ConfigureAwait(false));
    }

    /// <summary>Reads and strictly parses the store's inventory; any answer it cannot read whole is ambiguous and refused.</summary>
    private static async Task<Inventory> ReadAsync(Func<string, Task<ProcessResult>> execute, string bucket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var answer = SiloRuntimeProfile.ParseScriptResult(
            await execute(SiloRuntimeProfile.InventoryStoreScript(bucket)).ConfigureAwait(false), "inventory", "bucket bootstrap");
        string Field(string name) => answer.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Decode(value.GetString())
            : throw Refused("store-unreadable");
        var inventory = new Inventory(Field("buckets"), Field("objects"), Field("versioning"), Field("keys"));

        // Every bucket row must be a successful folder row with a name; names are judged by the caller.
        var names = new List<string>();
        foreach (var row in Lines(inventory.Buckets))
        {
            if (Text(row, "status") != "success" || Text(row, "type") != "folder" || Text(row, "key") is not { Length: > 0 } key)
                throw Refused("store-unreadable");
            names.Add(key);
        }
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count) throw Refused("store-unreadable");
        var present = names.Contains(bucket + "/", StringComparer.Ordinal);
        var enabled = false;
        if (present)
        {
            var versioning = Object(inventory.Versioning) ?? throw Refused("bucket-unreadable");
            if (Text(versioning, "status") != "success" || versioning["versioning"] is not JsonObject state ||
                state["status"] is not JsonValue status || !status.TryGetValue<string>(out var value))
                throw Refused("bucket-unreadable");
            enabled = value == "Enabled";
        }
        else if (inventory.Objects.Length != 0 || inventory.Versioning.Length != 0)
        {
            throw Refused("store-unreadable");
        }

        // Every access key row of the root user, service accounts and temporary keys alike.
        var keys = Object(inventory.Keys) ?? throw Refused("keys-unreadable");
        if (Text(keys, "status") != "success") throw Refused("keys-unreadable");
        if (keys["stsKeys"] is not (null or JsonArray)) throw Refused("keys-unreadable");
        var temporary = keys["stsKeys"] is JsonArray { Count: > 0 };
        var inheriting = false;
        var accessKeys = new List<string>();
        switch (keys["svcaccs"])
        {
            case null:
                break;
            case JsonArray accounts:
                foreach (var account in accounts)
                {
                    if (account is not JsonObject row || Text(row, "accessKey") is not { Length: > 0 } accessKey ||
                        row["impliedPolicy"] is not JsonValue implied || !implied.TryGetValue<bool>(out var inherits))
                        throw Refused("keys-unreadable");
                    inheriting |= inherits;
                    accessKeys.Add(accessKey);
                }
                break;
            default:
                throw Refused("keys-unreadable");
        }
        if (accessKeys.Distinct(StringComparer.Ordinal).Count() != accessKeys.Count) throw Refused("keys-unreadable");
        return inventory with
        {
            BucketNames = names,
            BucketPresent = present,
            VersioningEnabled = enabled,
            AccessKeys = accessKeys,
            AnyInheritsPolicy = inheriting,
            AnyTemporaryKey = temporary
        };
    }

    /// <summary>Whether a key's own answer is exactly an earlier writer of this generation for this bucket.</summary>
    internal static bool IsOwnWriter(string info, string accessKey, string bucket, int generation, string rootUser)
    {
        var answer = Object(info);
        return answer is not null && Text(answer, "status") == "success" && Text(answer, "accessKey") == accessKey &&
               Text(answer, "parentUser") == rootUser && Text(answer, "name") == SiloRuntimeProfile.WriterNameFor(generation) &&
               Text(answer, "description") == $"HVO CameraAgent writer generation {generation}" &&
               answer["policy"] is JsonObject policy &&
               Canonical(policy) is { } observed && observed == Canonical(JsonNode.Parse(SiloRuntimeProfile.WriterPolicy(bucket))!.AsObject());
    }

    /// <summary>
    /// A policy's effective grants in one canonical form: its version and each statement's effect, actions and resources as
    /// sorted sets, statements sorted. Null for anything else a statement could carry (a condition, a principal, a negated
    /// action or resource), which this installer never writes and therefore never adopts.
    /// </summary>
    internal static string? Canonical(JsonObject policy)
    {
        if (policy.Count != 2 || Text(policy, "Version") is not "2012-10-17" || policy["Statement"] is not JsonArray statements || statements.Count == 0)
            return null;
        var normalized = new List<string>();
        foreach (var node in statements)
        {
            if (node is not JsonObject statement || statement.Any(static property => property.Key is not ("Effect" or "Action" or "Resource")) ||
                Text(statement, "Effect") is not ("Allow" or "Deny") || Set(statement["Action"]) is not { } actions || Set(statement["Resource"]) is not { } resources)
                return null;
            normalized.Add($"{Text(statement, "Effect")}|{string.Join(',', actions)}|{string.Join(',', resources)}");
        }
        normalized.Sort(StringComparer.Ordinal);
        return string.Join('\n', normalized);
    }

    private static string[]? Set(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var one) => [one],
        JsonArray many when many.Count != 0 && many.All(static item => item is JsonValue value && value.TryGetValue<string>(out _)) =>
            [.. many.Select(static item => item!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        _ => null
    };

    /// <summary>The digest the changing script recomputes over the byte-stable answers: buckets, objects and versioning.</summary>
    internal static string Sha256(Inventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var material = string.Join('\n', inventory.Buckets, inventory.Objects, inventory.Versioning);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static (string AccessKey, string SecretKey) Created(ProcessResult result)
    {
        var created = SiloRuntimeProfile.ParseScriptResult(result, "created", "bucket bootstrap");
        return (created.GetProperty("accessKey").GetString() ?? string.Empty, created.GetProperty("secretKey").GetString() ?? string.Empty);
    }

    private static List<JsonObject> Lines(string text)
    {
        var rows = new List<JsonObject>();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            rows.Add(Object(line) ?? throw Refused("store-unreadable"));
        }
        return rows;
    }

    private static JsonObject? Object(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonObject node, string name)
        => node[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Decode(string? base64)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64 ?? throw Refused("store-unreadable")));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw Refused("store-unreadable");
        }
    }

    private static InstallerException Refused(string reason) => InstallerException.Refused($"The Silo bucket bootstrap step was refused: {reason}.");
}
