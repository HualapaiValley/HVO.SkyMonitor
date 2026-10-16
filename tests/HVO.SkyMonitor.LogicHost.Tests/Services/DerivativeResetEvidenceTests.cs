using System.Xml;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;

namespace HVO.SkyMonitor.LogicHost.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DerivativeResetEvidenceTests
{
    [TestMethod]
    public async Task SamplerStopsAtNinetySecondsWithoutCatchUpBursts()
    {
        var clock = new ManualClock();
        var samples = new List<double>();
        await DerivativeResetEvidence.SampleAsync(_ =>
        {
            samples.Add(clock.Seconds);
            clock.Seconds += 0.25;
            return Task.CompletedTask;
        }, () => false, clock, CancellationToken.None, (interval, _) =>
        {
            Assert.IsGreaterThanOrEqualTo(1, interval.TotalSeconds);
            clock.Seconds += interval.TotalSeconds;
            return Task.CompletedTask;
        });
        Assert.IsLessThanOrEqualTo(90, samples.Count);
        Assert.IsLessThan(90, samples[^1]);
        Assert.IsTrue(samples.Zip(samples.Skip(1)).All(pair => pair.Second - pair.First >= 1));
    }

    [TestMethod]
    public async Task SampleCountRemainsBoundedEvenWhenClockDoesNotAdvance()
    {
        var count = 0;
        await DerivativeResetEvidence.SampleAsync(_ => { count++; return Task.CompletedTask; }, () => false,
            new ManualClock(), CancellationToken.None, (_, _) => Task.CompletedTask);
        Assert.AreEqual(90, count);
    }

    [TestMethod]
    public async Task CompletedCommandStopsAfterOneFinalSampleAndCancellationStopsBeforeSampling()
    {
        var count = 0;
        await DerivativeResetEvidence.SampleAsync(_ => { count++; return Task.CompletedTask; }, () => true,
            new ManualClock(), CancellationToken.None);
        Assert.AreEqual(1, count);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => DerivativeResetEvidence.SampleAsync(
            _ => throw new AssertFailedException("must not sample"), () => false, new ManualClock(), canceled.Token));
    }

    [TestMethod]
    public void PlanProjectionDropsSqlCredentialsNamesLiteralsAndUnknownAttributes()
    {
        const string secret = "private-principal-and-payload";
        var plan = DerivativeResetEvidence.ProjectPlan($"""
            <ShowPlanXML><StmtSimple StatementText="{secret}"><RelOp NodeId="1" PhysicalOp="Nested Loops" EstimateRows="4" Unknown="{secret}">
            <Object Database="{secret}" Table="{secret}"/><ScalarOperator ScalarString="{secret}"/>
            <RelOp NodeId="2" PhysicalOp="{secret}" EstimateRows="{secret}"/></RelOp></StmtSimple></ShowPlanXML>
            """).ToString();
        Assert.IsFalse(plan.Contains(secret, StringComparison.Ordinal));
        StringAssert.Contains(plan, "Nested Loops");
        StringAssert.Contains(plan, "Other");
        StringAssert.Contains(plan, "actualRuntimeStatistics=\"false\"");
        Assert.ThrowsExactly<XmlException>(() => DerivativeResetEvidence.ProjectPlan("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>"));
    }

    [TestMethod]
    public void ResourceProjectionDropsNamesAndUnboundedText()
    {
        var counters = DerivativeResetEvidence.NumericLines("cpu 1 2 3\nMemAvailable: 1024 kB\nprivate-user 100\nmemory.current 42\nusage_usec password=secret\n8:0 rbytes=123 wbytes=456 rios=1 wios=2\n" + new string('x', 1024));
        StringAssert.Contains(counters, "cpu 1 2 3");
        StringAssert.Contains(counters, "memory.current 42");
        StringAssert.Contains(counters, "rbytes=123");
        Assert.IsFalse(counters.Contains("private", StringComparison.Ordinal));
        Assert.IsFalse(counters.Contains("secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OutputLimitLeavesNoPartialPublishedEvidence()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => P5TrialDiagnostics.WriteAsync(directory, "derivative-reset.json",
                new { value = new string('x', DerivativeResetEvidence.MaximumBytes + 1) }, DerivativeResetEvidence.MaximumBytes));
            Assert.IsEmpty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    private sealed class ManualClock : TimeProvider
    {
        internal double Seconds { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => (long)(Seconds * TimeSpan.TicksPerSecond);
    }
}
