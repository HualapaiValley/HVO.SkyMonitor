using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

internal static class CentralWorkspaceEventHistory
{
    internal static CentralTransientEventDetail Create(Guid id)
    {
        var value = TransientContractJson.ParseEvent(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures/transient-event-v1.json"))).Value!;
        var assessment = value.Assessments[^1];
        var reviews = new List<TransientReviewV1>();
        var notifications = new List<TransientNotificationV1>();
        for (var index = 0; index < 1_000; index++)
        {
            var now = value.VersionCreatedUtc.AddSeconds(index + 1);
            reviews.Add(new(Guid.NewGuid(), now, "redacted", TransientReviewDisposition.Confirmed, assessment.AssessmentId,
                null, index == 999 ? Enumerable.Range(0, 70).Select(i => $"review.reason-{i}").ToArray() : ["review.confirmed"],
                index == 0 ? null : reviews[^1].ReviewId));
            notifications.Add(new(Guid.NewGuid(), now, "email", TransientNotificationState.Sent, assessment.AssessmentId,
                null, index == 0 ? null : notifications[^1].NotificationId));
        }
        value = value with
        {
            VersionCreatedUtc = reviews[^1].CreatedUtc,
            Reviews = reviews,
            Notifications = notifications,
            Observations = value.Observations.Select(o => o with
            {
                Geometry = o.Geometry with { Polyline = Enumerable.Range(0, 70).Select(i => o.Geometry.Polyline[i % o.Geometry.Polyline.Count]).ToArray() }
            }).ToArray(),
            Assessments = value.Assessments.Select(a => a with
            {
                Reasons = Enumerable.Range(0, 70).Select(i => a.Reasons[0] with { Code = $"assessment.reason-{i}" }).ToArray()
            }).ToArray()
        };
        TransientContractJson.Serialize(value).Should().NotBeEmpty("the large history must be accepted by the real canonical contract");
        return new(new(id, value.EventVersionId, value.EventId, value.AgentId, value.EventCreatedUtc,
            value.FirstObservedUtc, value.LastObservedUtc, value.Version, value.State, CentralTransientReviewState.Reviewed,
            assessment.AssessmentId, assessment.Classification, assessment.MeteorSeverity, assessment.ConfidenceMillionths, "\"AAAAAAAAAAE=\""), value);
    }

    internal static void AssertBounds(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            element.GetArrayLength().Should().BeLessThanOrEqualTo(25);
            foreach (var item in element.EnumerateArray()) AssertBounds(item);
        }
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) AssertBounds(property.Value);
    }
}
