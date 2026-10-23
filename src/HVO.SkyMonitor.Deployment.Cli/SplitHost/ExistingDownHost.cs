using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Typed, confined private-file and owner-session operations over the shared OS primitive seam.</summary>
internal sealed class ExistingDownHost(IDeploymentHost host, SplitHostInventory inventory, JsonObject preflight, PartialPrepareState state, ExistingDownRequest request)
{
    // Preserve the retained transport's response-key compatibility, including its opaque Metadata exception.
    private static readonly Dictionary<string, string> ResponseKeys = ("Id:id Name:name LatitudeDegrees:latitudeDegrees LongitudeDegrees:longitudeDegrees ElevationMeters:elevationMeters TimeZoneId:timeZoneId RegistrationId:registrationId Status:status DeviceId:deviceId ObservatoryId:observatoryId DevicePublicId:devicePublicId IsAuthoritative:isAuthoritative Registrations:registrations VerificationCode:verificationCode ExpiresAtUtc:expiresAtUtc Envelope:envelope FriendlyName:friendlyName IsProvisioned:isProvisioned ConfiguredAgentId:configuredAgentId ActiveConfigurationSha256:activeConfigurationSha256 RequestToken:requestToken HeaderName:headerName State:state Version:version Replayed:replayed CentralFrameCount:centralFrameCount MaximumCaptureSequence:maximumCaptureSequence CaptureControl:captureControl Value:value FleetAgentInstanceId:fleetAgentInstanceId MaximumHeartbeatSequence:maximumHeartbeatSequence CentralArtifactCount:centralArtifactCount CurrentRigProfileVersion:currentRigProfileVersion CurrentRigProfileHash:currentRigProfileHash LastHeartbeatReceivedAtUtc:lastHeartbeatReceivedAtUtc CaptureWindow:captureWindow CaptureSequence:captureSequence CaptureId:captureId Artifacts:artifacts ArtifactId:artifactId Role:role ChecksumSha256:checksumSha256 ByteLength:byteLength ObjectState:objectState ObjectVerifiedAtUtc:objectVerifiedAtUtc Sources:sources CompletedDerivativeCount:completedDerivativeCount IssuedAtUtc:issuedAtUtc ActivatedAtUtc:activatedAtUtc LineageSourceCount:lineageSourceCount LatestArtifacts:latestArtifacts RecipeVersion:recipeVersion RecipeName:recipeName RecipeSemanticVersion:recipeSemanticVersion RecipeImplementationVersion:recipeImplementationVersion Width:width Height:height PixelFormat:pixelFormat")
        .Split(' ').Select(pair => pair.Split(':')).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
    internal static JsonObject NormalizeResponse(string body)
    {
        JsonNode? Normalize(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(ResponseKeys.GetValueOrDefault(pair.Key, pair.Key), pair.Key is "Metadata" or "metadata" ? pair.Value?.DeepClone() : Normalize(pair.Value)))),
            JsonArray array => new JsonArray(array.Select(Normalize).ToArray()),
            _ => node?.DeepClone()
        };
        var result = (JsonObject)Normalize(Parse(body))!;
        if (result.ContainsKey("registrationId") && result["status"] is JsonValue status && status.TryGetValue<int>(out var number) && number is >= 0 and <= 2) result["status"] = new[] { "Pending", "Active", "Revoked" }[number];
        return result;
    }
    public async Task<string> CorrelateAsync(DeploymentTarget target, CancellationToken token)
    {
        using (var files = host.Open(target, token)) new PartialPrepareTarget(files, target).Authenticate();
        var retained = preflight.Array("targets").SingleOrDefault(n => n!.Text("name") == target.Name);
        Require(retained is not null && retained.Text("hostIdentity") == target.HostIdentity && retained.Text("hostName") == target.HostName && retained.Text("architecture") == target.Architecture && retained.Text("dockerDaemonIdentity") == target.DockerDaemonIdentity, "preflight-correlation-mismatch");
        var binding = await DockerJsonAsync(["context", "inspect", target.DockerContext, "--format", "{\"name\":{{json .Name}},\"host\":{{json .Endpoints.docker.Host}}}"], token).ConfigureAwait(false);
        Require(binding.Text("name") == target.DockerContext, "docker-context-mismatch"); var endpoint = binding.Text("host");
        Require(endpoint.StartsWith("ssh://", StringComparison.Ordinal) || endpoint.StartsWith("unix://", StringComparison.Ordinal), "docker-context-invalid-endpoint");
        var info = await DockerJsonAsync(["--host", endpoint, "info", "--format", "{\"ID\":{{json .ID}},\"Name\":{{json .Name}},\"Architecture\":{{json .Architecture}},\"OSType\":{{json .OSType}},\"ServerVersion\":{{json .ServerVersion}}}"], token).ConfigureAwait(false);
        Require(info.Text("ID") == target.DockerDaemonIdentity && info.Text("Name") == target.HostName && info.Text("OSType") == "linux" && info.Text("ServerVersion").Length > 0 && PartialPrepareTarget.Architecture(info.Text("Architecture")) == target.Architecture, "docker-daemon-correlation-mismatch");
        return endpoint;
    }
    private async Task<JsonObject> DockerJsonAsync(string[] args, CancellationToken token)
    {
        var result = await host.DockerAsync(args, token).ConfigureAwait(false); Require(result.ExitCode == 0, "docker-unavailable"); return Parse(result.StandardOutput);
    }
    private JsonNode TargetNode(DeploymentTarget target) => new[] { inventory.Document["logicHost"]! }.Concat(inventory.Document.Array("cameraAgents").Select(n => n!)).Concat(inventory.Document["sharedServices"] is { } shared ? [shared] : []).Single(n => n.Text("name") == target.Name);
    public static bool AllowedRemote(string path, DeploymentTarget target)
    {
        if (!Root(path) || !path.StartsWith(target.RuntimeRoot + "/", StringComparison.Ordinal)) return false;
        var relative = path[(target.RuntimeRoot.Length + 1)..];
        return relative is "config/private/owner-password" or "config/secrets/LocalIdentity__AdminPasswordFile" ||
            relative.StartsWith(".hvo-deploy/", StringComparison.Ordinal) && Match(Path.GetFileName(path), "(?:owner-password|owner[.]cookies|owner[.]headers|[A-Za-z0-9._-]+-control[.]headers|LocalIdentity__AdminPasswordFile|bootstrap-request[.]json|[A-Za-z0-9._-]+-envelope[.]json)") ||
            Match(relative, "[.]hvo-deploy/(?:bootstrap|smoke|measure|campaign|down)-[A-Za-z0-9._-]+/(?:login[.]html|login-response[.]html|owner-verification[.]json|antiforgery[.]json)") ||
            Match(relative, "[.]hvo-deploy/down-[A-Za-z0-9._-]+/(?:pause|pause-response|final-pause|final-pause-response|continuity-boundary|continuity-[0-9]+|summary-[0-9]+)[.]json") ||
            Match(relative, "[.]hvo-deploy/uploads/hvo-upload-(?:up|bootstrap|smoke|measure|acceptance-run|transient-confirm|down)-[0-9]+-[0-9a-f]{32}[.]tmp");
    }
    private JsonArray Registry()
    {
        var path = request.StateRoot + "/private-upload-registry.json";
        if (!state.Exists("private-upload-registry.json")) state.WriteState("private-upload-registry.json", new JsonArray());
        var entries = JsonNode.Parse(PartialPrepareState.ReadPrivate(path)) as JsonArray ?? throw Error("private-upload-registry-invalid");
        Require(entries.Count <= 256, "private-upload-registry-invalid"); return entries;
    }
    private void Register(DeploymentTarget target, string path)
    {
        Require(AllowedRemote(path, target), "private-path-not-allowed"); var entries = Registry(); var node = TargetNode(target);
        foreach (var entry in entries.Where(n => n!.Text("path") == path)) Require(entry!.Text("kind") == "remote-private" && entry.Text("phase") == "down" && JsonNode.DeepEquals(entry!["target"], node), "private-path-already-registered");
        if (!entries.Any(n => n!.Text("path") == path)) entries.Add(new JsonObject { ["phase"] = "down", ["kind"] = "remote-private", ["target"] = node.DeepClone(), ["path"] = path });
        state.WriteState("private-upload-registry.json", entries);
    }
    public async Task ReconcileAsync(CancellationToken token)
    {
        var entries = Registry(); var retained = (JsonArray)entries.DeepClone();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        // Validate the complete registration set before erasing its first entry.
        foreach (var entry in entries)
        {
            Keys(entry!, "phase kind target path"); var path = entry!.Text("path"); var kind = entry.Text("kind");
            Require(paths.Add(path) && entry.Text("phase") is "up" or "bootstrap" or "smoke" or "measure" or "acceptance-run" or "transient-confirm" or "down", "private-upload-registry-invalid");
            var target = inventory.Targets.SingleOrDefault(t => t.Name == entry!["target"]!.Text("name"));
            Require(target is not null && JsonNode.DeepEquals(entry!["target"], TargetNode(target)), "private-upload-registry-invalid");
            if (kind == "local-private") Require(Root(path) && Beneath(path, request.StateRoot) && Match(Path.GetFileName(path), "(?:central[.]headers|" + target.Name + "-(?:owner-password|antiforgery[.]headers|antiforgery[.]json|envelope[.]json|bootstrap-request[.]json))") && (Path.GetFileName(path) != "central.headers" || target.Component == "logicHost"), "private-upload-registry-invalid");
            else Require(kind is "upload-temp" or "remote-private" && AllowedRemote(path, target) && (kind == "upload-temp") == path.Contains("/.hvo-deploy/uploads/", StringComparison.Ordinal), "private-upload-registry-invalid");
        }
        Exception? failure = null;
        foreach (var entry in entries)
        {
            var path = entry!.Text("path"); var kind = entry.Text("kind");
            var target = inventory.Targets.Single(t => t.Name == entry!["target"]!.Text("name"));
            try
            {
                if (kind == "local-private")
                {
                    SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(path)!);
                    if (NativeLinux.TryGetNodeIdentity(path) is not null) { _ = PartialPrepareState.ReadPrivate(path); File.Delete(path); }
                }
                else
                {
                    await CorrelateAsync(target, token).ConfigureAwait(false);
                    using (var files = host.Open(target, token)) Remove(files, target, path);
                    await CorrelateAsync(target, token).ConfigureAwait(false);
                }
                var at = retained.Select((node, index) => (node, index)).Single(pair => pair.node!.Text("path") == path).index;
                retained.RemoveAt(at); state.WriteState("private-upload-registry.json", retained);
            }
            catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException or OverflowException)
            {
                // One inaccessible target must not strand credentials on the remaining reachable targets.
                failure ??= exception;
            }
        }
        token.ThrowIfCancellationRequested();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public async Task ReconcileStoppedCameraAsync(DeploymentTarget target, CancellationToken token)
    {
        var setting = target.RuntimeRoot + "/config/secrets/LocalIdentity__AllowMissingAdminPassword";
        var directory = Path.GetDirectoryName(setting)!;
        await CorrelateAsync(target, token).ConfigureAwait(false);
        bool ready;
        using (var files = host.Open(target, token))
        {
            Components(files, directory);
            var node = Stat(files, setting);
            ready = node is not null && files.Call("read", OpenRestartSetting(files, setting, node)) == Convert.ToBase64String("true"u8);
        }
        // Preserve an already-published true setting. A missing or interrupted partial write is repairable.
        if (!ready) await WriteAsync(target, setting, "true", token, false).ConfigureAwait(false);
        using (var files = host.Open(target, token))
        {
            Components(files, directory);
            var node = Stat(files, setting) ?? throw Error("restart-setting-missing");
            var handle = OpenRestartSetting(files, setting, node);
            Require(files.Call("read", handle) == Convert.ToBase64String("true"u8), "restart-setting-invalid");
            files.Call("sync", files.Call("path", handle));
            files.Call("sync", directory);
            Require(Stat(files, setting) == node && files.Call("read", handle) == Convert.ToBase64String("true"u8), "private-file-changed");
        }
        await CorrelateAsync(target, token).ConfigureAwait(false);
        // An intent may predate LoginAsync, which normally registers these seeded credentials.
        // Register only after the restart setting and its directory entry are durable: failure cleanup erases registrations.
        Register(target, target.RuntimeRoot + "/config/private/owner-password");
        Register(target, target.RuntimeRoot + "/config/secrets/LocalIdentity__AdminPasswordFile");
        await ReconcileAsync(token).ConfigureAwait(false);
    }
    private static string OpenRestartSetting(IDeploymentFiles files, string path, DeploymentNode node)
    {
        Require(node.File && node.Links == 1 && node.Mode == 384, "unsafe-private-file");
        var handle = files.Call("open", path);
        Require(DeploymentNode.Parse(files.Call("fstat", handle)) == node && Stat(files, path) == node, "private-file-changed");
        return handle;
    }
    private static string ReadSecretSource(string path)
    {
        var identity = NativeLinux.GetNodeIdentity(path);
        Require(identity.Type == NativeLinux.RegularFileNodeType && identity.Uid == NativeLinux.getuid() && identity.LinkCount == 1 && identity.Mode is UnixFileMode.UserRead or (UnixFileMode.UserRead | UnixFileMode.UserWrite), "unsafe-secret-source");
        using var stream = SafeFileSystem.OpenRegularFileRead(path);
        Require(NativeLinux.GetOpenNodeIdentity(stream.SafeFileHandle, path) == identity && stream.Length <= 4 * 1024 * 1024, "unsafe-secret-source");
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false); return reader.ReadToEnd();
    }
    internal static Dictionary<string, string> ReadSecrets(SplitHostInventory inventory)
    {
        var path = inventory.Document["secretSource"]!.Text("path"); Require(Root(path), "noncanonical-secret-source");
        SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(path)!);
        var content = ReadSecretSource(path); Require(!content.Any(c => char.IsControl(c) && c != '\n'), "control-character-rejected");
        var lines = content.Split('\n'); var count = lines.Length; if (lines[^1].Length == 0) count--;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var at = lines[i].IndexOf('=', StringComparison.Ordinal); Require(at is >= 1 and <= 128 && Match(lines[i][..at], "[A-Z][A-Z0-9_]{0,127}"), "invalid-secret-entry");
            Require(values.TryAdd(lines[i][..at], lines[i][(at + 1)..]), "duplicate-secret-reference");
        }
        foreach (var reference in inventory.Document["secretSource"]!.Array("requiredReferences")) Require(values.ContainsKey(reference!.GetValue<string>()), "required-reference-missing");
        return values;
    }
    private static DeploymentNode? Stat(IDeploymentFiles files, string path) => DeploymentNode.Parse(files.Call("stat", path));
    private static void Components(IDeploymentFiles files, string path)
    {
        var current = "";
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) { current += "/" + component; Require(Stat(files, current) is { Directory: true }, "unsafe-private-path"); }
    }
    private static void Remove(IDeploymentFiles files, DeploymentTarget target, string path)
    {
        Require(AllowedRemote(path, target), "private-path-not-allowed");
        // Missing parents prove absence, but existing components must never be symlinks.
        var current = "";
        foreach (var component in Path.GetDirectoryName(path)!.Split('/', StringSplitOptions.RemoveEmptyEntries)) { current += "/" + component; var part = Stat(files, current); if (part is null) return; Require(part.Directory, "unsafe-private-path"); }
        var node = Stat(files, path); if (node is null) return;
        Require(node.File && node.Links == 1, "unsafe-private-file"); var handle = files.Call("open", path);
        Require(DeploymentNode.Parse(files.Call("fstat", handle)) == node && Stat(files, path) == node, "private-file-changed");
        files.Call("unlink", path); Require(Stat(files, path) is null, "private-file-retained");
    }
    public async Task EnsureDirectoryAsync(DeploymentTarget target, string path, CancellationToken token)
    {
        Require(Root(path) && Beneath(path, target.RuntimeRoot), "unsafe-private-path"); await CorrelateAsync(target, token).ConfigureAwait(false);
        using var files = host.Open(target, token); Components(files, Path.GetDirectoryName(path)!);
        if (Stat(files, path) is null) files.Call("mkdir", path);
        Require(Stat(files, path) is { Directory: true, Mode: 448 }, "unsafe-private-directory");
    }
    public async Task WriteAsync(DeploymentTarget target, string path, string content, CancellationToken token, bool register = true)
    {
        if (register) Register(target, path);
        else Require(path == target.RuntimeRoot + "/config/secrets/LocalIdentity__AllowMissingAdminPassword", "unsafe-private-path");
        await CorrelateAsync(target, token).ConfigureAwait(false);
        using (var files = host.Open(target, token))
        {
            Components(files, Path.GetDirectoryName(path)!);
            if (Stat(files, path) is { } old) { Require(old.File && old.Links == 1 && old.Mode == 384, "unsafe-private-file"); files.Call("unlink", path); }
            files.Call("write", path, content); Require(Stat(files, path) is { File: true, Links: 1, Mode: 384 }, "unsafe-private-file");
        }
        await CorrelateAsync(target, token).ConfigureAwait(false);
    }
    public async Task<(int Status, string Body)> HttpAsync(DeploymentTarget target, string method, string url, string output, string cookies, string? body, string? headers, CancellationToken token, string[]? form = null, bool redirect = false)
    {
        Register(target, output); Register(target, cookies);
        await CorrelateAsync(target, token).ConfigureAwait(false);
        int status; string bytes;
        using (var files = host.Open(target, token))
        {
            Components(files, Path.GetDirectoryName(output)!);
            foreach (var path in new[] { output, cookies, body, headers }.Where(p => !string.IsNullOrEmpty(p))) if (Stat(files, path!) is { } node) Require(node.File && node.Links == 1 && (node.Mode & 63) == 0, "unsafe-private-file");
            var args = new List<string> { "--silent", "--show-error", "--max-time", "30", "--max-filesize", "1048576", "--request", method, "--output", output, "--write-out", "%{http_code}", "--cookie", cookies, "--cookie-jar", cookies };
            if (form is not null) { args.RemoveAt(7); args.RemoveAt(6); }
            if (redirect) args.Add("--location");
            if (body is not null) args.AddRange(["--header", "Content-Type: application/json", "--data-binary", "@" + body]);
            if (headers is not null) args.AddRange(["--header", "@" + headers]);
            if (form is not null) foreach (var item in form) args.AddRange(["--data-urlencode", item]);
            args.Add(url); var result = files.Call("http", args.ToArray());
            Require(Match(result, "[0-9]{3}"), "http-status-invalid"); status = int.Parse(result, System.Globalization.CultureInfo.InvariantCulture);
            // Curl creates files under an explicit private umask in the primitive adapter.
            var handle = files.Call("open", output); bytes = Encoding.UTF8.GetString(Convert.FromBase64String(files.Call("read", handle)));
            Require(bytes.Length <= 1024 * 1024, "http-response-too-large");
        }
        await CorrelateAsync(target, token).ConfigureAwait(false); return (status, bytes);
    }
    public async Task<string> LoginAsync(DeploymentTarget target, string remote, CancellationToken token)
    {
        var node = TargetNode(target); var endpoint = node.Text("internalEndpoint"); var password = remote + "/owner-password"; var cookies = remote + "/owner.cookies";
        var secrets = ReadSecrets(inventory); var reference = node.Text("ownerPasswordSecretReference");
        Require(secrets.TryGetValue(reference, out var ownerPassword) && ownerPassword.Length > 0 && Encoding.UTF8.GetByteCount(ownerPassword) <= 16384, "secret-reference-missing");
        var seededPassword = target.RuntimeRoot + "/config/private/owner-password"; var passwordSetting = target.RuntimeRoot + "/config/secrets/LocalIdentity__AdminPasswordFile";
        Register(target, seededPassword); Register(target, passwordSetting);
        await WriteAsync(target, password, ownerPassword, token).ConfigureAwait(false);
        var login = await HttpAsync(target, "GET", endpoint + "/Account/Login", remote + "/login.html", cookies, null, null, token).ConfigureAwait(false);
        var match = Regex.Match(login.Body, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Require(login.Status == 200 && match.Success, "owner-authentication-failed");
        var posted = await HttpAsync(target, "POST", endpoint + "/Account/Login", remote + "/login-response.html", cookies, null, null, token,
            ["__RequestVerificationToken=" + match.Groups[1].Value, "Input.Email=" + node.Text("ownerEmail"), "Input.Password@" + password, "Input.RememberMe=false", "_handler=login"], true).ConfigureAwait(false);
        Require(posted.Status == 200, "owner-authentication-failed");
        var verified = await HttpAsync(target, "GET", endpoint + "/api/internal/deployment/antiforgery", remote + "/owner-verification.json", cookies, null, null, token).ConfigureAwait(false);
        Require(verified.Status == 200, "owner-authentication-failed");
        foreach (var path in new[] { password, remote + "/login.html", remote + "/login-response.html", remote + "/owner-verification.json", seededPassword, passwordSetting })
        {
            if (path != seededPassword && path != passwordSetting) Register(target, path);
            using var files = host.Open(target, token); Remove(files, target, path);
        }
        await WriteAsync(target, target.RuntimeRoot + "/config/secrets/LocalIdentity__AllowMissingAdminPassword", "true", token, false).ConfigureAwait(false);
        var antiforgery = await HttpAsync(target, "GET", endpoint + "/api/internal/deployment/antiforgery", remote + "/antiforgery.json", cookies, null, null, token).ConfigureAwait(false);
        Require(antiforgery.Status == 200, "antiforgery-token-failed"); var response = Parse(antiforgery.Body); var header = response["headerName"] ?? response["HeaderName"]; var value = response["requestToken"] ?? response["RequestToken"];
        Require(header is not null && value is not null && Match(header.GetValue<string>(), "[A-Za-z0-9-]+") && !header.GetValue<string>().Equals("Idempotency-Key", StringComparison.OrdinalIgnoreCase) && value.GetValue<string>().Length > 0 && !value.GetValue<string>().Any(char.IsControl), "antiforgery-token-invalid");
        var content = header.GetValue<string>() + ": " + value.GetValue<string>() + "\n";
        await WriteAsync(target, remote + "/owner.headers", content, token).ConfigureAwait(false); return content;
    }
}
