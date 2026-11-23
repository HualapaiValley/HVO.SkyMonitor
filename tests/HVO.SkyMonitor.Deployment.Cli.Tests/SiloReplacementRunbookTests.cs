using System.Diagnostics;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The runbook's stock-image replacement block for an installed instance (#1966 review rounds 2 and 3), extracted from
/// <c>docs/runbooks/cameraagent-silo-runtime.md</c> and run by bash as written, with only its two placeholders given. A
/// scripted <c>docker</c> and <c>hvo-sky-install</c> stand in for the daemon and the installer; the data root, its ancestors
/// and the other containers' mount sources are real directories and links, judged by the same device and inode the
/// installer's own exclusivity check compares. Silo stays stopped whenever its data root is not proved its own, and every
/// stop says once what the phase it reached leaves.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloReplacementRunbookTests
{
    private static readonly Guid InstanceId = Guid.Parse("eba0e357-d549-4692-9ed6-9d2634accc6d");
    private static readonly string Compact = InstanceId.ToString("N");
    private static readonly string Silo = $"hvo-skymonitor-{Compact}-silo";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("clean")]
    [DataRow("listed-container-gone-since")]
    [DataRow("archive-degraded")]
    public async Task TheBlock_ReplacesSiloWhenItsDataRootIsItsOwn(string change)
    {
        using var run = new Run();
        if (change == "listed-container-gone-since") run.Flag("listed-but-gone");
        if (change == "archive-degraded") run.Verify("degraded");

        var (exit, output, error) = await run.ExecuteAsync(TestContext.CancellationToken);

        Assert.AreEqual(0, exit, error);
        StringAssert.Contains(output, "Replaced: 2 object versions read back by version and SHA-256", StringComparison.Ordinal);
        StringAssert.Contains(output, change == "archive-degraded"
            ? "silo-verify degraded: archive temporarily-unavailable (archive-unreachable), unchanged by the replacement."
            : "silo-verify ready.", StringComparison.Ordinal);
        CollectionAssert.Contains(run.DockerCalls, $"rm {Silo}");
        Assert.IsTrue(run.DockerCalls.Any(static call => call.StartsWith("compose ", StringComparison.Ordinal) && call.EndsWith(" up --detach --no-deps silo", StringComparison.Ordinal)));
        Assert.IsTrue(run.SiloRunning, "The replacement runs.");
        Assert.IsEmpty(Directory.GetFileSystemEntries(run.TemporaryRoot), "The working folder is removed after a replacement.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("data-root-under-another-name", "the same directory as")]
    [DataRow("ancestor-under-another-name", "the same directory as")]
    [DataRow("data-root-itself", "mounts")]
    [DataRow("path-inside-the-data-root", "mounts")]
    [DataRow("ancestor-path", "mounts")]
    [DataRow("unanswered-inventory", "\"ids=$(docker ps -aq --no-trunc)\" failed.")]
    [DataRow("uninspectable-container", "Docker could not inspect container intruder, so its mounts are unknown")]
    public async Task TheBlock_KeepsSiloStoppedWhileItsDataRootIsNotProvedItsOwn(string change, string reason)
    {
        using var run = new Run();
        switch (change)
        {
            case "data-root-under-another-name": run.Intruder(run.Link("data-alias", run.DataRoot)); break;
            case "ancestor-under-another-name": run.Intruder(run.Link("instance-alias", run.InstanceRoot)); break;
            case "data-root-itself": run.Intruder(run.DataRoot); break;
            case "path-inside-the-data-root": run.Intruder(Path.Combine(run.DataRoot, ".minio.sys")); break;
            case "ancestor-path": run.Intruder(run.InstanceRoot); break;
            case "unanswered-inventory": run.Flag("ps-fails"); break;
            case "uninspectable-container": run.Intruder(Path.Combine(run.Root, "elsewhere")); run.Flag("inspect-fails-intruder"); break;
        }

        var (exit, _, error) = await run.ExecuteAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, exit, error);
        StringAssert.Contains(error, reason, StringComparison.Ordinal);
        AssertStoppedOnce(error);
        StringAssert.Contains(error, "Silo stays stopped and its original container is kept: its data root is not proved its own.", StringComparison.Ordinal);
        Assert.IsFalse(run.DockerCalls.Any(static call => call.StartsWith("rm ", StringComparison.Ordinal) || call.StartsWith("compose ", StringComparison.Ordinal)),
            "Nothing is removed or started: " + string.Join("; ", run.DockerCalls));
        Assert.IsFalse(run.SiloRunning, "Silo stays stopped.");
        Assert.IsTrue(run.OriginalSiloExists, "The original container is kept.");
        AssertInventoryKept(error);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("not-ready-before", "Nothing was changed; Silo is still running.")]
    [DataRow("replacement-does-not-start", "The original Silo container is removed and no replacement runs. Start the replacement with:")]
    [DataRow("read-back-differs", "The replacement Silo runs but did not verify as the original.")]
    public async Task TheBlock_SaysWhatThePhaseItReachedLeaves(string change, string guidance)
    {
        using var run = new Run();
        switch (change)
        {
            case "not-ready-before": run.Verify("not-ready"); break;
            case "replacement-does-not-start": run.Flag("up-fails"); break;
            case "read-back-differs": run.Flag("differ-after"); break;
        }

        var (exit, _, error) = await run.ExecuteAsync(TestContext.CancellationToken);

        Assert.AreEqual(1, exit, error);
        AssertStoppedOnce(error);
        StringAssert.Contains(error, guidance, StringComparison.Ordinal);
        Assert.IsFalse(error.Contains("docker start", StringComparison.Ordinal), "Only a kept, unproved original is ever started again: " + error);
        switch (change)
        {
            case "not-ready-before":
                Assert.IsFalse(run.DockerCalls.Any(static call => call.StartsWith("stop ", StringComparison.Ordinal)), "Nothing is stopped.");
                Assert.IsTrue(run.SiloRunning);
                Assert.IsFalse(error.Contains("The version inventory", StringComparison.Ordinal), "No inventory was taken to keep.");
                Assert.IsEmpty(Directory.GetFileSystemEntries(run.TemporaryRoot), "The empty working folder is removed.");
                return;
            case "replacement-does-not-start":
                StringAssert.Contains(error, $"--project-name hvo-skymonitor-{Compact}", StringComparison.Ordinal);
                Assert.IsFalse(run.OriginalSiloExists, "The original container was removed before the replacement failed.");
                break;
            case "read-back-differs":
                StringAssert.Contains(error, $"docker stop {Silo}", StringComparison.Ordinal);
                break;
        }
        AssertInventoryKept(error);
    }

    private static void AssertStoppedOnce(string error)
        => Assert.AreEqual(1, error.Split("Stopped: ").Length - 1, "The stop is reported once: " + error);

    private static void AssertInventoryKept(string error)
    {
        var line = error.Split('\n').Single(static line => line.StartsWith("The version inventory taken before the stop is in ", StringComparison.Ordinal));
        var folder = line["The version inventory taken before the stop is in ".Length..line.IndexOf("; remove it when done.", StringComparison.Ordinal)];
        Assert.IsTrue(Directory.Exists(folder), "The reported working folder is kept: " + folder);
    }

    /// <summary>One run of the block: a real instance layout, the scripted CLIs on PATH and the state they answer from.</summary>
    private sealed class Run : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("hvo-silo-replace-");

        public Run()
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(Path.Combine(InstanceRoot, "config", "secrets"));
            Directory.CreateDirectory(Path.Combine(Root, "elsewhere"));
            Directory.CreateDirectory(TemporaryRoot);
            Directory.CreateDirectory(Path.Combine(State, "containers"));
            Container("silo-original", "/" + Silo, DataRoot);
            Container("cameraagent", $"/hvo-skymonitor-{Compact}", Path.Combine(InstanceRoot, "config", "secrets"));
            Container("unrelated", "/unrelated", Path.Combine(Root, "elsewhere"));
            File.WriteAllText(Path.Combine(State, "listing"), "meta/instance.v1.json\tv1\nmeta/fence.json\tv2\n");
            Object("meta/instance.v1.json", "v1", "{\"format\":\"marker\"}");
            Object("meta/fence.json", "v2", "{\"format\":\"fence\"}");
            Verify("ready");
            Script("docker", FakeDocker);
            Script("hvo-sky-install", FakeInstaller);
        }

        public string Root => root.FullName;
        public string ProductRoot => Path.Combine(Root, "product");
        public string InstanceRoot => Path.Combine(ProductRoot, "cameraagents", InstanceId.ToString("D"));
        public string DataRoot => Path.Combine(InstanceRoot, "silo");
        public string TemporaryRoot => Path.Combine(Root, "tmp");
        private string State => Path.Combine(Root, "state");
        private string Bin => Path.Combine(Root, "bin");

        public string[] DockerCalls => File.Exists(Path.Combine(State, "calls")) ? File.ReadAllLines(Path.Combine(State, "calls")) : [];
        public bool OriginalSiloExists => Directory.Exists(Path.Combine(State, "containers", "silo-original"));
        public bool SiloRunning => Directory.GetDirectories(Path.Combine(State, "containers"))
            .Any(container => File.ReadAllText(Path.Combine(container, "name")).Trim() == "/" + Silo &&
                              File.ReadAllText(Path.Combine(container, "running")).Trim() == "true");

        public void Flag(string name) => File.WriteAllText(Path.Combine(State, name), string.Empty);

        public void Verify(string readiness) => File.WriteAllText(Path.Combine(State, "verify.json"),
            $"{{\"readiness\":\"{readiness}\",\"archive\":{{\"state\":\"temporarily-unavailable\",\"reason\":\"archive-unreachable\"}}}}\n");

        /// <summary>A link elsewhere naming <paramref name="target"/>: the same directory under another name.</summary>
        public string Link(string name, string target)
        {
            var link = Path.Combine(Root, name);
            File.CreateSymbolicLink(link, target);
            return link;
        }

        public void Intruder(string source) => Container("intruder", "/intruder", source);

        public async Task<(int Exit, string Output, string Error)> ExecuteAsync(CancellationToken cancellationToken)
        {
            var block = Block();
            var script = Path.Combine(Root, "replace.sh");
            await File.WriteAllTextAsync(script, block.Replace(
                "ID='<instance-id>'; P='<product-root>';",
                $"ID='{InstanceId:D}'; P='{ProductRoot}';",
                StringComparison.Ordinal), cancellationToken);
            Assert.AreEqual(1, (await File.ReadAllLinesAsync(script, cancellationToken)).Zip(block.Split('\n')).Count(static pair => pair.First != pair.Second),
                "Only the placeholders are given.");
            var start = new ProcessStartInfo("bash")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(script);
            start.Environment["PATH"] = Bin + ":/usr/bin:/bin";
            start.Environment["TMPDIR"] = TemporaryRoot;
            start.Environment["FAKE_STATE"] = State;
            start.Environment["FAKE_SILO"] = Silo;
            start.Environment["FAKE_DATA"] = DataRoot;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, await output, await error);
        }

        private static string Block()
        {
            var runbook = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", "runbooks", "cameraagent-silo-runtime.md"));
            var intro = Array.FindIndex(runbook, static line => line.StartsWith("On an installed instance, as the user that installed it, with `jq`", StringComparison.Ordinal));
            Assert.IsGreaterThanOrEqualTo(0, intro, "The runbook's replacement block for an installed instance is found.");
            var open = Array.FindIndex(runbook, intro, static line => line == "```bash");
            var close = Array.FindIndex(runbook, open + 1, static line => line == "```");
            return string.Join('\n', runbook[(open + 1)..close]) + "\n";
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
        }

        private void Container(string id, string name, string mount)
        {
            var directory = Path.Combine(State, "containers", id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "name"), name + "\n");
            File.WriteAllText(Path.Combine(directory, "running"), "true\n");
            File.WriteAllText(Path.Combine(directory, "health"), "healthy\n");
            File.WriteAllText(Path.Combine(directory, "mounts"), mount + "\n");
        }

        private void Object(string key, string version, string content)
        {
            var path = Path.Combine(State, "objects", $"{key}@{version}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private void Script(string name, string text)
        {
            Directory.CreateDirectory(Bin);
            var path = Path.Combine(Bin, name);
            File.WriteAllText(path, text);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose() => root.Delete(recursive: true);

        /// <summary>The Docker CLI the block calls, answered from the state folder; every call is logged.</summary>
        private const string FakeDocker = """
            #!/usr/bin/env bash
            set -u
            S=${FAKE_STATE:?}
            printf '%s\n' "$*" >> "$S/calls"
            find_container() {
              local d
              for d in "$S"/containers/*; do
                if [[ -d $d && ( $(basename "$d") == "$1" || $(cat "$d/name") == "/$1" ) ]]; then echo "$d"; return 0; fi
              done
              return 1
            }
            case "$1" in
              ps)
                if [[ -e $S/ps-fails ]]; then echo "Cannot connect to the Docker daemon (scripted)" >&2; exit 1; fi
                for d in "$S"/containers/*; do if [[ -d $d ]]; then basename "$d"; fi; done
                if [[ -e $S/listed-but-gone ]]; then echo gone0000; fi
                exit 0 ;;
              inspect)
                format=$3; target=$4
                if [[ -e $S/inspect-fails-$target ]]; then echo "Error response from daemon: scripted failure" >&2; exit 1; fi
                d=$(find_container "$target") || { echo "Error response from daemon: No such container: $target" >&2; exit 1; }
                running=$(cat "$d/running")
                case "$format" in
                  '{{.State.Pid}}') if [[ $running == true ]]; then echo 2147483646; else echo 0; fi ;;
                  '{{.State.Running}} {{.State.Pid}}') if [[ $running == true ]]; then echo "true 2147483646"; else echo "false 0"; fi ;;
                  '{{.Name}}') cat "$d/name" ;;
                  '{{range .Mounts}}{{println .Source}}{{end}}') cat "$d/mounts"; echo ;;
                  '{{.State.Health.Status}}') cat "$d/health" ;;
                  *) echo "unscripted format $format" >&2; exit 2 ;;
                esac
                exit 0 ;;
              stop)
                d=$(find_container "${@: -1}") || exit 1
                echo false > "$d/running"; echo "${@: -1}"; exit 0 ;;
              rm)
                d=$(find_container "$2") || exit 1
                rm -rf "$d"; echo "$2"; exit 0 ;;
              compose)
                if [[ " $* " != *" up --detach --no-deps silo "* ]]; then echo "unscripted compose call" >&2; exit 2; fi
                if [[ -e $S/up-fails ]]; then echo "compose up failed (scripted)" >&2; exit 1; fi
                new=$S/containers/silo-replacement; mkdir -p "$new"
                echo "/$FAKE_SILO" > "$new/name"; echo true > "$new/running"; echo healthy > "$new/health"; echo "$FAKE_DATA" > "$new/mounts"
                if [[ -e $S/differ-after ]]; then echo '{"format":"changed"}' > "$S/objects/meta/fence.json@v2"; fi
                exit 0 ;;
              exec)
                if [[ $2 == -i ]]; then
                  IFS=$'\t' read -r key version
                  cat "$S/objects/$key@$version"; exit 0
                fi
                while IFS=$'\t' read -r key version; do
                  printf '{"status":"success","type":"file","key":"%s","versionId":"%s","isLatest":true}\n' "$key" "$version"
                done < "$S/listing"
                exit 0 ;;
            esac
            echo "unscripted docker call: $*" >&2
            exit 2
            """;

        /// <summary><c>hvo-sky-install cameraagent silo-verify</c>: the readiness the state names; not-ready exits 3.</summary>
        private const string FakeInstaller = """
            #!/usr/bin/env bash
            cat "$FAKE_STATE/verify.json"
            if [[ "$(jq -r .readiness "$FAKE_STATE/verify.json")" == not-ready ]]; then exit 3; fi
            exit 0
            """;
    }
}
