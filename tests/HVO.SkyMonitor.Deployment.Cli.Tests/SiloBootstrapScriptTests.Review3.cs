using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The #1966 third review's two Sol High cases, run as bash runs them against the fake <c>mcli</c>, which logs every call.
/// The probe proves CameraAgent's live endpoint exactly (bytes, scheme, host and port) and its settings mount's identity
/// before it reads a credential or sends anything. Adoption reads the store's machine-readable answers whole and adopts
/// or removes nothing unless every row is exactly this instance's own, with exactly the least-privilege writer policy.
/// </summary>
public sealed partial class SiloBootstrapScriptTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("exact", true)]
    [DataRow("other-host", false)]
    [DataRow("other-port", false)]
    [DataRow("other-scheme", false)]
    [DataRow("trailing-newline", false)]
    [DataRow("symlinked-endpoint", false)]
    [DataRow("replaced-endpoint-file", false)]
    [DataRow("settings-directory-elsewhere", false)]
    [DataRow("symlinked-secret", false)]
    public async Task Probe_ProvesTheExactLiveEndpointAndMountBeforeAnyCredentialReadOrNetworkCall(string change, bool verified)
    {
        using var run = new FakeStore(TestContext);
        run.Bucket(Bucket, "Enabled");
        var marker = "{\"format\":\"marker\"}"u8.ToArray();
        var fence = "{\"format\":\"fence\"}"u8.ToArray();
        run.StoredObject(Bucket, SiloRuntimeProfile.MarkerKey, marker);
        run.StoredObject(Bucket, SiloRuntimeProfile.FenceKey, fence);
        var settings = run.Path("settings");
        Directory.CreateDirectory(settings);
        void Setting(string directory, string name, string value) => File.WriteAllText(Path.Combine(directory, name), value);
        Setting(settings, "CameraAgent__Silo__Endpoint", SiloRuntimeProfile.Endpoint);
        Setting(settings, SiloRuntimeProfile.WriterAccessKeySetting, "AKIAEXAMPLEWRITER001");
        Setting(settings, SiloRuntimeProfile.WriterSecretKeySetting, "exampleWriterSecret0123456789ABCDEFGHIJ");
        // What the host expects is taken before any change, as the installer takes it from its own records.
        var expectation = Expectation(settings);
        var mounted = settings;
        switch (change)
        {
            case "other-host": Setting(settings, "CameraAgent__Silo__Endpoint", "http://other-silo:9000"); break;
            case "other-port": Setting(settings, "CameraAgent__Silo__Endpoint", "http://hvo-silo:9001"); break;
            case "other-scheme": Setting(settings, "CameraAgent__Silo__Endpoint", "https://hvo-silo:9000"); break;
            case "trailing-newline": Setting(settings, "CameraAgent__Silo__Endpoint", SiloRuntimeProfile.Endpoint + "\n"); break;
            case "symlinked-endpoint":
                Setting(run.Root, "endpoint-elsewhere", SiloRuntimeProfile.Endpoint);
                File.Delete(Path.Combine(settings, "CameraAgent__Silo__Endpoint"));
                File.CreateSymbolicLink(Path.Combine(settings, "CameraAgent__Silo__Endpoint"), Path.Combine(run.Root, "endpoint-elsewhere"));
                break;
            case "replaced-endpoint-file":
                Setting(settings, ".endpoint.new", SiloRuntimeProfile.Endpoint);
                File.Move(Path.Combine(settings, ".endpoint.new"), Path.Combine(settings, "CameraAgent__Silo__Endpoint"), overwrite: true);
                break;
            case "settings-directory-elsewhere":
                mounted = run.Path("other-settings");
                Directory.CreateDirectory(mounted);
                foreach (var file in Directory.GetFiles(settings)) File.Copy(file, Path.Combine(mounted, Path.GetFileName(file)));
                break;
            case "symlinked-secret":
                Setting(run.Root, "secret-elsewhere", "exampleWriterSecret0123456789ABCDEFGHIJ");
                File.Delete(Path.Combine(settings, SiloRuntimeProfile.WriterSecretKeySetting));
                File.CreateSymbolicLink(Path.Combine(settings, SiloRuntimeProfile.WriterSecretKeySetting), Path.Combine(run.Root, "secret-elsewhere"));
                break;
        }

        var (exit, output) = await run.ExecuteAsync(ProbeScript(expectation), mounted);

        if (verified)
        {
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(), "\"outcome\":\"verified\"", StringComparison.Ordinal);
        }
        else
        {
            Assert.AreEqual(3, exit, output);
            StringAssert.StartsWith(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last(), "{\"outcome\":\"refused\"", StringComparison.Ordinal);
            Assert.IsEmpty(run.Calls, "Nothing is sent, authenticated or not, before the live endpoint and mount are proved: " + string.Join("; ", run.Calls));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    // RED reproductions: something that is not this instance's own hides from a name or pattern heuristic.
    [DataRow("hyphenated-foreign-key", false)]
    [DataRow("own-writer-with-broader-grants", false)]
    [DataRow("own-writer-with-an-extra-statement", false)]
    [DataRow("own-writer-of-another-parent", false)]
    // Owned controls: exactly this instance's own attempt is adopted.
    [DataRow("own-writer-with-the-exact-policy", true)]
    [DataRow("empty-store", true)]
    [DataRow("own-empty-bucket-only", true)]
    [DataRow("own-writer-whose-policy-answer-reorders-per-call", true)]
    public async Task Adopt_RemovesOrAdoptsOnlyRowsProvedExactlyOwnFromMachineReadableAnswers(string store, bool adopted)
    {
        using var run = new FakeStore(TestContext);
        var exact = SiloRuntimeProfile.WriterPolicy(Bucket);
        if (store != "empty-store") run.Bucket(Bucket, "Suspended");
        switch (store)
        {
            case "hyphenated-foreign-key":
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket, exact);
                run.Key("foreign-key-0001", "operator", "someone else's", "other-bucket");
                break;
            case "own-writer-with-broader-grants":
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket,
                    $$"""{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:*"],"Resource":["arn:aws:s3:::*","arn:aws:s3:::{{Bucket}}/*"]}]}""");
                break;
            case "own-writer-with-an-extra-statement":
                var broader = JsonNode.Parse(exact)!.AsObject();
                broader["Statement"]!.AsArray().Add(new JsonObject
                {
                    ["Effect"] = "Allow",
                    ["Action"] = new JsonArray("s3:DeleteBucket"),
                    ["Resource"] = new JsonArray($"arn:aws:s3:::{Bucket}")
                });
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket, broader.ToJsonString());
                break;
            case "own-writer-of-another-parent":
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket, exact);
                break;
            case "own-writer-with-the-exact-policy":
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket, exact);
                break;
            case "own-writer-whose-policy-answer-reorders-per-call":
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket, exact);
                var reordered = JsonNode.Parse(exact)!.AsObject();
                foreach (var statement in reordered["Statement"]!.AsArray())
                    statement!["Action"] = new JsonArray([.. statement["Action"]!.AsArray().Select(static action => (JsonNode)JsonValue.Create((string)action!)).Reverse()]);
                reordered["Statement"] = new JsonArray([.. reordered["Statement"]!.AsArray().Select(static item => item!.DeepClone()).Reverse()]);
                run.ReorderedPolicy("OWNPRIORKEY000000001", reordered.ToJsonString());
                break;
        }

        var (refused, output) = await AdoptAsync(run, rootUser: store == "own-writer-of-another-parent" ? "someoneelse" : "hvoroot");

        if (adopted)
        {
            Assert.IsFalse(refused, output);
            CollectionAssert.Contains(run.MutationKinds(), "admin accesskey create", output);
            if (store is "own-writer-with-the-exact-policy" or "own-writer-whose-policy-answer-reorders-per-call")
                CollectionAssert.Contains(run.MutationKinds(), "admin accesskey rm root/ OWNPRIORKEY000000001");
        }
        else
        {
            Assert.IsTrue(refused, output);
            Assert.AreEqual(string.Empty, run.Mutations, "No versioning change, bucket creation, key removal or new writer before every row is proved own.");
        }
    }

    [TestMethod]
    [DataRow("bucket-row-not-json", "store-unreadable")]
    [DataRow("bucket-row-without-a-key", "store-unreadable")]
    [DataRow("duplicate-bucket-rows", "store-unreadable")]
    [DataRow("versioning-missing", "bucket-unreadable")]
    [DataRow("keys-not-a-list", "keys-unreadable")]
    [DataRow("key-row-without-implied-policy", "keys-unreadable")]
    [DataRow("key-inheriting-its-parents-policy", "store-has-foreign-access-key")]
    [DataRow("temporary-key", "store-has-foreign-access-key")]
    [DataRow("key-answers-missing", "keys-unreadable")]
    [DataRow("key-answer-for-another-key", "store-has-foreign-access-key")]
    public async Task Adopt_RefusesAnyAnswerItCannotReadWholeBeforeAnyChange(string answer, string reason)
    {
        const string Own = "OWNPRIORKEY000000001";
        var bucketRow = $"{{\"status\":\"success\",\"type\":\"folder\",\"key\":\"{Bucket}/\"}}";
        var buckets = answer switch
        {
            "bucket-row-not-json" => "not json",
            "bucket-row-without-a-key" => "{\"status\":\"success\",\"type\":\"folder\"}",
            "duplicate-bucket-rows" => bucketRow + "\n" + bucketRow,
            _ => bucketRow
        };
        var versioning = answer == "versioning-missing" ? string.Empty : "{\"status\":\"success\",\"versioning\":{\"status\":\"Enabled\"}}";
        var row = $"{{\"accessKey\":\"{Own}\",\"impliedPolicy\":false}}";
        var keys = answer switch
        {
            "keys-not-a-list" => $"{{\"status\":\"success\",\"svcaccs\":{row}}}",
            "key-row-without-implied-policy" => $"{{\"status\":\"success\",\"svcaccs\":[{{\"accessKey\":\"{Own}\"}}]}}",
            "key-inheriting-its-parents-policy" => $"{{\"status\":\"success\",\"svcaccs\":[{{\"accessKey\":\"{Own}\",\"impliedPolicy\":true}}]}}",
            "temporary-key" => $"{{\"status\":\"success\",\"stsKeys\":[{{\"accessKey\":\"TEMP\"}}],\"svcaccs\":[{row}]}}",
            _ => $"{{\"status\":\"success\",\"stsKeys\":null,\"svcaccs\":[{row}]}}"
        };
        var info = new JsonObject
        {
            ["status"] = "success",
            ["accessKey"] = answer == "key-answer-for-another-key" ? "OTHERPRIORKEY0000001" : Own,
            ["parentUser"] = "hvoroot",
            ["name"] = WriterName,
            ["description"] = WriterDescription,
            ["policy"] = JsonNode.Parse(SiloRuntimeProfile.WriterPolicy(Bucket))
        }.ToJsonString();
        static string B64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        var scripts = new List<string>();
        Task<ProcessResult> Execute(string script)
        {
            scripts.Add(script);
            if (script.Contains("hvo-bootstrap: inventory", StringComparison.Ordinal))
                return Task.FromResult(new ProcessResult(0,
                    $"{{\"outcome\":\"inventory\",\"buckets\":\"{B64(buckets)}\",\"objects\":\"\",\"versioning\":\"{B64(versioning)}\",\"keys\":\"{B64(keys)}\"}}\n", string.Empty));
            if (script.Contains("hvo-bootstrap: key-info", StringComparison.Ordinal))
                return Task.FromResult(new ProcessResult(0,
                    answer == "key-answers-missing" ? "{\"outcome\":\"key-info\",\"info\":[]}\n" : $"{{\"outcome\":\"key-info\",\"info\":[\"{B64(info)}\"]}}\n",
                    string.Empty));
            return Task.FromResult(new ProcessResult(0, "{\"outcome\":\"created\",\"accessKey\":\"NEWWRITERKEY00000001\",\"secretKey\":\"x\"}\n", string.Empty));
        }

        var refusal = await Assert.ThrowsAsync<InstallerException>(() =>
            SiloBootstrapStore.AdoptAsync(Execute, Bucket, SiloRuntimeProfile.WriterGeneration, "hvoroot", TestContext.CancellationToken));

        Assert.IsTrue(refusal.IsRefusal, refusal.Message);
        StringAssert.Contains(refusal.Message, reason, StringComparison.Ordinal);
        Assert.IsFalse(scripts.Any(static script => script.Contains("hvo-bootstrap: apply", StringComparison.Ordinal)), "No changing script is sent.");
    }

    [TestMethod]
    public void OwnWriter_JudgesTheEffectivePolicyIndependentlyOfTheOrderSiloAnswersIn()
    {
        var exact = JsonNode.Parse(SiloRuntimeProfile.WriterPolicy(Bucket))!.AsObject();
        var reordered = exact.DeepClone().AsObject();
        foreach (var statement in reordered["Statement"]!.AsArray())
            statement!["Action"] = new JsonArray([.. statement["Action"]!.AsArray().Select(static action => (JsonNode)JsonValue.Create((string)action!)).Reverse()]);
        reordered["Statement"] = new JsonArray([.. reordered["Statement"]!.AsArray().Select(static item => item!.DeepClone()).Reverse()]);
        string Info(JsonObject policy, string name = WriterName, string parent = "hvoroot") => new JsonObject
        {
            ["status"] = "success",
            ["accessKey"] = "OWNPRIORKEY000000001",
            ["parentUser"] = parent,
            ["accountStatus"] = "on",
            ["policy"] = policy.DeepClone(),
            ["name"] = name,
            ["description"] = WriterDescription
        }.ToJsonString();
        bool Own(string info) => SiloBootstrapStore.IsOwnWriter(info, "OWNPRIORKEY000000001", Bucket, SiloRuntimeProfile.WriterGeneration, "hvoroot");

        Assert.IsTrue(Own(Info(exact)));
        Assert.IsTrue(Own(Info(reordered)), "Silo's order of actions and statements does not matter.");
        var conditioned = exact.DeepClone().AsObject();
        conditioned["Statement"]![0]!["Condition"] = new JsonObject { ["Bool"] = new JsonObject { ["aws:SecureTransport"] = "true" } };
        Assert.IsFalse(Own(Info(conditioned)), "Anything this installer never writes is never adopted.");
        var widened = exact.DeepClone().AsObject();
        widened["Statement"]![1]!["Resource"] = new JsonArray($"arn:aws:s3:::{Bucket}/*", "arn:aws:s3:::*");
        Assert.IsFalse(Own(Info(widened)));
        Assert.IsFalse(Own(Info(exact, name: "hvo-writer-g2")));
        Assert.IsFalse(Own(Info(exact, parent: "someoneelse")));
    }

    /// <summary>The probe script for <paramref name="expectation"/>.</summary>
    private static string ProbeScript(SiloRuntimeProfile.SettingsExpectation expectation)
        => SiloRuntimeProfile.VerifyBindingScript(Bucket, MarkerVersionFor(), expectation);

    private static SiloRuntimeProfile.SettingsExpectation Expectation(string settings) => SiloRuntimeProfile.ExpectSettings(settings);

    private static string MarkerVersionFor() => "v-" + Convert.ToHexStringLower(SHA256.HashData("{\"format\":\"marker\"}"u8.ToArray()))[..12];

    /// <summary>Adoption as the installer runs it against this store: read-only inventory, decision, then the change.</summary>
    private async Task<(bool Refused, string Output)> AdoptAsync(FakeStore run, string rootUser)
    {
        try
        {
            var created = await SiloBootstrapStore.AdoptAsync(Execute(run), Bucket, SiloRuntimeProfile.WriterGeneration, rootUser,
                TestContext.CancellationToken);
            return (false, created.AccessKey);
        }
        catch (InstallerException exception) when (exception.IsRefusal)
        {
            return (true, exception.Message);
        }
    }
}
