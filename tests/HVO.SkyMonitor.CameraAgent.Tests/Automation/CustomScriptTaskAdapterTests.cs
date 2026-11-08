using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class CustomScriptTaskAdapterTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void AuthorizationIsAnExplicitStartupOptIn(bool enabled, bool authorized)
    {
        var adapter = new CustomScriptTaskAdapter(Options.Create(new CustomScriptOptions
        { Enabled = enabled, OwnerAuthorized = authorized, Scripts = [Script()] }));
        Assert.IsFalse(adapter.Describe().Available);
        Assert.IsEmpty(adapter.Describe().Targets);
        Assert.IsTrue(adapter.RunsOffSweep);
    }

    [TestMethod]
    [DataRow("content")]
    [DataRow("argument")]
    [DataRow("argument-kind")]
    [DataRow("timeout")]
    [DataRow("scratch")]
    [DataRow("output")]
    public void TargetPinsContentTypedArgumentsAndEveryExecutionLimit(string change)
    {
        var script = Script();
        var original = script.Target;
        switch (change)
        {
            case "content": script.Content = "exit 1"; break;
            case "argument": script.Arguments = [new(CustomScriptArgumentKind.Text, "another")]; break;
            case "argument-kind": script.Arguments = [new(CustomScriptArgumentKind.WholeNumber, "1")]; break;
            case "timeout": script.TimeoutSeconds++; break;
            case "scratch": script.ScratchBytes++; break;
            default: script.OutputBytes++; break;
        }
        Assert.AreNotEqual(original, script.Target);
        Assert.IsLessThanOrEqualTo(LocalAutomationContract.MaximumTargetLength, script.Target.Length);
        Assert.IsFalse(script.Target.Contains(script.Content, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(CustomScriptArgumentKind.Text, "$(exit 7); a 'quoted' argument", true)]
    [DataRow(CustomScriptArgumentKind.WholeNumber, "-12", true)]
    [DataRow(CustomScriptArgumentKind.WholeNumber, "01", false)]
    [DataRow(CustomScriptArgumentKind.WholeNumber, "1; exit 1", false)]
    [DataRow(CustomScriptArgumentKind.Boolean, "true", true)]
    [DataRow(CustomScriptArgumentKind.Boolean, "TRUE", false)]
    [DataRow(CustomScriptArgumentKind.Text, "line\nbreak", false)]
    [DataRow((CustomScriptArgumentKind)99, "value", false)]
    public void TypedArgumentsRejectMalformedValues(CustomScriptArgumentKind kind, string value, bool valid)
        => Assert.AreEqual(valid, new CustomScriptArgument(kind, value).IsValid());

    [TestMethod]
    [DataRow("id")]
    [DataRow("interpreter")]
    [DataRow("content")]
    [DataRow("nul")]
    [DataRow("arguments")]
    [DataRow("timeout")]
    [DataRow("scratch")]
    [DataRow("output")]
    public void InvalidRegistrationFailsClosed(string field)
    {
        var script = Script();
        switch (field)
        {
            case "id": script.Id = "/bin/sh"; break;
            case "interpreter": script.Interpreter = (CustomScriptInterpreter)99; break;
            case "content": script.Content = new string('a', 65_537); break;
            case "nul": script.Content = "a\0b"; break;
            case "arguments": script.Arguments = Enumerable.Repeat(new CustomScriptArgument(CustomScriptArgumentKind.Text, "x"), 17).ToList(); break;
            case "timeout": script.TimeoutSeconds = 301; break;
            case "scratch": script.ScratchBytes = 8_388_609; break;
            default: script.OutputBytes = 1_048_577; break;
        }
        Assert.IsFalse(script.IsValid());
        var adapter = Adapter(script);
        Assert.IsFalse(adapter.Describe().Available);
        Assert.IsEmpty(adapter.Describe().Targets);
    }

    [TestMethod]
    public void DuplicateEmptyAndMalformedStartupRegistrationsAreUnavailable()
    {
        foreach (var scripts in new[] { Array.Empty<CustomScriptRegistration>(), new[] { Script(), Script() } })
        {
            var adapter = new CustomScriptTaskAdapter(Options.Create(new CustomScriptOptions
            { Enabled = true, OwnerAuthorized = true, Scripts = [.. scripts] }));
            Assert.IsFalse(adapter.Describe().Available);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Enabled"] = "true",
            ["OwnerAuthorized"] = "true",
            ["Scripts:0:Id"] = "bound-script",
            ["Scripts:0:Content"] = "exit 0",
            ["Scripts:0:Interpreter"] = "synthetic-sensitive-invalid-value"
        }).Build();
        var services = new ServiceCollection();
        services.AddOptions<CustomScriptOptions>().Bind(configuration);
        using var provider = services.BuildServiceProvider();
        var malformed = new CustomScriptTaskAdapter(provider.GetRequiredService<IOptions<CustomScriptOptions>>());
        Assert.IsFalse(malformed.Describe().Available);
        Assert.IsEmpty(malformed.Describe().Targets);
        Assert.IsFalse(malformed.Describe().UnavailableReason!.Contains("synthetic-sensitive", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LaunchUsesOnlyTheUserBusLocatorAndLiteralArgvWithMandatoryIsolation()
    {
        var script = Script();
        script.Arguments = [new(CustomScriptArgumentKind.Text, "$(printf injected); 'a b'")];
        var info = LinuxCustomScriptSandbox.BuildStartInfo(script);
        Assert.HasCount(1, info.Environment);
        Assert.IsTrue(info.Environment.ContainsKey("XDG_RUNTIME_DIR"));
        Assert.IsFalse(info.UseShellExecute);
        Assert.AreEqual(script.Arguments[0].Value, info.ArgumentList[^1]);
        Assert.Contains("--unshare-all", info.ArgumentList);
        Assert.Contains("--die-with-parent", info.ArgumentList);
        Assert.Contains("--property=TasksMax=16", info.ArgumentList);
        Assert.Contains("--property=MemoryMax=67108864", info.ArgumentList);
        Assert.Contains("--property=CPUQuota=50%", info.ArgumentList);
        Assert.Contains("--property=MemorySwapMax=0", info.ArgumentList);
        Assert.DoesNotContain("--share-net", info.ArgumentList);
        Assert.Contains("--disable-userns", info.ArgumentList);
        Assert.Contains("--assert-userns-disabled", info.ArgumentList);
        Assert.Contains("--seccomp", info.ArgumentList);
        Assert.DoesNotContain("/lib64/ld-linux-x86-64.so.2", info.ArgumentList);
        Assert.IsTrue(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
    }

    [TestMethod]
    public void ScratchBudgetRoundsDownAndSubpageRegistrationsFailClosed()
    {
        var script = Script();
        script.ScratchBytes = Environment.SystemPageSize * 2 + 1;
        Assert.IsTrue(script.IsValid());
        var arguments = LinuxCustomScriptSandbox.BuildStartInfo(script).ArgumentList.ToList();
        Assert.AreEqual((Environment.SystemPageSize * 2).ToString(System.Globalization.CultureInfo.InvariantCulture),
            arguments[arguments.IndexOf("--size") + 1]);

        script.ScratchBytes = Environment.SystemPageSize - 1;
        Assert.IsFalse(script.IsValid());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LinuxCustomScriptSandbox.BuildStartInfo(script));
    }

    [TestMethod]
    public void DisabledAdapterCannotValidateAnArbitraryCommandTarget()
    {
        var adapter = new CustomScriptTaskAdapter(Options.Create(new CustomScriptOptions()));
        Assert.IsNotNull(adapter.Validate(Definition("sh -c arbitrary")));
    }

    [TestMethod]
    public void StartupBindingPreservesTypedRegistrationsWithoutPublishingContent()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Enabled"] = "true",
            ["OwnerAuthorized"] = "true",
            ["Scripts:0:Id"] = "bound-script",
            ["Scripts:0:Content"] = "exit 0",
            ["Scripts:0:Interpreter"] = "PosixShell",
            ["Scripts:0:Arguments:0:Kind"] = "WholeNumber",
            ["Scripts:0:Arguments:0:Value"] = "12"
        }).Build();
        var options = configuration.Get<CustomScriptOptions>()!;
        Assert.IsTrue(options.Enabled && options.OwnerAuthorized);
        var script = options.Scripts.Single();
        Assert.IsTrue(script.IsValid());
        Assert.AreEqual(CustomScriptArgumentKind.WholeNumber, script.Arguments.Single().Kind);
    }

    [TestMethod]
    [DataRow("rw,nsdelegate,memory_recursiveprot", true)]
    [DataRow("rw,memory_recursiveprot", false)]
    [DataRow("rw,nsdelegate-suffix", false)]
    [DataRow("rw", false)]
    public void AvailabilityRejectsCgroupContextsWithoutNamespaceRootProtection(string flags, bool expected)
        => Assert.AreEqual(expected, LinuxCustomScriptSandbox.HasProtectedCgroupMount(
            "33 24 0:28 / /sys/fs/cgroup rw,nosuid,nodev,noexec - cgroup2 cgroup2 " + flags));

    internal static CustomScriptRegistration Script(string content = "exit 0") => new()
    { Id = "operator-test", Content = content, Arguments = [new(CustomScriptArgumentKind.Text, "1")] };

    internal static CustomScriptTaskAdapter Adapter(CustomScriptRegistration script) => new(Options.Create(new CustomScriptOptions
    { Enabled = true, OwnerAuthorized = true, Scripts = [script] }));

    internal static LocalAutomationDefinition Definition(string target) => new("script-task", "Operator script", true,
        LocalAutomationTaskKind.CustomScript, target, LocalAutomationTriggerKind.Periodic, 60, DateTimeOffset.UnixEpoch);

    internal static LocalAutomationOccurrence Occurrence(CustomScriptRegistration script) =>
        LocalAutomationOccurrence.Create(new(Definition(script.Target), 1,
            LocalAutomationContract.ComputeRevisionSha256(Definition(script.Target)), null, null),
            "script-test-occurrence", DateTimeOffset.UnixEpoch);
}
