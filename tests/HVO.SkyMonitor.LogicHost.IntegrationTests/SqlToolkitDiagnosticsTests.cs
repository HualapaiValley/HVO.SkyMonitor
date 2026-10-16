using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Unit")]
public sealed class SqlToolkitDiagnosticsTests
{
    private const string Secret = "Password=secret-canary;User ID=private-login;/private/run-owned/path;SELECT private_payload";

    [TestMethod]
    [DataRow((int)ToolkitStage.BlockedThresholdRestoration)]
    [DataRow((int)ToolkitStage.AdvancedOptionsVerification)]
    [DataRow((int)ToolkitStage.PrincipalSessionCleanup)]
    [DataRow((int)ToolkitStage.ExactFileCleanup)]
    [DataRow((int)ToolkitStage.ExactFileVerification)]
    [DataRow((int)ToolkitStage.ContainerDisposal)]
    public async Task FaultRetainsExactStageAndRunsLaterCleanup(int stageValue)
    {
        var stage = (ToolkitStage)stageValue;
        var diagnostics = new SqlToolkitDiagnostics();
        diagnostics.CompleteBody();
        await diagnostics.AttemptAsync(stage, () => Task.FromException(new IOException(Secret))).ConfigureAwait(false);
        var laterRan = false;
        await diagnostics.AttemptAsync(ToolkitStage.ContainerDisposal, () =>
        {
            laterRan = true;
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        laterRan.Should().BeTrue();
        var fact = ReadFacts(diagnostics).Single();
        fact.GetProperty("Stage").GetString().Should().Be(stage.ToString());
        fact.GetProperty("Category").GetString().Should().Be("io");
        var failure = Assert.ThrowsExactly<AssertFailedException>(diagnostics.ThrowIfFailed);
        failure.InnerException.Should().BeNull();
        AssertNoSecrets(failure.ToString());
        failure.Message.Should().Contain("body-complete");
    }

    [TestMethod]
    public async Task BodyAndMultipleCleanupAndDisposalFailuresAllRemainFailed()
    {
        var diagnostics = new SqlToolkitDiagnostics();
        await diagnostics.AttemptAsync(ToolkitStage.Body, () => Task.FromException(new AssertFailedException(Secret))).ConfigureAwait(false);
        await diagnostics.AttemptAsync(ToolkitStage.BlockedThresholdVerification, () => Task.FromException(new InvalidOperationException(Secret))).ConfigureAwait(false);
        await diagnostics.AttemptAsync(ToolkitStage.ExactFileCleanup, () => Task.FromException(new UnauthorizedAccessException(Secret))).ConfigureAwait(false);
        await diagnostics.AttemptAsync(ToolkitStage.ContainerDisposal, () => Task.FromException(new TimeoutException(Secret))).ConfigureAwait(false);
        ReadFacts(diagnostics).Select(fact => fact.GetProperty("Category").GetString())
            .Should().Equal("assertion", "invalid-operation", "access", "timeout");
        diagnostics.Serialize().Should().Contain("body-incomplete").And.NotContain(Secret);
        Assert.ThrowsExactly<AssertFailedException>(diagnostics.ThrowIfFailed).InnerException.Should().BeNull();
    }

    [TestMethod]
    public void AggregateAndInnerSqlNumbersSurviveWithoutRawExceptionDetails()
    {
        var diagnostics = new SqlToolkitDiagnostics { Stage = ToolkitStage.MigrationLoginCleanup };
        diagnostics.Record(new AggregateException(Secret,
            new InvalidOperationException(Secret, CreateSqlException(1205)), new SanitizedScriptException(50001)));
        var facts = ReadFacts(diagnostics);
        facts.Select(fact => fact.GetProperty("Category").GetString())
            .Should().Equal("aggregate", "invalid-operation", "sql", "sql-script");
        facts.Select(fact => fact.GetProperty("SqlErrorNumber").GetInt32()).Should().Equal(0, 0, 1205, 50001);
        facts.Should().OnlyContain(fact => fact.GetProperty("Stage").GetString() == "MigrationLoginCleanup");
        AssertNoSecrets(diagnostics.Serialize());
        diagnostics.Serialize().Should().NotContain("SqlException");
    }

    [TestMethod]
    public void AggregateTraversalAndRepeatedFailuresAreBounded()
    {
        var diagnostics = new SqlToolkitDiagnostics();
        diagnostics.Record(new AggregateException(Secret, Enumerable.Range(0, 100).Select(_ => new SecretCanaryException(Secret))));
        diagnostics.Record(new SecretCanaryException(Secret));
        ReadFacts(diagnostics).Length.Should().Be(SqlToolkitDiagnostics.MaximumFacts);
        diagnostics.Serialize().Should().Contain("\"Truncated\":true").And.NotContain(Secret);
        diagnostics.Serialize().Length.Should().BeLessThan(6000);
    }

    [TestMethod]
    public void DeepInnerChainIsBoundedAndUnknownTypesUseClosedCategory()
    {
        Exception exception = new SecretCanaryException(Secret);
        for (var index = 0; index < 100; index++) exception = new SecretCanaryException(Secret, exception);
        var diagnostics = new SqlToolkitDiagnostics { Stage = (ToolkitStage)int.MaxValue };
        diagnostics.Record(exception);
        ReadFacts(diagnostics).Length.Should().Be(SqlToolkitDiagnostics.MaximumFacts);
        ReadFacts(diagnostics).Should().OnlyContain(fact => fact.GetProperty("Stage").GetString() == "Body" &&
            fact.GetProperty("Category").GetString() == "other");
        diagnostics.Serialize().Should().NotContain(Secret).And.NotContain(nameof(SecretCanaryException));
    }

    [TestMethod]
    public void SuccessfulBodyIsExplicitAndDoesNotCertifyLifecycleUntilCleanupFinishes()
    {
        var diagnostics = new SqlToolkitDiagnostics();
        diagnostics.CompleteBody();
        diagnostics.ThrowIfFailed();
        ReadFacts(diagnostics).Should().BeEmpty();
        diagnostics.Stage.Should().Be(ToolkitStage.BodyComplete);
        diagnostics.Record(new ObjectDisposedException(Secret));
        Assert.ThrowsExactly<AssertFailedException>(diagnostics.ThrowIfFailed).Message.Should().Contain("body-complete");
    }

    [TestMethod]
    public async Task ObservedDisposalPreservesBodyFailureAndSanitizesDisposalFailure()
    {
        var diagnostics = new SqlToolkitDiagnostics { Stage = ToolkitStage.Body, BodyStep = ToolkitBodyStep.BackupRestore };
        diagnostics.Record(new SecretCanaryException(Secret));
        await using (diagnostics.ObserveDisposal(ToolkitStage.ContainerDisposal, new FailingDisposable())) { }
        var facts = ReadFacts(diagnostics);
        facts.Select(fact => fact.GetProperty("Stage").GetString()).Should().Equal("Body", "ContainerDisposal");
        facts[0].GetProperty("BodyStep").GetString().Should().Be("BackupRestore");
        facts[1].GetProperty("BodyStep").ValueKind.Should().Be(JsonValueKind.Null);
        Assert.ThrowsExactly<AssertFailedException>(diagnostics.ThrowIfFailed).ToString().Should().NotContain(Secret);
    }

    [TestMethod]
    public async Task LargeBodyAggregateCannotHideLaterCleanupOrDisposalFailure()
    {
        var diagnostics = new SqlToolkitDiagnostics { Stage = ToolkitStage.Body };
        diagnostics.Record(new AggregateException(Secret, Enumerable.Range(0, 100).Select(_ => new SecretCanaryException(Secret))));
        await diagnostics.AttemptAsync(ToolkitStage.ExactFileCleanup, () => Task.FromException(new IOException(Secret))).ConfigureAwait(false);
        await using (diagnostics.ObserveDisposal(ToolkitStage.ContainerDisposal, new FailingDisposable())) { }
        ReadFacts(diagnostics).Length.Should().Be(SqlToolkitDiagnostics.MaximumFacts);
        ReadFacts(diagnostics).Select(fact => fact.GetProperty("Stage").GetString()).Should().Contain(["Body", "ExactFileCleanup", "ContainerDisposal"]);
        Enum.GetValues<ToolkitStage>().Length.Should().BeLessThan(SqlToolkitDiagnostics.MaximumFacts);
        AssertNoSecrets(Assert.ThrowsExactly<AssertFailedException>(diagnostics.ThrowIfFailed).ToString());
    }

    private sealed class FailingDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new IOException(Secret));
    }

    private static void AssertNoSecrets(string output)
    {
        foreach (var canary in new[] { "secret-canary", "private-login", "/private/run-owned/path", "SELECT private_payload", Secret })
            output.Should().NotContain(canary);
    }

    private static JsonElement[] ReadFacts(SqlToolkitDiagnostics diagnostics)
    {
        using var document = JsonDocument.Parse(diagnostics.Serialize());
        return document.RootElement.GetProperty("Failures").EnumerateArray().Select(fact => fact.Clone()).ToArray();
    }

    private static SqlException CreateSqlException(int number)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var constructor = typeof(SqlError).GetConstructors(flags)
            .Where(item => item.GetParameters().Length > 0 && item.GetParameters()[0].ParameterType == typeof(int))
            .OrderByDescending(item => item.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select((parameter, index) => index == 0
            ? number
            : parameter.ParameterType == typeof(string) ? Secret
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : null).ToArray();
        var error = constructor.Invoke(arguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", flags)!.Invoke(errors, [error]);
        return (SqlException)typeof(SqlException).GetMethod("CreateException", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(SqlErrorCollection), typeof(string)])!.Invoke(null, [errors, "16.0"])!;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors",
        Justification = "Canary-only unknown exception type; the diagnostic must not emit its name or message.")]
    private sealed class SecretCanaryException(string message, Exception? inner = null) : Exception(message, inner);
}
