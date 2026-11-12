using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
public sealed class CaptureStoragePolicyTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EveryRuntimeStorageOptionParticipatesInPolicyComparison(bool artifactPolicy)
    {
        var baseline = new FileStorageCaptureProcessingStepOptions
        {
            StorageRoot = Path.Combine(Path.GetTempPath(), "comparison-field-guard"),
            Policies = [new() { StepId = "preview" }]
        };
        var source = artifactPolicy ? (object)baseline.Policies[0] : baseline;
        foreach (var property in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var changed = new FileStorageCaptureProcessingStepOptions
            {
                StorageRoot = baseline.StorageRoot,
                Policies = [new() { StepId = "preview" }]
            };
            var target = artifactPolicy ? (object)changed.Policies[0] : changed;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object value = type == typeof(string) ? "different-field-value"
                : type == typeof(bool) ? !(property.GetValue(source) as bool? ?? false)
                : type == typeof(int) ? (property.GetValue(source) as int? ?? 30) + 1
                : type == typeof(FrameArtifactRole) ? FrameArtifactRole.Preview
                : type == typeof(IReadOnlyList<ArtifactStoragePolicyOptions>)
                    ? new[] { new ArtifactStoragePolicyOptions { StepId = "other" } }
                : throw new AssertFailedException($"Add a comparison probe for {property.Name} ({type.Name}).");
            property.SetValue(target, value);
            CapturePipelineConfig WithOptions(FileStorageCaptureProcessingStepOptions options)
                => new([new("Storage", "storage", Options: JsonSerializer.SerializeToElement(options))]);
            var error = Assert.ThrowsExactly<ValidationException>(() =>
                CaptureStoragePolicy.EnsureMatchesProfile(WithOptions(changed), WithOptions(baseline)), property.Name);
            Assert.AreEqual(artifactPolicy ? "Policies" : property.Name, error.ValidationResult!.MemberNames.Single());
        }
    }

    [TestMethod]
    public void MissingNodesAndNonEquivalentLinuxPathsRemainRejected()
    {
        var profile = Pipeline("/tmp/storage-policy", "preview");
        foreach (var root in new[] { "/tmp/STORAGE-policy", "/tmp/storage-policy\\child", "/tmp/other-storage" })
            Assert.ThrowsExactly<ValidationException>(() => CaptureStoragePolicy.EnsureMatchesProfile(Pipeline(root, "preview"), profile));
        Assert.ThrowsExactly<ValidationException>(() => CaptureStoragePolicy.EnsureMatchesProfile(
            Pipeline("/tmp/storage-policy\\child", "preview"), Pipeline("/tmp/storage-policy/child", "preview")));
        var missing = Assert.ThrowsExactly<ValidationException>(() => CaptureStoragePolicy.EnsureMatchesProfile(profile, null));
        Assert.AreEqual("StorageNode", missing.ValidationResult!.MemberNames.Single());
        CaptureStoragePolicy.EnsureMatchesProfile(profile with { Steps = [profile.Steps[0] with { Enabled = false }] }, null);
    }

    [TestMethod]
    public void DiagnosticProjectionDropsUntrustedMemberNames()
    {
        var error = new ValidationException(new ValidationResult(CaptureStoragePolicy.RejectionMessage,
            ["/private/operator/path", "StorageRoot"]), null, null);
        var message = CaptureStoragePolicy.DescribeMismatch(error);
        StringAssert.Contains(message, "StorageRoot", StringComparison.Ordinal);
        Assert.IsFalse(message.Contains("/private/", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EquivalentRootSpellingAndSelectorCasePreserveSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "private-storage");
        var source = Pipeline(root + "/./", "PREVIEW");
        var before = JsonSerializer.Serialize(source);
        CaptureStoragePolicy.EnsureMatchesProfile(source, Pipeline(root, "preview"));
        Assert.AreEqual(before, JsonSerializer.Serialize(source));
    }

    [TestMethod]
    [DataRow("storageRoot", "StorageRoot")]
    [DataRow("retentionDays", "RetentionDays")]
    [DataRow("updateLatestFrame", "UpdateLatestFrame")]
    [DataRow("queueForUpload", "QueueForUpload")]
    [DataRow("policies", "Policies")]
    public void MismatchNamesOnlyDifferingFields(string member, string expected)
    {
        var profile = Pipeline("/private/operator-root", "preview");
        var raw = JsonSerializer.SerializeToNode(profile.Steps[0].Options!.Value)!.AsObject();
        raw[member] = member switch
        {
            "storageRoot" => JsonSerializer.SerializeToNode("/private/other-root"),
            "retentionDays" => JsonSerializer.SerializeToNode(90),
            "policies" => JsonSerializer.SerializeToNode(Array.Empty<object>()),
            _ => JsonSerializer.SerializeToNode(false)
        };
        var candidate = profile with { Steps = [profile.Steps[0] with { Options = JsonSerializer.SerializeToElement(raw) }] };
        var error = Assert.ThrowsExactly<ValidationException>(() => CaptureStoragePolicy.EnsureMatchesProfile(candidate, profile));
        Assert.AreEqual(CaptureStoragePolicy.RejectionMessage, error.Message);
        CollectionAssert.AreEqual(new[] { expected }, error.ValidationResult!.MemberNames.ToArray());
        Assert.IsFalse(error.Message.Contains("/private/", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReorderingOverlappingPoliciesIsNotEquivalent()
    {
        var profile = Pipeline("/tmp/storage-policy", "preview");
        var options = new FileStorageCaptureProcessingStepOptions
        {
            StorageRoot = "/tmp/storage-policy",
            Policies = [new() { StepId = "preview", QueueForUpload = true }, new() { Role = FrameArtifactRole.Preview, QueueForUpload = false }]
        };
        var first = profile with { Steps = [profile.Steps[0] with { Options = JsonSerializer.SerializeToElement(options) }] };
        var secondOptions = JsonSerializer.SerializeToNode(options)!;
        // Preserve the two policies' values while reversing which equally specific match wins.
        var reversed = options.Policies.Reverse().ToArray();
        secondOptions[nameof(options.Policies)] = JsonSerializer.SerializeToNode(reversed);
        var second = first with { Steps = [first.Steps[0] with { Options = JsonSerializer.SerializeToElement(secondOptions) }] };
        CaptureStoragePolicy.EnsureMatchesProfile(first, first);
        CaptureStoragePolicy.EnsureMatchesProfile(second, second);
        var forward = Assert.ThrowsExactly<ValidationException>(() => CaptureStoragePolicy.EnsureMatchesProfile(first, second));
        var backward = Assert.ThrowsExactly<ValidationException>(() => CaptureStoragePolicy.EnsureMatchesProfile(second, first));
        Assert.AreEqual("Policies", forward.ValidationResult!.MemberNames.Single());
        Assert.AreEqual("Policies", backward.ValidationResult!.MemberNames.Single());
    }

    private static CapturePipelineConfig Pipeline(string root, string selector)
        => new([new("Storage", "storage", Options: JsonSerializer.SerializeToElement(new
        {
            storageRoot = root, retentionDays = 30, updateLatestFrame = true, queueForUpload = true,
            policies = new[] { new { stepId = selector, retentionDays = 90 } }
        }))]);
}
