using System.Diagnostics;
using System.Diagnostics.Metrics;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Http;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentOperatorTelemetryTests
{
    [TestMethod]
    public void ArtifactComparisonInheritsCurrentRequestTrace()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == CameraAgentOperatorTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var requestSource = new ActivitySource("CameraAgentOperatorTelemetryTests.Request");
        using var requestListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "CameraAgentOperatorTelemetryTests.Request",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(requestListener);
        using var request = requestSource.StartActivity("request");
        using var telemetry = new CameraAgentOperatorTelemetry();

        using var comparison = telemetry.StartArtifactComparison();

        Assert.IsNotNull(request);
        Assert.IsNotNull(comparison);
        Assert.AreEqual(request.TraceId, comparison.TraceId);
        Assert.AreEqual(request.SpanId, comparison.ParentSpanId);
    }

    [TestMethod]
    public void PlanMutationUsesBoundedActionForActivityAndMetric()
    {
        var measurements = new List<KeyValuePair<string, object?>[]>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CameraAgentOperatorTelemetry.InstrumentationName &&
                    instrument.Name == "camera_agent.processing.plan.mutations")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) => measurements.Add(tags.ToArray()));
        meterListener.Start();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == CameraAgentOperatorTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(activityListener);
        using var telemetry = new CameraAgentOperatorTelemetry();

        using var activity = telemetry.StartPlanMutation("rollback");
        telemetry.RecordPlanMutation("rollback", "failed");

        Assert.IsNotNull(activity);
        Assert.AreEqual("processing-plan.rollback", activity.OperationName);
        Assert.IsTrue(measurements.Single().Contains(new KeyValuePair<string, object?>("action", "rollback")));
        Assert.IsTrue(measurements.Single().Contains(new KeyValuePair<string, object?>("outcome", "failed")));
    }

    [TestMethod]
    public void MutationOutcomeUsesReturnedResultStatus()
    {
        Assert.AreEqual("applied", CameraAgentScheduleOperationsEndpoints.ClassifyMutationOutcome(Results.Ok()));
        Assert.AreEqual("failed", CameraAgentScheduleOperationsEndpoints.ClassifyMutationOutcome(
            Results.Problem(statusCode: StatusCodes.Status403Forbidden)));
        Assert.AreEqual("failed", CameraAgentScheduleOperationsEndpoints.ClassifyMutationOutcome(
            Results.Problem(statusCode: StatusCodes.Status404NotFound)));
        Assert.AreEqual("failed", CameraAgentScheduleOperationsEndpoints.ClassifyMutationOutcome(
            Results.Problem(statusCode: StatusCodes.Status409Conflict)));
        Assert.AreEqual("failed", CameraAgentScheduleOperationsEndpoints.ClassifyMutationOutcome(
            Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity)));
    }
}
