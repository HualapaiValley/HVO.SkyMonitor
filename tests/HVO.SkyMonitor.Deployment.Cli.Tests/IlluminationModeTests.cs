using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class IlluminationModeTests
{
    private static readonly Guid ApplicationIdentity = Guid.Parse("65c0dd43-6490-4f10-b972-0f68c79e45a8");
    private static readonly Guid InstanceId = Guid.Parse("e8260a29-0717-4730-a9ea-7780b3a8d377");
    private static readonly string[] Arguments =
    [
        "cameraagent", "install", "--friendly-name", "Camera", "--catalog-bundle", "/srv/catalog.bundle",
        "--image-ref", $"sha256:{new string('a', 64)}"
    ];

    [TestMethod]
    public void OmittedMode_PreservesTheLegacyConfigurationAndRequestIdentity()
    {
        var request = Request(null);
        var generated = CameraConfiguration.Generate(request, ApplicationIdentity);
        using var document = JsonDocument.Parse(generated.Json);

        Assert.IsFalse(document.RootElement.GetProperty("module").GetProperty("options").TryGetProperty("illuminationMode", out _));
        Assert.AreEqual("6ec0a9605d346d76f5869accda8a5ac50b32d3cfda8942dbb05b0a36085e7f41", generated.Sha256);
        Assert.AreEqual("df46995abab4cdee5f33813d3076c512b9a279b9a53178a3f39be852fb132be5",
            CameraAgentInstaller.ComputeRequestSha256(request, InstanceId));
    }

    [TestMethod]
    [DataRow("LegacyScalarSolar")]
    [DataRow("SolarDriven")]
    [DataRow("ControlledNight")]
    public void CommandLine_ExplicitModeIsEmittedAndBoundToCanonicalIdentity(string mode)
    {
        var request = CommandLine.Parse([.. Arguments, "--illumination-mode", mode], false, InstallRequest.DefaultProductRoot);
        AssertConfiguration(request, mode);
    }

    [TestMethod]
    [DataRow("LegacyScalarSolar")]
    [DataRow("SolarDriven")]
    [DataRow("ControlledNight")]
    public void Answers_ExplicitModeRoundTripsIntoTheGeneratedConfiguration(string mode)
    {
        var answers = InstallAnswers.Parse(System.Text.Encoding.UTF8.GetBytes(AnswersJson(mode)), "camera.answers.json");
        var serialized = JsonSerializer.Serialize(answers, InstallAnswersJsonContext.Default.InstallAnswers);
        var restored = InstallAnswers.Parse(System.Text.Encoding.UTF8.GetBytes(serialized), "saved.answers.json");

        AssertConfiguration(restored.ToRequest(InstallRequest.DefaultProductRoot), mode);
    }

    [TestMethod]
    [DataRow("controllednight")]
    [DataRow("ControlledNite")]
    [DataRow("2")]
    [DataRow("")]
    public void InvalidMode_IsAUsageErrorOnTheCommandLineAndInAnswers(string mode)
    {
        var commandError = Assert.ThrowsExactly<InstallUsageException>(() =>
            CommandLine.Parse([.. Arguments, "--illumination-mode", mode], false, InstallRequest.DefaultProductRoot));
        StringAssert.Contains(commandError.Message, "--illumination-mode", StringComparison.Ordinal);
        var answersError = Assert.ThrowsExactly<InstallUsageException>(() =>
        {
            var answers = InstallAnswers.Parse(System.Text.Encoding.UTF8.GetBytes(AnswersJson(mode)), "camera.answers.json");
            answers.ToRequest(InstallRequest.DefaultProductRoot).Validate();
        });
        StringAssert.Contains(answersError.Message, "illumination", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void ExplicitMode_IsPartOfTheInstallationRequestIdentity()
    {
        var legacy = CameraAgentInstaller.ComputeRequestSha256(Request(null), InstanceId);
        var night = CameraAgentInstaller.ComputeRequestSha256(Request("ControlledNight"), InstanceId);
        var solar = CameraAgentInstaller.ComputeRequestSha256(Request("SolarDriven"), InstanceId);

        Assert.AreNotEqual(legacy, night);
        Assert.AreNotEqual(solar, night);
    }

    [TestMethod]
    public void ControlledNight_MatchesTheSharedRuntimeAcceptanceFixture()
    {
        var generated = CameraConfiguration.Generate(Request("ControlledNight"), ApplicationIdentity);
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "installer-controlled-night.json")));

        Assert.AreEqual(generated.Sha256, CanonicalSha256(fixture.RootElement));
    }

    [TestMethod]
    [DataRow("2")]
    [DataRow("true")]
    public void Answers_NonStringIlluminationIsRejected(string value)
    {
        var json = JsonNode.Parse(AnswersJson("ControlledNight"))!.AsObject();
        json["illuminationMode"] = JsonNode.Parse(value);
        Assert.ThrowsExactly<InstallUsageException>(() =>
            InstallAnswers.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()), "camera.answers.json"));
    }

    internal static string CanonicalSha256(JsonElement element)
        => Convert.ToHexStringLower(Convert.FromHexString(CaptureContractJson.ComputeCanonicalJsonSha256(element)));

    internal static InstallRequest Request(string? mode)
    {
        var json = JsonSerializer.SerializeToNode(new InstallRequest
        {
            FriendlyName = "Camera",
            CatalogBundle = "/srv/catalog.bundle",
            ImageReference = $"sha256:{new string('a', 64)}"
        }, InstallRequestJsonContext.Default.InstallRequest)!.AsObject();
        if (mode is not null) json["illuminationMode"] = mode;
        return JsonSerializer.Deserialize(json, InstallRequestJsonContext.Default.InstallRequest)!;
    }

    private static string AnswersJson(string mode) => $$"""
        {"schemaVersion":1,"instanceId":"{{InstanceId:D}}","friendlyName":"Camera",
         "catalogBundle":"/srv/catalog.bundle","imageReference":"sha256:{{new string('a', 64)}}",
         "illuminationMode":{{JsonSerializer.Serialize(mode)}}}
        """;

    private static void AssertConfiguration(InstallRequest request, string mode)
    {
        request.Validate();
        var generated = CameraConfiguration.Generate(request, ApplicationIdentity);
        using var document = JsonDocument.Parse(generated.Json);
        Assert.AreEqual(mode, document.RootElement.GetProperty("module").GetProperty("options").GetProperty("illuminationMode").GetString());
        Assert.AreEqual(generated.Sha256, CanonicalSha256(document.RootElement));
        var legacy = CameraConfiguration.Generate(Request(null), ApplicationIdentity);
        Assert.AreNotEqual(legacy.Sha256, generated.Sha256);
        Assert.AreEqual(legacy.RigProfileSha256, generated.RigProfileSha256);
        Assert.AreEqual(legacy.ScheduleSha256, generated.ScheduleSha256);
        var legacyJson = JsonNode.Parse(legacy.Json)!.AsObject();
        var changedJson = JsonNode.Parse(generated.Json)!.AsObject();
        changedJson["module"]!["options"]!.AsObject().Remove("illuminationMode");
        Assert.IsTrue(JsonNode.DeepEquals(legacyJson, changedJson), "Only the requested module option may change.");
    }
}
