using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using static HVO.SkyMonitor.Deployment.SplitHostJson;

namespace HVO.SkyMonitor.Deployment;

internal sealed class PartialPrepareState : IDisposable
{
    public bool PublicationInProgress { get; private set; }
    private readonly SafeFileHandle runLock;
    private readonly string state;
    private readonly string evidence;
    private readonly string prefix;
    private readonly Action<string> boundary;
    private readonly UnixNodeIdentity stateIdentity;
    private readonly UnixNodeIdentity evidenceIdentity;
    public PartialPrepareState(string stateRoot, string evidenceRoot, bool inspect, Action<string> failpoint)
    {
        Require(Root(stateRoot) && Root(evidenceRoot) && !Beneath(stateRoot, evidenceRoot) && !Beneath(evidenceRoot, stateRoot), "state-evidence-overlap");
        PrepareDirectory(stateRoot); PrepareDirectory(evidenceRoot);
        state = stateRoot; evidence = evidenceRoot; prefix = inspect ? "partial-prepare-inspect" : "partial-prepare-cleanup"; boundary = failpoint;
        stateIdentity = NativeLinux.GetNodeIdentity(state); evidenceIdentity = NativeLinux.GetNodeIdentity(evidence);
        runLock = NativeLinux.OpenLockFile(state + "/run.lock");
        try
        {
            SafeFileSystem.ValidateOwnerFile(runLock, "run.lock");
            Require(NativeLinux.GetOpenNodeIdentity(runLock, "run.lock") == NativeLinux.GetNodeIdentity(state + "/run.lock"), "unsafe-lock-entry");
            Require(NativeLinux.TryLockExclusive(runLock, "run.lock"), "run-already-active");
        }
        catch { runLock.Dispose(); throw; }
    }
    private static void PrepareDirectory(string path)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(path);
        var current = path;
        while (!Directory.Exists(current)) current = Path.GetDirectoryName(current)!;
        var node = NativeLinux.GetNodeIdentity(current);
        Require(node.Type == 0x4000 && node.Uid == NativeLinux.getuid() && ((int)node.Mode & 18) == 0, "unsafe-output-ancestor");
        if (Directory.Exists(path)) Require((int)node.Mode == 448, "output-root-must-be-owner-only");
        else Directory.CreateDirectory(path, SafeFileSystem.OwnerDirectoryMode);
    }
    private void Revalidate()
    {
        SafeFileSystem.EnsureSafeExistingAncestors(state); SafeFileSystem.EnsureSafeExistingAncestors(evidence);
        Require(NativeLinux.GetNodeIdentity(state) == stateIdentity && NativeLinux.GetNodeIdentity(evidence) == evidenceIdentity &&
            NativeLinux.GetOpenNodeIdentity(runLock, "run.lock") == NativeLinux.GetNodeIdentity(state + "/run.lock"), "state-directory-changed");
    }
    public JsonObject Read(string name, bool fromEvidence = false)
    {
        Revalidate(); return Parse(ReadPrivate((fromEvidence ? evidence : state) + "/" + name));
    }
    public static string ReadPrivate(string path)
    {
        var identity = NativeLinux.GetNodeIdentity(path);
        Require(identity.Type == NativeLinux.RegularFileNodeType, "unsafe-state-entry");
        using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        Require(NativeLinux.GetOpenNodeIdentity(stream.SafeFileHandle, path) == identity, "state-entry-changed");
        Require(stream.Length <= 4 * 1024 * 1024, "state-too-large");
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
    public bool Exists(string name, bool fromEvidence = false) => NativeLinux.TryGetNodeIdentity((fromEvidence ? evidence : state) + "/" + name) is not null;
    public void RequireNoLaterPhase()
    {
        foreach (var name in new[] { "up-ledger.json", "up-manifest.json", "up-commit.json", "up-rendered", "down-ledger.json", "down-manifest.json", "down-commit.json" }) Require(!Exists(name), "later-phase-present");
        Require(!Exists("up.json", true) && !Exists("down.json", true), "later-phase-present");
        var registry = state + "/private-upload-registry.json";
        if (NativeLinux.TryGetNodeIdentity(registry) is null) SafeFileSystem.WriteTextAtomic(registry, "[]\n");
        Require(JsonNode.Parse(ReadPrivate(registry)) is JsonArray { Count: 0 }, "private-artifacts-present");
    }
    private string Ledger => state + "/" + prefix + "-ledger.json";
    private string Manifest => state + "/" + prefix + "-manifest.json";
    private string Evidence => evidence + "/" + prefix + ".json";
    private string Commit => state + "/" + prefix + "-commit.json";
    public JsonObject? Recover(Action<JsonObject> validate)
    {
        Revalidate();
        if (NativeLinux.TryGetNodeIdentity(Ledger) is null)
        {
            Require(new[] { Manifest, Evidence, Commit }.All(path => NativeLinux.TryGetNodeIdentity(path) is null), "orphan-phase-companion"); return null;
        }
        var ledger = Parse(ReadPrivate(Ledger)); validate(ledger); var expected = CommitFor(ledger); var generation = ledger["publicationGeneration"]!.GetValue<int>();
        if (NativeLinux.TryGetNodeIdentity(Commit) is not null)
        {
            var commit = Parse(ReadPrivate(Commit));
            Require(commit["schemaVersion"]?.GetValue<int>() == 1 && commit["generation"] is JsonValue && Match(commit.Text("ledgerSha256"), "[0-9a-f]{64}"), "invalid-phase-commit");
            var committed = commit["generation"]!.GetValue<int>();
            if (committed == generation)
            {
                Require(JsonNode.DeepEquals(commit, expected), "committed-ledger-mismatch");
                Require(JsonNode.DeepEquals(Parse(ReadPrivate(Manifest)), ledger) && JsonNode.DeepEquals(Parse(ReadPrivate(Evidence)), ledger), "committed-phase-mirror-mismatch"); return ledger;
            }
            Require(generation == committed + 1, "publication-generation-gap");
        }
        else Require(generation == 1, "phase-commit-missing");
        Write(Manifest, ledger); Write(Evidence, ledger); Write(Commit, expected); return ledger;
    }
    private static JsonObject CommitFor(JsonObject ledger) => new() { ["schemaVersion"] = 1, ["generation"] = ledger["publicationGeneration"]!.DeepClone(), ["ledgerSha256"] = Hash(Canonical(ledger) + "\n") };
    private void Write(string path, JsonObject value) { Revalidate(); SafeFileSystem.WriteTextAtomic(path, value.ToJsonString(Options) + "\n"); }
    public void Publish(JsonObject ledger)
    {
        if (ledger.Text("phaseStatus") == "passed") RequireNoLaterPhase();
        PublicationInProgress = true;
        ledger["updatedAt"] = Now(); ledger["publicationGeneration"] = ledger["publicationGeneration"]!.GetValue<int>() + 1;
        Write(Ledger, ledger); boundary("abrupt-after-" + prefix + "-ledger");
        Write(Manifest, ledger); boundary("abrupt-after-" + prefix + "-manifest");
        Write(Evidence, ledger); boundary("abrupt-after-" + prefix + "-evidence");
        Write(Commit, CommitFor(ledger)); PublicationInProgress = false; boundary("abrupt-after-" + prefix + "-commit");
    }
    public void Dispose() => runLock.Dispose();
}
