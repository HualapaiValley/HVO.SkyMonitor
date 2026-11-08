using System.Security.Claims;
using System.Security.Cryptography;
using Bunit;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCloudScenarioTests
{
    [TestMethod]
    [DataRow("MeasuredStellarAssociations", 1, "outputVariant", "virtual-measured", " ")]
    [DataRow("MeasuredAstrometry", 2, "captureStride", "120", "59")]
    [DataRow("CaptureSatelliteTracks", 3, "outputVariant", "virtual-tracks", " ")]
    [DataRow("CaptureSatelliteLayer", 4, "inputVariant", "virtual-tracks", " ")]
    [DataRow("Storage", 5, "retentionDays", "60", "0")]
    public async Task NodePanelsUseVirtualSkyAndRealRevisionServiceForEditRejectSaveReload(
        string alias, int index, string key, string value, string invalid)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-virtual-node-panels");
        try
        {
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            var config = Config(CameraPixelFormat.Mono16, null) with
            {
                AgentId = "virtual-node-panels",
                Pipeline = ProcessingGraphOperationsTests.RemainingOptionsPipeline(root)
            };
            var module = Module();
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var capture = await module.CaptureAsync(new(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(capture.Frame);
            TestContext.WriteLine($"VirtualSky {alias} input SHA256={Convert.ToHexStringLower(SHA256.HashData(capture.Frame.PixelData.Span))}");

            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            using var context = new BunitContext();
            context.JSInterop.Mode = JSRuntimeMode.Loose;
            var authorization = context.AddAuthorization();
            authorization.SetAuthorized("owner");
            authorization.SetClaims(new Claim(ClaimTypes.NameIdentifier, "virtual-owner"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType));
            var accessor = new CameraAgentConfigurationAccessor();
            accessor.SetConfiguration(config);
            var service = new CameraAgentProcessingGraphUiService(
                context.Services.GetRequiredService<AuthenticationStateProvider>(), context.Services.GetRequiredService<IAuthorizationService>(),
                operations, provider.GetRequiredService<ICaptureProcessingPipelineFactory>(), accessor, null, TimeProvider.System,
                NullLogger<CameraAgentProcessingGraphUiService>.Instance);
            context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(service);
            var navigation = context.Services.GetRequiredService<NavigationManager>();
            navigation.NavigateTo("/operations/pipeline/graphs/new?from=" + initial.ActiveRevisionId);
            var cut = context.Render<ProcessingGraphEditorPage>();
            var selector = "#node-options-" + index + "-" + key;
            cut.WaitForElement(selector).Change(value);
            if (alias == "CaptureSatelliteTracks") cut.Find("#node-options-4-inputVariant").Change(value);
            if (alias == "CaptureSatelliteLayer") cut.Find("#node-options-3-outputVariant").Change(value);
            cut.Find("form").Submit();
            cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled")));
            cut.Find(selector).Change(invalid);
            cut.Find("form").Submit();
            Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
            Assert.IsNotEmpty(cut.FindAll(".option-errors[role='alert'] li"));
            cut.Find(selector).Change(value);
            cut.Find("form").Submit();
            cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled")));
            cut.Find("button.btn-primary").Click();
            cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "saved as an immutable revision", StringComparison.Ordinal));
            var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, registry.ActiveRevisionId);
            var saved = registry.Revisions.Single(revision => revision.RevisionId != initial.ActiveRevisionId);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, saved.Lifecycle);
            cut.Dispose();
            navigation.NavigateTo("/operations/pipeline/graphs/new?from=" + saved.RevisionId);
            var reload = context.Render<ProcessingGraphEditorPage>();
            reload.WaitForAssertion(() => Assert.AreEqual(value, reload.Find(selector).GetAttribute("value")));
            reload.Find("form").Submit();
            Assert.IsTrue(reload.Find("button.btn-primary").HasAttribute("disabled"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
