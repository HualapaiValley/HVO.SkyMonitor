using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

/// <summary>
/// Covers the operator settings file editor: key-level edits that keep every other setting, removal that leaves no
/// empty sections, version-checked writes that never overwrite a hand edit, and files the operator must correct.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OperatorSettingsFileTests
{
    private const string LimitKey = "CameraAgent:SkyMap:MaximumObjects";

    private string _root = null!;
    private int _reloads;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-operator-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _reloads = 0;
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string SettingsPath => Path.Combine(_root, "appsettings.local.json");

    [TestMethod]
    public async Task ReadAsync_WhenTheFileIsAbsent_ReportsAnEmptyReadableFile()
    {
        using var file = CreateFile();

        var snapshot = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsFile.AbsentVersion, snapshot.Version);
        Assert.IsFalse(snapshot.Exists);
        Assert.IsNull(snapshot.Problem);
        Assert.IsNull(snapshot.GetValue(LimitKey));
    }

    [TestMethod]
    public async Task WriteAsync_CreatesAnOwnerOnlyFileVersionedByItsContent()
    {
        using var file = CreateFile();

        var result = await WriteAsync(file, OperatorSettingsFile.AbsentVersion, LimitKey, 900).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsWriteStatus.Applied, result.Status);
        Assert.AreEqual("900", result.Snapshot.GetValue(LimitKey));
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(SettingsPath).ConfigureAwait(false))), result.Snapshot.Version);
        Assert.AreEqual(1, _reloads);
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SettingsPath));
        }
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    [TestMethod]
    public async Task WriteAsync_ChangesOnlyTheNamedKeyAndMatchesItWithoutRegardToCase()
    {
        await File.WriteAllTextAsync(SettingsPath, """
            {
              /* Kept by hand. */
              "Logging": { "LogLevel": { "Default": "Warning" } },
              "cameraagent": { "skymap": { "maximumobjects": 400, "Other": true } },
            }
            """).ConfigureAwait(false);
        using var file = CreateFile();
        var before = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        var result = await WriteAsync(file, before.Version, LimitKey, 900).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsWriteStatus.Applied, result.Status);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false))!.AsObject();
        Assert.AreEqual("Warning", saved["Logging"]!["LogLevel"]!["Default"]!.ToString());
        var skyMap = saved["cameraagent"]!["skymap"]!.AsObject();
        Assert.HasCount(2, skyMap);
        Assert.AreEqual("900", skyMap["maximumobjects"]!.ToString());
        Assert.AreEqual("true", result.Snapshot.GetValue("CameraAgent:SkyMap:Other"));
    }

    [TestMethod]
    public async Task WriteAsync_RemovingTheLastKeyOfASection_RemovesTheSection()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "Keep": "yes", "CameraAgent": { "SkyMap": { "MaximumObjects": 400 } } }""").ConfigureAwait(false);
        using var file = CreateFile();
        var before = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        var result = await WriteAsync(file, before.Version, LimitKey, null).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsWriteStatus.Applied, result.Status);
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false))!.AsObject();
        Assert.HasCount(1, saved);
        Assert.AreEqual("yes", saved["Keep"]!.ToString());
        Assert.IsFalse(result.Snapshot.HasSection("CameraAgent"));
    }

    [TestMethod]
    public async Task WriteAsync_RemovingAKeyTheFileDoesNotHave_IsUnchanged()
    {
        using var file = CreateFile();
        var created = await WriteAsync(file, OperatorSettingsFile.AbsentVersion, "Keep", "yes").ConfigureAwait(false);

        var result = await WriteAsync(file, created.Snapshot.Version, LimitKey, null).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsWriteStatus.Unchanged, result.Status);
        Assert.AreEqual(1, _reloads);
    }

    [TestMethod]
    public async Task WriteAsync_WithAStaleVersion_ConflictsWithoutWriting()
    {
        using var file = CreateFile();
        var created = await WriteAsync(file, OperatorSettingsFile.AbsentVersion, LimitKey, 400).ConfigureAwait(false);
        const string handEdit = """{ "CameraAgent": { "SkyMap": { "MaximumObjects": 700 } } }""";
        await File.WriteAllTextAsync(SettingsPath, handEdit).ConfigureAwait(false);

        var result = await WriteAsync(file, created.Snapshot.Version, LimitKey, 900).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsWriteStatus.Conflict, result.Status);
        Assert.AreEqual("700", result.Snapshot.GetValue(LimitKey));
        Assert.AreEqual(handEdit, await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false));
        Assert.AreEqual(1, _reloads);
    }

    [TestMethod]
    public async Task WriteAsync_WhenTheFileIsEditedBeforePublication_ConflictsAndKeepsTheEdit()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "CameraAgent": { "SkyMap": { "MaximumObjects": 400 } } }""").ConfigureAwait(false);
        const string handEdit = """{ "CameraAgent": { "SkyMap": { "MaximumObjects": 700 } } }""";
        using var file = new OperatorSettingsFile(SettingsPath, () => _reloads++, _ => [])
        {
            BeforePublish = () => File.WriteAllText(SettingsPath, handEdit)
        };
        var before = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        var result = await WriteAsync(file, before.Version, LimitKey, 900).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsWriteStatus.Conflict, result.Status);
        Assert.AreEqual("700", result.Snapshot.GetValue(LimitKey));
        Assert.AreEqual(handEdit, await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false));
        Assert.AreEqual(0, _reloads);
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    [TestMethod]
    public async Task AFileLargerThanTheOldBoundButWithinTheDeploymentBound_IsEditable()
    {
        // Deployment tooling carries a settings file of up to 1 MiB across a reset; the editor must accept the same.
        var padding = new string('x', 300 * 1024);
        await File.WriteAllTextAsync(SettingsPath, $$"""{ "Padding": "{{padding}}" }""").ConfigureAwait(false);
        using var file = CreateFile();
        var snapshot = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        var result = await WriteAsync(file, snapshot.Version, LimitKey, 900).ConfigureAwait(false);

        Assert.IsNull(snapshot.Problem);
        Assert.AreEqual(OperatorSettingsWriteStatus.Applied, result.Status);
        Assert.AreEqual(padding, result.Snapshot.GetValue("Padding"));
    }

    [TestMethod]
    [DataRow("""{ "CameraAgent": """, DisplayName = "truncated")]
    [DataRow("""[ 1, 2 ]""", DisplayName = "array root")]
    [DataRow("""{ "CameraAgent": 1, "cameraAgent": 2 }""", DisplayName = "duplicate key")]
    [DataRow("""{ "Nested": { "Key": 1, "KEY": 2 } }""", DisplayName = "nested duplicate key")]
    public async Task AnUnreadableFile_IsReportedAndNeverRewritten(string content)
    {
        await File.WriteAllTextAsync(SettingsPath, content).ConfigureAwait(false);
        using var file = CreateFile();

        var snapshot = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await WriteAsync(file, snapshot.Version, LimitKey, 900).ConfigureAwait(false);

        Assert.IsTrue(snapshot.Exists);
        Assert.IsNotNull(snapshot.Problem);
        Assert.IsNull(snapshot.GetValue("CameraAgent"));
        Assert.AreEqual(OperatorSettingsWriteStatus.Unreadable, result.Status);
        Assert.AreEqual(content, await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false));
        Assert.AreEqual(0, _reloads);
    }

    [TestMethod]
    public async Task AnOversizedFile_IsReportedAndNeverRewritten()
    {
        await File.WriteAllTextAsync(SettingsPath, $$"""{ "Padding": "{{new string('x', OperatorSettingsFile.MaximumFileBytes)}}" }""").ConfigureAwait(false);
        using var file = CreateFile();

        var snapshot = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var result = await WriteAsync(file, snapshot.Version, LimitKey, 900).ConfigureAwait(false);

        Assert.IsNotNull(snapshot.Problem);
        Assert.AreEqual(OperatorSettingsWriteStatus.Unreadable, result.Status);
        Assert.AreEqual(0, _reloads);
    }

    [TestMethod]
    public async Task WriteAsync_UnderAValueThatIsNotASection_IsUnreadable()
    {
        const string content = """{ "CameraAgent": { "SkyMap": 5 } }""";
        await File.WriteAllTextAsync(SettingsPath, content).ConfigureAwait(false);
        using var file = CreateFile();
        var snapshot = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        var result = await WriteAsync(file, snapshot.Version, LimitKey, 900).ConfigureAwait(false);

        Assert.AreEqual(OperatorSettingsWriteStatus.Unreadable, result.Status);
        Assert.Contains(LimitKey, result.Snapshot.Problem!);
        Assert.AreEqual(content, await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task GetValue_ReadsScalarsAndIgnoresSectionsAndArrays()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "A": { "Text": "x", "Number": 1.5, "Flag": false, "Section": {}, "List": [1] } }""").ConfigureAwait(false);
        using var file = CreateFile();

        var snapshot = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual("x", snapshot.GetValue("a:text"));
        Assert.AreEqual("1.5", snapshot.GetValue("A:Number"));
        Assert.AreEqual("false", snapshot.GetValue("A:Flag"));
        Assert.IsNull(snapshot.GetValue("A:Section"));
        Assert.IsTrue(snapshot.HasSection("A:Section"));
        Assert.IsNull(snapshot.GetValue("A:List"));
        Assert.IsNull(snapshot.GetValue("A:Missing"));
    }

    [TestMethod]
    public async Task ReadAsync_RefusesASymbolicLink()
    {
        var elsewhere = Path.Combine(_root, "elsewhere.json");
        await File.WriteAllTextAsync(elsewhere, "{}").ConfigureAwait(false);
        File.CreateSymbolicLink(SettingsPath, elsewhere);
        using var file = CreateFile();

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await file.ReadAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WriteAsync_RefusesADanglingSymbolicLinkAndLeavesIt()
    {
        var missing = Path.Combine(_root, "missing.json");
        File.CreateSymbolicLink(SettingsPath, missing);
        using var file = CreateFile();

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await WriteAsync(file, OperatorSettingsFile.AbsentVersion, LimitKey, 900).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.AreEqual(missing, new FileInfo(SettingsPath).LinkTarget);
        Assert.IsFalse(File.Exists(missing));
        Assert.AreEqual(0, _reloads);
    }

    [TestMethod]
    public async Task WriteAsync_WhenTheDirectorySyncFails_HasAlreadyPublishedTheFile()
    {
        using var file = new OperatorSettingsFile(SettingsPath, () => _reloads++, _ => [])
        {
            SyncPublishedDirectory = _ => throw new IOException("sync failed")
        };

        await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await WriteAsync(file, OperatorSettingsFile.AbsentVersion, LimitKey, 900).ConfigureAwait(false)).ConfigureAwait(false);

        var snapshot = await file.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("900", snapshot.GetValue(LimitKey));
        Assert.IsEmpty(Directory.GetFiles(_root, "*.tmp"));
    }

    [TestMethod]
    public void Constructor_RequiresAnAbsolutePath()
        => Assert.ThrowsExactly<ArgumentException>(() => new OperatorSettingsFile("appsettings.local.json", () => { }, _ => []));

    private OperatorSettingsFile CreateFile() => new(SettingsPath, () => _reloads++, _ => []);

    private static ValueTask<OperatorSettingsWriteResult> WriteAsync(
        OperatorSettingsFile file,
        string expectedVersion,
        string key,
        JsonNode? value)
        => file.WriteAsync(
            expectedVersion,
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { [key] = value },
            "owner-id",
            CancellationToken.None);
}
