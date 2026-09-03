using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Single-use test inputs are clearer inline.")]
public sealed class ProcessingGraphDeliveryContractTests
{
    [TestMethod]
    public void DeliveryEnumsRequireDefinedCaseSensitiveStringValues()
    {
        Assert.AreEqual(
            ProcessingGraphDeliveryFactKind.Accepted,
            JsonSerializer.Deserialize<ProcessingGraphDeliveryFactKind>("\"Accepted\""));
        Assert.AreEqual(
            "\"Proposed\"",
            JsonSerializer.Serialize(ProcessingGraphProposalPollDisposition.Proposed));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ProcessingGraphDeliveryFactKind>("\"accepted\""));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ProcessingGraphDeliveryFactKind>("1"));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ProcessingGraphDeliveryFactKind>("\"Unknown\""));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Serialize((ProcessingGraphDeliveryFactKind)int.MaxValue));
    }

    [TestMethod]
    public void AgentCapabilitiesNormalizeAndVerifyEveryIdentityComponent()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            ProcessingGraphAgentCapabilities.Create(null!));

        var capabilities = ProcessingGraphAgentCapabilities.Create(
            [" Upload ", "Preview", "Preview", ""],
            [" gpu ", "fits", "fits", " "]);
        CollectionAssert.AreEqual(new[] { "Preview", "Upload" }, capabilities.StepAliases.ToArray());
        CollectionAssert.AreEqual(new[] { "fits", "gpu" }, capabilities.CapabilityLabels.ToArray());
        Assert.IsTrue(capabilities.HasValidIdentity());
        Assert.IsTrue(ProcessingGraphAgentCapabilities.Create(["Preview"]).HasValidIdentity());

        Assert.IsFalse(new ProcessingGraphAgentCapabilities(
            default, [], capabilities.IdentitySha256).HasValidIdentity());
        Assert.IsFalse(new ProcessingGraphAgentCapabilities(
            [], default, capabilities.IdentitySha256).HasValidIdentity());
        Assert.IsFalse(new ProcessingGraphAgentCapabilities(
            ["\0"], [], capabilities.IdentitySha256).HasValidIdentity());
        Assert.IsFalse(new ProcessingGraphAgentCapabilities(
            [new string('x', 129)], [], capabilities.IdentitySha256).HasValidIdentity());
        Assert.IsFalse(new ProcessingGraphAgentCapabilities(
            [], ["\0"], capabilities.IdentitySha256).HasValidIdentity());
        Assert.IsFalse(new ProcessingGraphAgentCapabilities(
            [], [new string('x', 129)], capabilities.IdentitySha256).HasValidIdentity());
        Assert.IsFalse((capabilities with { StepAliases = ["Upload", "Preview"] }).HasValidIdentity());
        Assert.IsFalse((capabilities with { CapabilityLabels = ["gpu", "fits"] }).HasValidIdentity());
        Assert.IsFalse((capabilities with { IdentitySha256 = new string('0', 64) }).HasValidIdentity());
        Assert.IsFalse((capabilities with
        {
            StepAliases = ImmutableArray.Create("Preview", "Preview")
        }).HasValidIdentity());
    }
}
