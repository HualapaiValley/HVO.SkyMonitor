using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Data;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed partial class ObservingDayEvidenceIntegrationTests
{
    [TestMethod]
    public async Task EmptyArchivedPeriodRendersUnavailableLanesAndPreservesTheCalendarRangeAsync()
    {
        using var client = await CreateOwnerClientAsync().ConfigureAwait(false);
        using var response = await client.GetAsync(new Uri(
            "/archive/day/2000-01-02?calendar=" + ObservingDayCalendar.LegacyNoonVersion, UriKind.Relative)).ConfigureAwait(false);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "No archived session", StringComparison.Ordinal);
        StringAssert.Contains(html, "No captures were retained on this observing day", StringComparison.Ordinal);
        StringAssert.Contains(html, "No retained exposures", StringComparison.Ordinal);
        StringAssert.Contains(html, "Clear or usable intervals unavailable", StringComparison.Ordinal);
        StringAssert.Contains(html, "Cloudier intervals unavailable", StringComparison.Ordinal);
        StringAssert.Contains(html, "No candidate records", StringComparison.Ordinal);
        StringAssert.Contains(html, "/archive/day/2000-01-01?calendar=" + ObservingDayCalendar.LegacyNoonVersion, StringComparison.Ordinal);
        StringAssert.Contains(html, "/archive/day/2000-01-03?calendar=" + ObservingDayCalendar.LegacyNoonVersion, StringComparison.Ordinal);
        StringAssert.Contains(html, "/archive/calendar?month=2000-01&calendar=" + ObservingDayCalendar.LegacyNoonVersion, StringComparison.Ordinal);
        Assert.IsFalse(html.Contains("timeline-bar--captures", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("timeline-marker\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InvalidDayDoesNotRenderEvidenceOrLoseItsCalendarReturnAsync()
    {
        using var client = await CreateOwnerClientAsync().ConfigureAwait(false);
        using var response = await client.GetAsync(new Uri(
            "/archive/day/not-a-date?calendar=" + ObservingDayCalendar.LegacyNoonVersion, UriKind.Relative)).ConfigureAwait(false);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.Contains(html, "Observing day not recognised", StringComparison.Ordinal);
        StringAssert.Contains(html, "/archive/calendar?calendar=" + ObservingDayCalendar.LegacyNoonVersion, StringComparison.Ordinal);
        Assert.IsFalse(html.Contains("timeline-tracks", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("day-hero__image", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task VirtualCaptureDayUsesItsOwnRetainedPreviewAndExactCaptureDestinationAsync()
    {
        // Use the fixture's ordinary virtual acquisition and SQLite archive, without substituting a UI fixture.
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var archive = scope.ServiceProvider.GetRequiredService<ICameraAgentArchive>();
        var day = archive.ObservingDays.SelectVersion(ObservingDayCalendar.LegacyNoonVersion).Resolve(DateTimeOffset.UtcNow);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CameraAgentObservingDayDetail? detail;
        do
        {
            detail = await archive.GetObservingDayAsync(day.Date, day.CalendarVersion, deadline.Token).ConfigureAwait(false);
            if (detail?.Day.RepresentativeCaptureId is not null) break;
            await Task.Delay(100, deadline.Token).ConfigureAwait(false);
        } while (true);
        Assert.IsNotNull(detail.Day.RepresentativeExposureUtc);
        Assert.IsTrue(day.Contains(detail.Day.RepresentativeExposureUtc.Value));

        using var client = await CreateOwnerClientAsync().ConfigureAwait(false);
        using var response = await client.GetAsync(new Uri(
            "/archive/day/" + day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "?calendar=" + day.CalendarVersion,
            UriKind.Relative)).ConfigureAwait(false);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var match = RepresentativeLink().Match(html);
        Assert.IsTrue(match.Success, "The acquired virtual day must expose its retained representative capture.");
        var captureId = Guid.Parse(match.Groups[1].Value);
        StringAssert.Contains(html, $"/api/v1/operations/gallery/{captureId:D}/thumbnail", StringComparison.Ordinal);
        StringAssert.Contains(html, "Newest capture with a published preview", StringComparison.Ordinal);
        StringAssert.Contains(html, "timeline-bar--captures", StringComparison.Ordinal);

        using var preview = await client.GetAsync(new Uri($"/api/v1/operations/gallery/{captureId:D}/thumbnail", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Found, preview.StatusCode);
        Assert.IsNotNull(preview.Headers.Location);
        using var image = await client.GetAsync(preview.Headers.Location).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, image.StatusCode);
        Assert.IsTrue(image.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.Ordinal));
        using var capture = await client.GetAsync(new Uri($"/gallery/{captureId:D}", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, capture.StatusCode);
        using var retained = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var gallery = retained.ServiceProvider.GetRequiredService<ICameraAgentGallery>();
        var record = await gallery.GetCaptureAsync(captureId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(record);
        Assert.IsTrue(day.Contains(record.ExposureStartedUtc));
        Assert.IsTrue(record.Artifacts.Any(artifact => preview.Headers.Location.OriginalString.Contains(
            artifact.ArtifactId.ToString("D"), StringComparison.Ordinal)), "The preview must belong to that exact retained capture.");
    }

    private static async Task<HttpClient> CreateOwnerClientAsync()
    {
        using var scope = AssemblyHooks.Fixture.CreateCameraAgentScope();
        var owner = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync("owner@cameraagent.integration").ConfigureAwait(false);
        Assert.IsNotNull(owner);
        var client = AssemblyHooks.Fixture.CreateCameraAgentClient();
        client.DefaultRequestHeaders.Add(IntegrationUserAuthenticationHandler.UserIdHeader, owner.Id);
        return client;
    }

    [GeneratedRegex("href=\"/gallery/([0-9a-f-]{36})\" aria-label=\"Open the representative capture\"")]
    private static partial Regex RepresentativeLink();
}
