using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment.Contracts;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class PreflightAncillaryTests
{
    private static readonly string[] SchemaKeys2 = new[] { "distributionMode", "artifactRoot", "tag", "builder", "registryImmutableTags", "logicHost", "cameraAgent" };
    private static readonly string[] SchemaKeys1 = new[] { "cpus", "memory", "sqlMemory", "sqlMemoryLimitMb" };
    private static readonly string[] SchemaKeys0 = new[] { "name", "friendlyName", "instanceId", "applicationIdentity", "catalogId", "cookieName", "sshHost", "dockerContext", "expectedArchitecture", "expectedHostName", "expectedHostIdentity", "expectedDockerDaemonIdentity", "runtimeRoot", "runtimeOwner", "internalEndpoint", "publicEndpoint", "trustedProxyAddresses", "ports" };
    internal static JsonObject Fixture(string name)
    {
        using var stream = typeof(PreflightAncillaryTests).Assembly.GetManifestResourceStream("HVO.SkyMonitor.Deployment.Cli.Tests.PreflightFixtures.json")!;
        using var reader = new StreamReader(stream); return (JsonObject)Parse(reader.ReadToEnd())[name]!.DeepClone();
    }
    [TestMethod]
    [DataRow("valid", null)]
    [DataRow("raw-missing", "persistent-local-capture-history-missing")]
    [DataRow("raw-reset", "persistent-local-capture-history-behind")]
    [DataRow("raw-behind", "persistent-local-capture-history-behind")]
    [DataRow("outbox-missing", "persistent-local-artifact-history-missing")]
    [DataRow("outbox-reset", "persistent-local-artifact-history-missing")]
    [DataRow("artifact-behind", "persistent-local-artifact-history-behind")]
    [DataRow("artifact-other-device", "persistent-local-artifact-history-behind")]
    [DataRow("fleet-missing", "persistent-local-fleet-history-missing")]
    [DataRow("fleet-other-instance", "persistent-local-fleet-identity-mismatch")]
    [DataRow("fleet-behind", "persistent-local-fleet-history-behind")]
    public void PersistentCaptureContinuity_PreservesAllHistoricalBarriers(string fault, string? reason)
    {
        var local = Fixture("local-continuity"); var durable = local["durable"]!;
        switch (fault)
        {
            case "raw-missing": durable["rawIngressDatabaseExists"] = false; durable["captureSequences"] = new JsonArray(); break;
            case "raw-reset": durable["captureSequences"]![0]!["lastSequence"] = 0; break;
            case "raw-behind": durable["captureSequences"]![0]!["lastSequence"] = 11; break;
            case "outbox-missing": durable["artifactOutboxDatabaseExists"] = false; durable["latestArtifacts"] = new JsonArray(); break;
            case "outbox-reset": durable["artifactOutboxMaximumRecordId"] = 0; durable["artifactOutboxMaximumAuditId"] = 0; break;
            case "artifact-behind": durable["latestArtifacts"]![0]!["captureSequence"] = 11; break;
            case "artifact-other-device": durable["latestArtifacts"]![0]!["agentId"] = "device-west"; break;
            case "fleet-missing": durable["fleetDatabaseExists"] = false; durable["fleetAgentInstanceId"] = null; break;
            case "fleet-other-instance": durable["fleetAgentInstanceId"] = "55555555-5555-5555-5555-555555555555"; break;
            case "fleet-behind": durable["fleetMaximumSequence"] = 11; durable["fleetNextSequence"] = 12; break;
        }
        Assert.AreEqual(reason, PreflightContinuity.Validate("persistent", local, Fixture("central-continuity")));
        Assert.IsNull(PreflightContinuity.Validate("isolated", local, Fixture("central-continuity")));
    }
    [TestMethod]
    [DataRow("valid", true)]
    [DataRow("duplicate", false)]
    [DataRow("extra", false)]
    public void TransientAllowlist_RejectsDuplicateAndUnknownFields(string fault, bool valid)
    {
        var list = Fixture("transient-allowlist");
        if (fault == "duplicate") list.Array("events").Add(list.Array("events")[0]!.DeepClone());
        if (fault == "extra") list["events"]![0]!["unexpected"] = true;
        Assert.AreEqual(valid, PreflightTransient.Allowlist(list, "test-run"));
    }
    [TestMethod]
    [DataRow("valid", true)]
    [DataRow("notification-other-assessment", false)]
    [DataRow("notification-before-review", false)]
    public void TransientAudit_RequiresSameAssessmentAndCausalNotification(string fault, bool valid)
    {
        var detail = Fixture("transient-detail");
        Assert.IsTrue(PreflightTransient.DetailEligible(detail, "11111111-1111-4111-8111-111111111111", "synthetic-east", "synthetic-campaign-test-run"));
        if (fault == "notification-other-assessment") detail["event"]!["notifications"]![0]!["assessmentId"] = "55555555-5555-4555-8555-555555555555";
        if (fault == "notification-before-review") detail["event"]!["notifications"]![0]!["createdUtc"] = "2026-08-15T00:00:00Z";
        Assert.AreEqual(valid, PreflightTransient.AuditReady(detail, "33333333-3333-4333-8333-333333333333", "22222222-2222-4222-8222-222222222222", "synthetic-campaign-test-run"));
    }
    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, "existing-message")]
    public void TransientMailpit_UsesBaselineUnlessResumed(bool resumed, string? expected)
        => Assert.AreEqual(expected, PreflightTransient.MailpitId(Fixture("transient-mailpit"), ["existing-message"], "11111111-1111-4111-8111-111111111111", "split-host-owner@hvo.local", "2026-08-15T00:00:00Z", resumed));
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TransientEvidence_RequiresTheCompleteBoundReviewAndNotification(bool tampered)
    {
        var evidence = Fixture("transient-evidence"); if (tampered) ((JsonObject)evidence["events"]![0]!).Remove("notificationId");
        Assert.AreEqual(!tampered, PreflightTransient.Evidence(evidence, "test-run", new string('a', 64), new string('b', 40), new string('c', 64), Fixture("transient-allowlist")));
    }
    [TestMethod]
    [DataRow("valid", true)]
    [DataRow("issuer", false)]
    [DataRow("token", false)]
    [DataRow("malformed", false)]
    [DataRow("oversized", false)]
    [DataRow("status", false)]
    public void OidcDiscovery_RejectsEveryHistoricalAuthorityFailure(string fault, bool valid)
    {
        const string authority = "https://public.example.test:5174";
        var doc = new JsonObject { ["issuer"] = authority + "/", ["token_endpoint"] = authority + "/connect/token" };
        if (fault == "issuer") doc["issuer"] = "https://wrong.example.test:5174/";
        if (fault == "token") doc["token_endpoint"] = "https://wrong.example.test:5174/connect/token";
        if (fault == "oversized") doc["padding"] = new string('a', 65536);
        Assert.AreEqual(valid, PreflightTransient.OidcDiscovery(fault == "malformed" ? "{" : doc.ToJsonString(), fault == "status" ? "302" : "200", authority));
    }
    [TestMethod]
    [DataRow(65535, true)]
    [DataRow(65536, true)]
    [DataRow(65537, false)]
    [DataRow(66125, false)]
    public void OidcDiscovery_EnforcesUtf8ByteBoundary(int byteCount, bool accepted)
    {
        const string authority = "https://public.example.test:5174";
        const string prefix = "{\"issuer\":\"https://public.example.test:5174/\",\"token_endpoint\":\"https://public.example.test:5174/connect/token\",\"padding\":\"";
        const string suffix = "\"}";
        var paddingBytes = byteCount - Encoding.UTF8.GetByteCount(prefix + suffix);
        var body = prefix + new string('\u00e9', paddingBytes / 2) + (paddingBytes % 2 == 0 ? "" : "a") + suffix;
        Assert.AreEqual(byteCount, Encoding.UTF8.GetByteCount(body));
        Assert.IsTrue(body.Length < 65536);
        Assert.AreEqual(accepted, PreflightTransient.OidcDiscovery(body, "200", authority));
    }
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ExampleSchemaAndExecutableInventory_Agree()
    {
        var root = PreflightInventoryTests.Repository();
        await SplitHostInventory.ReadAsync(root + "/deploy/split-host/inventory.example.yml", "isolated", new ProcessRunner(), CancellationToken.None);
        var schema = Parse(DeploymentFakeHost.Read(root + "/deploy/split-host/inventory.schema.json"));
        var result = await new ProcessRunner().RunAsync("jq", ["-c", "--arg", "mode", "isolated", SplitHostInventory.Resource("InventoryV8.jq"), root + "/deploy/split-host/inventory.example.yml"], CancellationToken.None);
        Assert.AreEqual("true", result.StandardOutput.Trim());
        CollectionAssert.AreEqual(SchemaKeys0, schema["$defs"]!["applicationTarget"]!.Array("required").Select(n => n!.GetValue<string>()).ToArray());
        Assert.AreEqual(8, schema["properties"]!["schemaVersion"]!["const"]!.GetValue<int>());
        Assert.AreEqual("#/$defs/runtimeRoot", schema["properties"]!["productRoot"]!.Text("$ref"));
        Assert.AreEqual("#/$defs/catalogInstallation", schema["properties"]!["catalogs"]!["items"]!.Text("$ref"));
        CollectionAssert.AreEqual(SchemaKeys1, schema["properties"]!["deployment"]!["properties"]!["limits"]!.Array("required").Select(n => n!.GetValue<string>()).ToArray());
        Assert.AreEqual("string", schema["properties"]!["deployment"]!["allOf"]![0]!["then"]!["properties"]!["limits"]!["properties"]!["sqlMemory"]!.Text("type"));
        CollectionAssert.AreEqual(SchemaKeys2, schema["properties"]!["images"]!.Array("required").Select(n => n!.GetValue<string>()).ToArray());
        Assert.AreEqual("#/$defs/infrastructureTarget", schema["properties"]!["sharedServices"]!["oneOf"]![1]!.Text("$ref"));
        Assert.AreEqual("#/$defs/applicationTarget", schema["properties"]!["logicHost"]!.Text("$ref"));
        Assert.AreEqual("#/$defs/runtimeRoot", schema["$defs"]!["applicationTarget"]!["allOf"]![0]!["properties"]!["runtimeRoot"]!.Text("$ref"));
        foreach (var type in new[] { "infrastructureTarget", "cameraAgentTarget" }) Assert.AreEqual("#/$defs/runtimeRoot", schema["$defs"]![type]!["properties"]!["runtimeRoot"]!.Text("$ref"));
        Assert.AreEqual("^/", schema["$defs"]!["cameraAgentTarget"]!["properties"]!["moduleConfigPath"]!.Text("pattern"));
        StringAssert.Contains(schema["$defs"]!["applicationTarget"]!["properties"]!["publicEndpoint"]!.Text("pattern"), ":", StringComparison.Ordinal);
    }
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CanonicalWorkloads_PinSourceConfigurationOptionsAndReadout()
    {
        var root = PreflightInventoryTests.Repository(); var workloads = Parse(DeploymentFakeHost.Read(root + "/deploy/split-host/workloads/canonical-workloads.json"));
        Assert.AreEqual(1, workloads["schemaVersion"]!.GetValue<int>());
        foreach (var (key, path, warmup, measured, width, height, pixel) in new[]
        {
            ("W0", "deploy/split-host/workloads/w0-virtual-mono16.json", 0, 1, 64, 48, "Mono16"),
            ("W1", "src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json", 5, 30, 1936, 1216, "Mono16"),
            ("W2", "src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json", 5, 30, 3096, 2080, "BayerRggb16")
        })
        {
            var profile = workloads["profiles"]![key]!; Assert.AreEqual(path, profile.Text("sourcePath")); Assert.AreEqual(warmup, profile["warmupOperations"]!.GetValue<int>()); Assert.AreEqual(measured, profile["measuredOperations"]!.GetValue<int>());
            Assert.AreEqual(width, profile["width"]!.GetValue<int>()); Assert.AreEqual(height, profile["height"]!.GetValue<int>()); Assert.AreEqual(pixel, profile.Text("pixelFormat")); Assert.AreEqual(2025, profile["seed"]!.GetValue<int>());
            Assert.AreEqual(profile.Text("sourceSha256"), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(root + "/" + path))));
            foreach (var (expression, field) in new[] { (".", "configurationIdentitySha256"), (".module.options", "optionsSha256") })
            {
                var result = await new ProcessRunner().RunAsync("jq", ["-S", "-c", expression, root + "/" + path], CancellationToken.None); Assert.AreEqual(profile.Text(field), Hash(result.StandardOutput.TrimEnd('\n')));
            }
        }
        var w0 = Parse(DeploymentFakeHost.Read(root + "/deploy/split-host/workloads/w0-virtual-mono16.json"));
        Assert.IsTrue(JsonNode.DeepEquals(w0["rig"]!["readout"], JsonNode.Parse("""{"roi":{"x":0,"y":0,"width":64,"height":48},"binX":1,"binY":1,"binningAlgorithm":"IdentityV1","pixelFormat":"Mono16","sampleDepthBits":16,"containerDepthBits":16,"packing":"ByteAligned","storedCodeTransform":"IdentityV1","levelCodeSpace":"StoredContainer","blackLevel":0,"whiteLevel":65535,"strideBytes":128,"byteOrder":"LittleEndian","cfaPattern":"None"}""")));
    }
}
