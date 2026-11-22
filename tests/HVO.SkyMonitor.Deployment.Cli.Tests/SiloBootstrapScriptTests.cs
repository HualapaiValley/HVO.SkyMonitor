using System.Diagnostics;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The Silo bootstrap's store scripts as bash runs them (#1966 review), against a fake <c>mcli</c> that serves a store's
/// buckets, objects, versioning and access keys and logs every mutating call. Every ownership and account check finishes
/// before any mutation: a mixed store (this instance's own state beside anything foreign) is refused with nothing changed,
/// while a lost acknowledgement or an interrupted creation is adopted and owned cleanup happens only after all checks.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed partial class SiloBootstrapScriptTests
{
    private const string Bucket = "hvo-0123456789abcdef0123456789abcdef";
    private const string WriterName = "hvo-writer-g1";
    private const string WriterDescription = "HVO CameraAgent writer generation 1";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    // Mixed stores: this instance's own state beside something foreign. Nothing may change.
    [DataRow("own-key-then-foreign-key", "store-has-foreign-access-key")]
    [DataRow("foreign-key-then-own-key", "store-has-foreign-access-key")]
    [DataRow("suspended-own-bucket-and-foreign-key", "store-has-foreign-access-key")]
    [DataRow("own-bucket-and-foreign-bucket", "store-holds-foreign-content")]
    [DataRow("own-bucket-with-an-object", "bucket-not-empty")]
    [DataRow("own-key-for-another-bucket", "store-has-foreign-access-key")]
    public async Task Adopt_RefusesAMixedStoreBeforeAnyMutation(string store, string reason)
    {
        using var run = new FakeStore(TestContext);
        var exact = SiloRuntimeProfile.WriterPolicy(Bucket);
        switch (store)
        {
            case "own-key-then-foreign-key":
                run.Bucket(Bucket, "Enabled");
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket, exact);
                run.Key("FOREIGNKEY0000000001", "operator", "someone else's", "other-bucket");
                break;
            case "foreign-key-then-own-key":
                run.Bucket(Bucket, "Enabled");
                run.Key("FOREIGNKEY0000000001", "operator", "someone else's", "other-bucket");
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket, exact);
                break;
            case "suspended-own-bucket-and-foreign-key":
                run.Bucket(Bucket, "Suspended");
                run.Key("FOREIGNKEY0000000001", "operator", "someone else's", "other-bucket");
                break;
            case "own-bucket-and-foreign-bucket":
                run.Bucket(Bucket, "Enabled");
                run.Bucket("someone-elses", "");
                break;
            case "own-bucket-with-an-object":
                run.Bucket(Bucket, "Enabled");
                run.Object(Bucket, "captures/one.fits");
                break;
            case "own-key-for-another-bucket":
                run.Bucket(Bucket, "Enabled");
                run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, "hvo-ffffffffffffffffffffffffffffffff",
                    SiloRuntimeProfile.WriterPolicy("hvo-ffffffffffffffffffffffffffffffff"));
                break;
        }

        var (refused, output) = await AdoptAsync(run, rootUser: "hvoroot");

        Assert.IsTrue(refused, output);
        StringAssert.Contains(output, reason, StringComparison.Ordinal);
        Assert.AreEqual(string.Empty, run.Mutations, "Nothing may change before every check has passed.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("lost-acknowledgement")]
    [DataRow("lost-acknowledgement-suspended")]
    [DataRow("interrupted-before-bucket")]
    public async Task Adopt_AdoptsOnlyThisInstancesOwnAttemptAndCleansUpAfterEveryCheck(string store)
    {
        using var run = new FakeStore(TestContext);
        if (store != "interrupted-before-bucket")
        {
            run.Bucket(Bucket, store == "lost-acknowledgement-suspended" ? "Suspended" : "Enabled");
            run.Key("OWNPRIORKEY000000001", WriterName, WriterDescription, Bucket, SiloRuntimeProfile.WriterPolicy(Bucket));
        }

        var (refused, output) = await AdoptAsync(run, rootUser: "hvoroot");

        Assert.IsFalse(refused, output);
        var expected = store switch
        {
            "interrupted-before-bucket" => new[] { $"mb --with-versioning root/{Bucket}", "admin accesskey create" },
            "lost-acknowledgement-suspended" => [$"version enable root/{Bucket}", "admin accesskey rm root/ OWNPRIORKEY000000001", "admin accesskey create"],
            _ => ["admin accesskey rm root/ OWNPRIORKEY000000001", "admin accesskey create"]
        };
        CollectionAssert.AreEqual(expected, run.MutationKinds());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("empty", null)]
    [DataRow("own-named-bucket", "store-not-empty")]
    [DataRow("foreign-bucket", "store-not-empty")]
    [DataRow("access-key", "store-has-access-keys")]
    [DataRow("hyphenated-access-key", "store-has-access-keys")]
    public async Task Preflight_ReadsTheStoreOnlyAndAdmitsOnlyAnEmptyOne(string store, string? reason)
    {
        using var run = new FakeStore(TestContext);
        switch (store)
        {
            case "own-named-bucket": run.Bucket(Bucket, ""); break;
            case "foreign-bucket": run.Bucket("someone-elses", ""); break;
            case "access-key": run.Key("FOREIGNKEY0000000001", "operator", "someone else's", "other-bucket"); break;
            case "hyphenated-access-key": run.Key("foreign-key-0001", "operator", "someone else's", "other-bucket"); break;
        }

        string? refusal = null;
        try
        {
            _ = await SiloBootstrapStore.ProveEmptyAsync(Execute(run), Bucket, TestContext.CancellationToken);
        }
        catch (InstallerException exception) when (exception.IsRefusal)
        {
            refusal = exception.Message;
        }

        if (reason is null) Assert.IsNull(refusal, refusal);
        else StringAssert.Contains(refusal, reason, StringComparison.Ordinal);
        Assert.AreEqual(string.Empty, run.Mutations, "The preflight never changes the store.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("unchanged", true)]
    [DataRow("key-added-between-inventory-and-change", false)]
    [DataRow("bucket-added-between-inventory-and-change", false)]
    public async Task Create_ActsOnlyWhileTheStoreIsExactlyTheApprovedInventory(string change, bool created)
    {
        using var run = new FakeStore(TestContext);
        var empty = await SiloBootstrapStore.ProveEmptyAsync(Execute(run), Bucket, TestContext.CancellationToken);
        if (change == "key-added-between-inventory-and-change") run.Key("FOREIGNKEY0000000001", "operator", "someone else's", "other-bucket");
        if (change == "bucket-added-between-inventory-and-change") run.Bucket("someone-elses", "");

        string? refusal = null;
        try
        {
            _ = await SiloBootstrapStore.CreateAsync(Execute(run), Bucket, SiloRuntimeProfile.WriterGeneration, empty, TestContext.CancellationToken);
        }
        catch (InstallerException exception) when (exception.IsRefusal)
        {
            refusal = exception.Message;
        }

        if (created)
        {
            Assert.IsNull(refusal, refusal);
            CollectionAssert.AreEqual(new[] { $"mb --with-versioning root/{Bucket}", "admin accesskey create" }, run.MutationKinds());
        }
        else
        {
            StringAssert.Contains(refusal, "store-changed", StringComparison.Ordinal);
            Assert.AreEqual(string.Empty, run.Mutations);
        }
    }

    private static Func<string, Task<ProcessResult>> Execute(FakeStore run) => async script =>
    {
        var (exit, output) = await run.ExecuteAsync(script);
        return new ProcessResult(exit, output, string.Empty);
    };

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("first-bind", 0, null)]
    [DataRow("re-bind-after-a-completed-binding", 0, null)]
    [DataRow("marker-already-holds-other-bytes", 3, "marker-conflict")]
    public async Task Bind_IsCreateOnlyAndIdempotentForTheExactBytesAndReportsAnyRefusalReadably(string store, int exitCode, string? reason)
    {
        using var run = new FakeStore(TestContext);
        run.Bucket(Bucket, "Enabled");
        var marker = "{\"format\":\"marker\"}"u8.ToArray();
        var fence = "{\"format\":\"fence\"}"u8.ToArray();
        if (store == "re-bind-after-a-completed-binding")
        {
            run.StoredObject(Bucket, SiloRuntimeProfile.MarkerKey, marker);
            run.StoredObject(Bucket, SiloRuntimeProfile.FenceKey, fence);
        }
        if (store == "marker-already-holds-other-bytes")
            run.StoredObject(Bucket, SiloRuntimeProfile.MarkerKey, "{\"format\":\"someone else's\"}"u8.ToArray());

        var (exit, output) = await run.ExecuteAsync(SiloRuntimeProfile.WriteBindingScript(
            Bucket, "AKIAEXAMPLEWRITER001", "exampleWriterSecret0123456789ABCDEFGHIJ", marker, fence));

        Assert.AreEqual(exitCode, exit, output);
        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
        if (reason is null)
        {
            StringAssert.StartsWith(last, "{\"outcome\":\"bound\"", StringComparison.Ordinal);
            StringAssert.Contains(last, $"\"markerSha256\":\"{SiloHarness.Sha(marker)}\"", StringComparison.Ordinal);
        }
        else
        {
            Assert.AreEqual($"{{\"outcome\":\"refused\",\"reason\":\"{reason}\"}}", last, "A refusal is the readable last line.");
        }
    }

    /// <summary>A fake Silo store and <c>mcli</c> on a private PATH; the root credential files sit in a private directory.</summary>
    internal sealed class FakeStore : IDisposable
    {
        private readonly TestContext context;
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("hvo-silo-script-");

        public FakeStore(TestContext context)
        {
            this.context = context;
            Directory.CreateDirectory(Path("state/objects"));
            Directory.CreateDirectory(Path("credentials"));
            Directory.CreateDirectory(Path("bin"));
            Directory.CreateDirectory(Path("tmp"));
            File.WriteAllText(Path("credentials/root-user"), "hvoroot");
            File.WriteAllText(Path("credentials/root-password"), "hvorootpassword");
            File.WriteAllText(Path("state/buckets"), string.Empty);
            File.WriteAllText(Path("state/keys"), string.Empty);
            File.WriteAllText(Path("state/mutations"), string.Empty);
            File.WriteAllText(Path("state/calls"), string.Empty);
            File.WriteAllText(Path("bin/mcli"), FakeMcli);
            File.SetUnixFileMode(Path("bin/mcli"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Mutations => File.ReadAllText(Path("state/mutations")).Trim();

        public string[] MutationKinds() => [.. File.ReadAllLines(Path("state/mutations")).Where(static line => line.Length != 0)
            .Select(static line => line.StartsWith("admin accesskey create", StringComparison.Ordinal) ? "admin accesskey create" : line)];

        public void Bucket(string name, string versioning)
        {
            File.AppendAllText(Path("state/buckets"), $"{name} {(versioning.Length == 0 ? "-" : versioning)}\n");
            File.WriteAllText(Path($"state/objects/{name}"), string.Empty);
        }

        public void Object(string bucket, string key) => File.AppendAllText(Path($"state/objects/{bucket}"), key + "\n");

        public void StoredObject(string bucket, string key, byte[] content)
        {
            var path = Path($"state/blobs/{bucket}/{key}");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public void Key(string accessKey, string name, string description, string bucket, string? policy = null)
            => File.AppendAllText(Path("state/keys"), $"{accessKey}|{name}|{description}|{bucket}|{policy}\n");

        /// <summary>The policy every key scoped to <paramref name="bucket"/> reports unless it names its own.</summary>
        public void BucketPolicy(string bucket, string policy) => File.WriteAllText(Path($"state/policy-{bucket}"), policy);

        /// <summary>Makes <paramref name="accessKey"/>'s answer alternate between its policy and this reordering of it.</summary>
        public void ReorderedPolicy(string accessKey, string policy) => File.WriteAllText(Path($"state/policy-order-{accessKey}"), policy);

        public string[] Calls => File.Exists(Path("state/calls")) ? File.ReadAllLines(Path("state/calls")) : [];

        public string Root => root.FullName;

        public async Task<(int Exit, string Output)> ExecuteAsync(string script, string? settingsDirectory = null)
        {
            var text = script.Replace("/run/hvo-silo/", Path("credentials") + "/", StringComparison.Ordinal)
                .Replace("/run/hvo-secrets", settingsDirectory ?? "/run/hvo-secrets", StringComparison.Ordinal)
                .Replace("mktemp -d /tmp/", $"mktemp -d {Path("tmp")}/", StringComparison.Ordinal);
            var start = new ProcessStartInfo("bash")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("-s");
            start.Environment["PATH"] = Path("bin") + ":/usr/bin:/bin";
            start.Environment["FAKE_STATE"] = Path("state");
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteAsync(text);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(context.CancellationToken);
            var error = await process.StandardError.ReadToEndAsync(context.CancellationToken);
            await process.WaitForExitAsync(context.CancellationToken);
            return (process.ExitCode, output + error);
        }

        public string Path(string relative) => System.IO.Path.Combine(root.FullName, relative);

        public void Dispose() => root.Delete(recursive: true);

        /// <summary>
        /// The subset of <c>mcli</c> the bootstrap scripts use. State: <c>buckets</c> holds "name versioning" lines,
        /// <c>objects/NAME</c> the bucket's object keys, <c>keys</c> "accessKey|name|description|bucket" lines; every
        /// mutating call is appended to <c>mutations</c>.
        /// </summary>
        private const string FakeMcli = """
            #!/usr/bin/env bash
            set -uo pipefail
            s="$FAKE_STATE"
            printf '%s\n' "$*" >> "$s/calls"
            mutate() { printf '%s\n' "$*" >> "$s/mutations"; }
            case "$*" in
              "ready "*) exit 0 ;;
              "ls root --json")
                while read -r name _; do [ -n "$name" ] && printf '{"status":"success","type":"folder","key":"%s/"}\n' "$name"; done < "$s/buckets"; exit 0 ;;
              "ls --versions --recursive root/"*" --json")
                b="${4#root/}"; while read -r key; do [ -n "$key" ] && printf '{"status":"success","key":"%s"}\n' "$key"; done < "$s/objects/$b"; exit 0 ;;
              "mb --with-versioning root/"*)
                b="${3#root/}"; grep -q "^$b " "$s/buckets" && exit 1; mutate "$*"; printf '%s Enabled\n' "$b" >> "$s/buckets"; : > "$s/objects/$b"; exit 0 ;;
              "version info root/"*" --json")
                b="${3#root/}"; v="$(awk -v b="$b" '$1 == b { print $2 }' "$s/buckets")"; [ "$v" = "-" ] && v=""
                printf '{"Op":"info","status":"success","versioning":{"status":"%s"}}\n' "$v"; exit 0 ;;
              "version enable root/"*)
                b="${3#root/}"; mutate "$*"; awk -v b="$b" '$1 == b { $2 = "Enabled" } { print }' "$s/buckets" > "$s/buckets.next" && mv "$s/buckets.next" "$s/buckets"; exit 0 ;;
              "admin accesskey ls root/ --json")
                list=""; while IFS='|' read -r k _ _ _ _; do [ -n "$k" ] && list="$list${list:+,}{\"parentUser\":\"\",\"accountStatus\":\"\",\"impliedPolicy\":false,\"accessKey\":\"$k\",\"expiration\":\"1970-01-01T00:00:00Z\"}"; done < "$s/keys"
                if [ -n "$list" ]; then printf '{"status":"success","user":"hvoroot","stsKeys":null,"svcaccs":[%s]}\n' "$list"
                else printf '{"status":"success","user":"hvoroot","stsKeys":null,"svcaccs":null}\n'; fi; exit 0 ;;
              "admin accesskey info root/ "*" --json")
                line="$(grep -F "$5|" "$s/keys" | head -1)" || exit 1; IFS='|' read -r k n d b policy <<< "$line"
                [ -n "$policy" ] || policy="$(cat "$s/policy-$b" 2>/dev/null || printf '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:GetObject"],"Resource":["arn:aws:s3:::%s/*"]}]}' "$b")"
                # Silo orders a policy's actions differently from call to call; an alternative ordering models that.
                if [ -f "$s/policy-order-$k" ]; then
                  turn=$(( $(cat "$s/turn-$k" 2>/dev/null || echo 0) + 1 )); echo "$turn" > "$s/turn-$k"
                  [ $((turn % 2)) = 0 ] && policy="$(cat "$s/policy-order-$k")"
                fi
                printf '{"status":"success","accessKey":"%s","parentUser":"hvoroot","accountStatus":"on","policy":%s,"name":"%s","description":"%s","expiration":"1970-01-01T00:00:00Z","provider":"builtin"}\n' "$k" "$policy" "$n" "$d"; exit 0 ;;
              "admin accesskey rm root/ "*)
                mutate "$*"; grep -v "^$5|" "$s/keys" > "$s/keys.next"; mv "$s/keys.next" "$s/keys"; exit 0 ;;
              "stat writer/"*" --json")
                target="${2#writer/}"; [ -f "$s/blobs/$target" ] || exit 1
                printf '{"status":"success","versionID":"v-%s"}\n' "$(sha256sum "$s/blobs/$target" | cut -c1-12)"; exit 0 ;;
              "stat writer/"*)
                b="${2#writer/}"; grep -q "^$b " "$s/buckets"; exit $? ;;
              "version info writer/"*" --json")
                b="${3#writer/}"; v="$(awk -v b="$b" '$1 == b { print $2 }' "$s/buckets")"; [ "$v" = "-" ] && v=""
                printf '{"Op":"info","status":"success","versioning":{"status":"%s"}}\n' "$v"; exit 0 ;;
              "cat --version-id "*" writer/"*)
                cat "$s/blobs/${4#writer/}"; exit $? ;;
              "put "*)
                args=("$@"); file="${args[-2]}"; target="${args[-1]#root/}"; json=0; [ "$2" = "--json" ] && json=1
                if [ -f "$s/blobs/$target" ]; then
                  if [ "$json" = 1 ]; then printf '{"status":"error","error":{"message":"unable to upload","cause":{"message":"At least one of the pre-conditions you specified did not hold","error":{"Code":"PreconditionFailed"}}}}\n'
                  else printf 'mcli: <ERROR> unable to upload. At least one of the pre-conditions you specified did not hold.\n'; fi
                  exit 1
                fi
                mutate "put $target"; mkdir -p "$(dirname "$s/blobs/$target")"; cp "$file" "$s/blobs/$target"; exit 0 ;;
              "admin accesskey create root/ "*)
                mutate "$*"; printf 'NEWWRITERKEY00000001|hvo-writer-g1|HVO CameraAgent writer generation 1|new\n' >> "$s/keys"
                printf '{"status":"success","accessKey":"NEWWRITERKEY00000001","secretKey":"newWriterSecret0123456789ABCDEFGHIJKLMN"}\n'; exit 0 ;;
            esac
            printf 'fake mcli: unsupported %s\n' "$*" >&2
            exit 2
            """;
    }
}
