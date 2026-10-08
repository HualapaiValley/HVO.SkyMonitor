using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SoftwarePageTests
{
    [TestMethod]
    public void Software_ShowsTheRunningVersionAndTheSelectedCatalog()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<SoftwarePage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("1.4.2", Fact(cut, "software-agent-heading", "Version"));
            Assert.AreEqual("0123456789ab", Fact(cut, "software-agent-heading", "Source revision"));
            Assert.AreEqual(".NET 10.0.0 / linux-x64", Fact(cut, "software-agent-heading", "Runtime"));
            Assert.AreEqual("2h 5m", Fact(cut, "software-agent-heading", "Uptime"));
            Assert.AreEqual("23 Jul 2026 10:00:00 +00:00 (UTC (site time zone unavailable))", Fact(cut, "software-agent-heading", "Started"));
            Assert.AreEqual("2026.07.1 / official", Fact(cut, "software-catalog-heading", "Package"));
            Assert.AreEqual("118,218", Fact(cut, "software-catalog-heading", "Star rows"));
            Assert.AreEqual("celestial-v2 (manifest 3)", Fact(cut, "software-catalog-heading", "Schema"));
            Assert.AreEqual("50.0 MiB", Fact(cut, "software-catalog-heading", "Database size"));
            var checksum = cut.Find("section[aria-labelledby='software-catalog-heading'] code");
            Assert.AreEqual("sha256:01234567…89ABCDEF", checksum.TextContent);
            Assert.AreEqual("sha256:" + TestSystemUiService.Software().Catalog.DatabaseSha256, checksum.GetAttribute("title"));
            StringAssert.Contains(cut.Find(".ops-status-rail").TextContent, "hyg-v41-openngc", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll(".software-packages tbody tr"));
            Assert.IsEmpty(cut.FindAll("[role='alert']"));
        });
    }

    [TestMethod]
    public void MissingRevisionAndFixturePackage_ReadPlainly()
    {
        using var context = new BunitContext();
        var system = Configure(context);
        system.SoftwareHandler = _ => ValueTask.FromResult(OperatorUiResult<SystemSoftwareView>.Success(
            TestSystemUiService.Software(revision: null, packageKind: "Fixture")));

        var cut = context.Render<SoftwarePage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Not recorded", Fact(cut, "software-agent-heading", "Source revision"));
            Assert.AreEqual("2026.07.1 / test fixture", Fact(cut, "software-catalog-heading", "Package"));
        });
    }

    [TestMethod]
    public void UnboundedActions_AreDisabledWithTheReason()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<SoftwarePage>();

        string[] reasonIds = ["software-availability-unavailable", "software-verify-unavailable"];
        cut.WaitForAssertion(() =>
        {
            foreach (var id in reasonIds)
            {
                var button = cut.Find($"button[aria-describedby='{id}']");
                Assert.IsTrue(button.HasAttribute("disabled"));
                Assert.AreEqual(cut.Find($"#{id}").TextContent, button.GetAttribute("title"));
            }
            StringAssert.Contains(cut.Find("#software-availability-unavailable").TextContent, "never contacts a package source", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#software-verify-unavailable").TextContent, "verified every time CameraAgent starts", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("button:not([disabled])"));
        });
    }

    [TestMethod]
    public void ReadFailure_ShowsAnAlertWithoutAnyFacts()
    {
        using var context = new BunitContext();
        var system = Configure(context);
        system.SoftwareHandler = _ => ValueTask.FromResult(OperatorUiResult<SystemSoftwareView>.Failure(
            OperatorUiResultKind.Unavailable, "The catalog snapshot was not validated."));

        var cut = context.Render<SoftwarePage>();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[role='alert']").TextContent;
            StringAssert.Contains(alert, "Software inventory unavailable.", StringComparison.Ordinal);
            StringAssert.Contains(alert, "The catalog snapshot was not validated.", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".ops-facts"));
        });
    }

    [TestMethod]
    public void Unauthorized_NavigatesToAccessDenied()
    {
        using var context = new BunitContext();
        var system = Configure(context);
        system.SoftwareHandler = _ => ValueTask.FromResult(OperatorUiResult<SystemSoftwareView>.Failure(
            OperatorUiResultKind.Unauthorized, "Denied."));

        var cut = context.Render<SoftwarePage>();

        var navigation = context.Services.GetRequiredService<NavigationManager>();
        Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll(".ops-facts"));
    }

    [TestMethod]
    [DataRow("0123456789abcdef0123456789abcdef", "sha256:01234567…89abcdef")]
    [DataRow("0123456789abcdef0123", "sha256:0123456789abcdef0123")]
    public void ShortChecksum_KeepsBothEnds(string sha256, string expected)
        => Assert.AreEqual(expected, SoftwarePage.ShortChecksum(sha256));

    [TestMethod]
    [DataRow("Production", "official")]
    [DataRow("Fixture", "test fixture")]
    [DataRow("Candidate", "Candidate")]
    public void PackageKindText_ReadsThePackageKind(string kind, string expected)
        => Assert.AreEqual(expected, SoftwarePage.PackageKindText(kind));

    private static TestSystemUiService Configure(BunitContext context)
    {
        var system = new TestSystemUiService();
        context.Services.AddSingleton<ICameraAgentSystemUiService>(system);
        return system;
    }

    private static string Fact(IRenderedComponent<SoftwarePage> cut, string headingId, string term)
        => cut.FindAll($"section[aria-labelledby='{headingId}'] .ops-facts > div")
            .Single(fact => fact.QuerySelector("dt")!.TextContent == term)
            .QuerySelector("dd")!.TextContent;
}
