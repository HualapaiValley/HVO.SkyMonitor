using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using HVO.SkyMonitor.Imaging;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>Real isolated virtual host for the collaborative-browser receipt; no browser-only product substitute.</summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The typed virtual host is retained throughout the manual browser scope.")]
public sealed class FrameFactsEditorPreviewHostTests
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public async Task ServeVirtualFrameFactsEditorForCollaborativeBrowser()
    {
        var evidence = Environment.GetEnvironmentVariable("HVO_FRAME_FACTS_BROWSER_EVIDENCE");
        if (string.IsNullOrWhiteSpace(evidence))
        {
            Assert.Inconclusive("Set HVO_FRAME_FACTS_BROWSER_EVIDENCE to a registered issue scratch directory; use T3 preview and create browser-complete after the documented checks.");
            return;
        }
        Directory.CreateDirectory(evidence);
        await using var host = await CameraAgentKestrelFixture.CreateAsync(scratchRoot: evidence, prepareRoot: async root =>
        {
            var path = Path.Combine(root, "cameraagent.browser.json");
            var config = JsonNode.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false))!;
            config["rig"]!["sensor"]!["widthPixels"] = 1936;
            config["rig"]!["sensor"]!["heightPixels"] = 1216;
            config["rig"]!["sensor"]!["strideBytes"] = 3872;
            config["rig"]!["optics"]!["principalPointX"] = 968;
            config["rig"]!["optics"]!["principalPointY"] = 608;
            config["rig"]!["optics"]!["imageCircleRadiusPixels"] = 600;
            config["rig"]!["pipeline"]!["captureInterval"] = "00:00:03";
            config["schedule"]!["setpointProfiles"]![0]!["captureInterval"] = "00:00:03";
            var storage = config["pipeline"]!["steps"]!.AsArray().Single(step => step!["type"]!.GetValue<string>() == "Storage")!.DeepClone();
            storage["dependsOn"] = new JsonArray("$raw", "CombinedPreview", "ProjectedScene", "Manifest");
            config["pipeline"]!["steps"] = JsonNode.Parse("""
                [
                  {"id":"Calibration","type":"Calibration","order":10,"dependsOn":["$raw"],"options":{"strategy":"None","outputVariant":"none"}},
                  {"id":"ProjectedScene","type":"ProjectedScene","order":15,"dependsOn":["$raw"],"publication":{"persistence":"durable-local"}},
                  {"id":"Stack","type":"RollingCombination","order":25,"dependsOn":["Calibration"],"options":{"windowSize":1,"outputVariant":"rolling-mean"},"publication":{"persistence":"durable-local"}},
                  {"id":"CombinedPreview","type":"CombinedPreview","order":50,"dependsOn":["Stack"],"options":{"outputVariant":"combined-preview"},"publication":{"persistence":"durable-local"}},
                  {"id":"SceneOverlay","type":"ScenePresentationLayer","order":65,"dependsOn":["ProjectedScene"],"publication":{"persistence":"durable-local"}},
                  {"id":"FrameFacts","type":"EnvironmentPresentationLayer","order":72,"dependsOn":["ProjectedScene","CombinedPreview"],"options":{"widthPixels":1936,"heightPixels":1216,"environmentalKinds":[],"future":{"keep":[1,2]}},"publication":{"persistence":"durable-local"}},
                  {"id":"Manifest","type":"OverlayManifest","order":73,"dependsOn":["CombinedPreview","SceneOverlay","FrameFacts"],"publication":{"persistence":"durable-local"}}
                ]
                """);
            config["pipeline"]!["steps"]!.AsArray().Add(storage);
            await File.WriteAllTextAsync(path, config.ToJsonString()).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidence, "browser-host.json"), JsonSerializer.Serialize(new { address = host.BaseAddress, root = host.Root })).ConfigureAwait(false);
        var operations = host.Services.GetRequiredService<IProcessingGraphOperations>();
        var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidence, "registry-before.json"), JsonSerializer.Serialize(registry)).ConfigureAwait(false);
        var archive = host.Services.GetRequiredService<ICameraAgentArchive>();
        var artifacts = host.Services.GetRequiredService<ICameraAgentArtifactService>();
        var stop = DateTimeOffset.UtcNow.AddMinutes(20);
        byte[]? originalLayer = null;
        Guid originalLayerId = default;
        var collectedLegacy = false;
        var collectedRich = false;
        while (DateTimeOffset.UtcNow < stop && !File.Exists(Path.Combine(evidence, "browser-complete")))
        {
            var page = await archive.GetProductPageAsync(new(PageSize: 50, Role: FrameArtifactRole.Metadata), CancellationToken.None).ConfigureAwait(false);
            foreach (var product in page.Items.Where(product => product.ProductSchemaVersion is PresentationMetadataFactsProductV1.CurrentSchemaVersion or PresentationMetadataFactsProductV1.RichTextSchemaVersion))
            {
                var rich = product.ProductSchemaVersion == PresentationMetadataFactsProductV1.RichTextSchemaVersion;
                if (rich ? collectedRich : collectedLegacy) continue;
                var opened = await artifacts.OpenContentAsync(product.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                if (opened.Content is not { } content) continue;
                await using (content.ConfigureAwait(false))
                {
                    using var buffer = new MemoryStream();
                    await content.CopyToAsync(buffer).ConfigureAwait(false);
                    var bytes = buffer.ToArray();
                    var facts = PresentationProcessingProducts.ParseMetadataFacts(bytes);
                    await File.WriteAllBytesAsync(Path.Combine(evidence, rich ? "retained-rich-facts.json" : "retained-legacy-facts.json"), bytes).ConfigureAwait(false);
                    if (!rich && content.Descriptor is { } descriptor)
                    {
                        var snapshot = FrameFactsSnapshotV1.Create(FrameFactsRetainedContext.Create(new(descriptor)));
                        await File.WriteAllBytesAsync(Path.Combine(evidence, "retained-snapshot.json"),
                            JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJson)).ConfigureAwait(false);
                    }
                    var legacyLayer = PresentationLayerProducers.FromMetadataFacts(facts.Corners, 1936, 1216);
                    await File.WriteAllBytesAsync(Path.Combine(evidence, rich ? "rich-legacy-comparison-payload.json" : "retained-legacy-layer.json"),
                        PresentationLayerPayloadJson.Serialize(legacyLayer)).ConfigureAwait(false);
                }
                var layers = await archive.GetProductPageAsync(new(PageSize: 50, Role: FrameArtifactRole.Metadata), CancellationToken.None).ConfigureAwait(false);
                var layer = layers.Items.FirstOrDefault(layer => layer.CaptureId == product.CaptureId && layer.NodeId == "FrameFacts" && layer.ProductSchemaVersion != product.ProductSchemaVersion);
                if (layer is null) continue;
                var layerOpened = await artifacts.OpenContentAsync(layer.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(layerOpened.Content);
                await using (var layerContent = layerOpened.Content)
                {
                    using var buffer = new MemoryStream();
                    await layerContent.CopyToAsync(buffer).ConfigureAwait(false);
                    var bytes = buffer.ToArray();
                    var payload = PresentationLayerPayloadJson.Parse(bytes).Payload!;
                    var layout = new ImageLayout(1936, 1216, CameraPixelFormat.Mono8, 1936);
                    var raster = PresentationLayerCompositor.CompositeDisplay(layout, new byte[layout.RequiredByteLength],
                        [new(payload, true, PresentationRasterBlendMode.Normal, 1_000_000)]);
                    var name = rich ? "retained-rich" : "retained-legacy";
                    await File.WriteAllBytesAsync(Path.Combine(evidence, name + "-payload.json"), bytes).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(Path.Combine(evidence, name + "-render.jpg"), JpegImageCodec.EncodeToJpeg(raster.Layout, raster.Pixels, 95)).ConfigureAwait(false);
                    if (!rich) { originalLayer = bytes; originalLayerId = layer.ArtifactId; }
                }
                if (rich) collectedRich = true; else collectedLegacy = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        Assert.IsTrue(File.Exists(Path.Combine(evidence, "browser-complete")), "Collaborative-browser checklist did not complete within 20 minutes.");
        Assert.IsTrue(collectedLegacy, "No actual virtual legacy facts were produced.");
        Assert.IsTrue(collectedRich, "No actual virtual rich facts were produced after explicit activation.");
        var after = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(evidence, "registry-after.json"), JsonSerializer.Serialize(after)).ConfigureAwait(false);
        var retained = await artifacts.OpenContentAsync(originalLayerId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(retained.Content);
        await using (var original = retained.Content)
        {
            using var bytes = new MemoryStream();
            await original.CopyToAsync(bytes).ConfigureAwait(false);
            CollectionAssert.AreEqual(originalLayer, bytes.ToArray(), "The original retained product must remain byte-identical after activation and rollback.");
            await File.WriteAllTextAsync(Path.Combine(evidence, "retained-original-sha256.txt"), Convert.ToHexString(SHA256.HashData(bytes.ToArray()))).ConfigureAwait(false);
        }
        Assert.AreEqual(registry.ActiveRevisionId, after.ActiveRevisionId, "Browser acceptance must finish by rolling back to the retained original revision.");
    }
}
