using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class CaptureScalarOptionsEditorTests
{
    private static readonly string[] CalibrationStrategies = ["None", "SyntheticReferences", "CalibrationLibrary"];
    private static readonly JsonSerializerOptions CalibrationOptionsJson = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public void CalibrationPanelRetainsModelAndCompatibilityMembersAndExportsAccessibleFixture()
    {
        using var context = new BunitContext();
        var service = Configure(context, "Calibration", """{ "Strategy":"SyntheticReferences", "SyntheticCalibration": { "gain":1, "future":[1e+02] }, "CalibrationPasses":2, "MaxCalibrationSeconds":60, "future":[1e+02] }""");
        var source = service.RevisionDetail!.Pipeline.Steps[0].Options!.Value;
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/operations/pipeline/graphs/new?section=calibration#calibration");
        var cut = context.Render<ProcessingGraphEditorPage>();
        Assert.IsNotNull(cut.Find("#calibration[aria-label='Calibration']"));
        foreach (var field in new[] { "enabled", "strategy", "outputVariant" })
        {
            var id = "node-options-0-" + field;
            Assert.IsNotEmpty(cut.Find("label[for='" + id + "']").TextContent);
            Assert.AreEqual(id + "-help", cut.Find("#" + id).GetAttribute("aria-describedby"));
            Assert.IsNotEmpty(cut.Find("#" + id + "-help").TextContent);
        }
        CollectionAssert.AreEqual(CalibrationStrategies,
            cut.FindAll("#node-options-0-strategy option").Select(static option => option.GetAttribute("value")).ToArray());
        Assert.IsEmpty(cut.FindAll("input[id*='CalibrationPasses'],input[id*='syntheticCalibration'],input[id*='maxCalibrationSeconds']"));
        var path = Path.Combine(TestContext.TestResultsDirectory!, "calibration-options.html");
        File.WriteAllText(path, "<!doctype html><html lang='en' data-theme='hvo-dark'><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Calibration options fixture</title><body data-theme='hvo-dark'>" + cut.Markup + "</body></html>");
        TestContext.AddResultFile(path);
        cut.Find("#node-options-0-outputVariant").Change("corrected");
        cut.Find("form").Submit();
        cut.Find("button.btn-primary").Click();
        var options = service.Created.Single().Pipeline.Steps[0].Options!.Value;
        Assert.AreEqual("corrected", options.GetProperty("outputVariant").GetString());
        foreach (var member in source.EnumerateObject())
            Assert.AreEqual(member.Value.GetRawText(), options.GetProperty(member.Name).GetRawText(), member.Name);
        Assert.IsFalse(options.TryGetProperty("enabled", out _));
        Assert.IsEmpty(service.Commands);
        Assert.AreEqual(source.GetRawText(), service.RevisionDetail.Pipeline.Steps[0].Options!.Value.GetRawText());
    }

    [TestMethod]
    [DataRow("strategy", "Other")]
    [DataRow("strategy", "none")]
    [DataRow("outputVariant", " ")]
    public void InvalidCalibrationScalarInvalidatesPreview(string field, string value)
    {
        using var context = new BunitContext();
        var service = Configure(context, "Calibration", null);
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-outputVariant").Change("corrected");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-" + field).Change(value);
        cut.Find("form").Submit();
        Assert.IsNotEmpty(cut.FindAll(".option-errors li"));
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    [DataRow(false, "SyntheticReferences")]
    [DataRow(true, "None")]
    [DataRow(true, "CalibrationLibrary")]
    public void CalibrationModelConsistencyFailureCannotSaveAPreviousValidPreview(bool hasModel, string strategy)
    {
        using var context = new BunitContext();
        var service = Configure(context, "Calibration", hasModel ? """{"strategy":"SyntheticReferences","syntheticCalibration":{}}""" : null);
        service.Preview = pipeline =>
        {
            var options = pipeline.Steps[0].Options!.Value.Deserialize<CalibrationProcessingStepOptions>(CalibrationOptionsJson)!;
            var errors = new List<ValidationResult>();
            return Validator.TryValidateObject(options, new ValidationContext(options), errors, true)
                ? OperatorUiResult<CaptureProcessingPlanPreview>.Success(new(pipeline.SchemaVersion, pipeline.DependencyPolicy, new string('D', 64), new string('E', 64), [], []))
                : OperatorUiResult<CaptureProcessingPlanPreview>.Failure(OperatorUiResultKind.Invalid, string.Join(' ', errors.Select(static error => error.ErrorMessage)));
        };
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-outputVariant").Change("corrected");
        cut.Find("form").Submit();
        Assert.IsFalse(cut.Find("button.btn-primary").HasAttribute("disabled"));
        cut.Find("#node-options-0-strategy").Change(strategy);
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }

    [TestMethod]
    public void RestoredCalibrationScalarsDoNotMaterializeAbsentDefaults()
    {
        using var context = new BunitContext();
        var service = Configure(context, "Calibration", """{ "future": [1e+02] }""");
        var cut = context.Render<ProcessingGraphEditorPage>();
        cut.Find("#node-options-0-strategy").Change("CalibrationLibrary");
        cut.Find("#node-options-0-strategy").Change("None");
        cut.Find("#node-options-0-outputVariant").Change("corrected");
        cut.Find("#node-options-0-outputVariant").Change("none");
        cut.Find("form").Submit();
        Assert.IsTrue(cut.Find("button.btn-primary").HasAttribute("disabled"));
        Assert.IsEmpty(service.Created);
    }
}
