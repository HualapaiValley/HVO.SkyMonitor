using System.Text.Json;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class InstallRecordTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Correction1_NewRecord_IsReadableWithTheReleasedStrictDeclarations()
    {
        using var root = new ProductRoot();
        File.SetUnixFileMode(root.Paths.ProductRoot, SafeFileSystem.OwnerDirectoryMode);
        using var release = ReleaseFixture.Create(root.Paths.ProductRoot);
        await PathPrograms.SettleAsync(root.Paths, InstallRequest.DefaultProductRoot, release.Folder, ImageA, CancellationToken.None);
        var location = InstallRecordLocation.For(root.Paths.ProductRoot, InstallRequest.DefaultProductRoot);
        var manifest = root.Manifest(ImageA, null);
        await InstallRecordStore.SettleInstanceAsync(location, root.Paths, manifest, InstallRecordEvent.Install, CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(location.Path);
        var previous = JsonSerializer.Deserialize(bytes, LegacyInstallRecordJsonContext.Default.LegacyInstallRecord);
        Assert.IsNotNull(previous);
        Assert.AreEqual(1, previous.SchemaVersion);
        Assert.AreEqual(manifest.InstanceId, previous.Instances.Single().InstanceId);
        using var sidecar = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(
            Path.GetDirectoryName(location.Path)!, "install-programs.json")));
        Assert.AreEqual(3, sidecar.RootElement.GetProperty("instances")[0].GetProperty("programs").GetArrayLength());
    }
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Correction1_TrustedProgramSidecar_UsesPolicyLocationPermissionsAndIdempotence()
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("var/lib/hvo-sky");
        var location = InstallRecordLocation.Machine(host.Location, root);
        var entry = new InstallProgramInstance(First, [new("hvo-sky", "/home/runtime/.local/bin/hvo-sky", "1.2.0", new string('a', 64), null)]);
        var change = new InstallRecordChange(null, null, entry);
        Assert.AreEqual("updated", InstallRecordStore.ApplyTrusted(host.Location, root, change));
        var file = InstallRecordStore.ProgramsPath(location);
        var bytes = await File.ReadAllBytesAsync(file);
        Assert.AreEqual((UnixFileMode)0x1a4, File.GetUnixFileMode(file));
        Assert.AreEqual("unchanged", InstallRecordStore.ApplyTrusted(host.Location, root, change));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file));
        Assert.IsFalse(File.Exists(location.Path), "the program helper must not change schema-1 install.json");
        var record = await InstallRecordStore.ReadProgramsAsync(location, CancellationToken.None);
        Assert.AreEqual(First, record!.Instances.Single().InstanceId);
        Assert.AreEqual("updated", InstallRecordStore.ApplyTrusted(host.Location, root,
            new(null, null, new(First, null))));
        Assert.AreEqual(0, (await InstallRecordStore.ReadProgramsAsync(location, CancellationToken.None))!.Instances.Count);
    }

    [TestMethod]
    public void Correction1_ProgramSidecarHelper_ParsesOnlyOneValidatedInstanceAtTheFixedRecord()
    {
        var entry = new InstallProgramInstance(First, [new("hvo-sky", "/home/runtime/.local/bin/hvo-sky", "1.2.0", new string('a', 64), null)]);
        var args = InstallRecordStore.HelperArguments("/var/lib/hvo-sky", new(null, null, entry)).Skip(2).ToArray();
        Assert.IsTrue(InstallRecordStore.TryParse(args, out var root, out var change));
        Assert.AreEqual("/var/lib/hvo-sky", root);
        Assert.AreEqual(First, change!.Programs!.InstanceId);
        Assert.IsFalse(InstallRecordStore.TryParse([.. args, "/arbitrary/sidecar"], out _, out _));
        args[4] = args[4].TrimEnd().TrimEnd('}') + ",\"secret\":\"unexpected\"}";
        Assert.IsFalse(InstallRecordStore.TryParse(args, out _, out _));
        var pending = entry with { Programs = [entry.Programs![0] with { PendingSha256 = new string('b', 64) }] };
        args = InstallRecordStore.HelperArguments("/var/lib/hvo-sky", new(null, null, pending)).Skip(2).ToArray();
        Assert.IsFalse(InstallRecordStore.TryParse(args, out _, out _));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(UnixFileMode.GroupWrite)]
    [DataRow(UnixFileMode.OtherWrite)]
    public async Task Correction1_UntrustedProgramSidecar_IsPreservedAndRefused(UnixFileMode unsafeMode)
    {
        using var host = new PolicyHost();
        var root = host.CreateDirectory("var/lib/hvo-sky");
        var location = InstallRecordLocation.Machine(host.Location, root);
        var entry = new InstallProgramInstance(First, []);
        InstallRecordStore.ApplyTrusted(host.Location, root, new(null, null,
            entry with { Programs = [new("hvo-sky", "/home/runtime/.local/bin/hvo-sky", "1.2.0", new string('a', 64), null)] }));
        var file = InstallRecordStore.ProgramsPath(location);
        File.SetUnixFileMode(file, File.GetUnixFileMode(file) | unsafeMode);
        var bytes = await File.ReadAllBytesAsync(file);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallRecordStore.ReadProgramsAsync(location, CancellationToken.None));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(file));
    }

}
