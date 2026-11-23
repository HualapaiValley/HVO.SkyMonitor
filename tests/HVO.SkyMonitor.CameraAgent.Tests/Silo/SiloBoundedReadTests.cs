using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
public sealed class SiloBoundedReadTests
{
    private const string Key = "object";

    [TestMethod]
    [DataRow(5L)]
    [DataRow(81925L)]
    public async Task ExactProbeReadsAtMostOneBytePastTheExpectedLength(long expectedLength)
    {
        var store = new SiloTestStore("local-silo", "instance-bucket");
        var version = store.Plant(Key, new byte[1024 * 1024]);

        var probe = await SiloVerifier.ProbeAsync(store, store.Locator(Key, version), expectedLength, SiloHash.Sha256([1]),
            TimeProvider.System, captureBytes: false, CancellationToken.None).ConfigureAwait(false);

        probe.Status.Should().Be(SiloProbeStatus.Damaged);
        probe.Detail.Should().Be("length-exceeds-identity");
        store.BytesServed(Key).Should().Be(expectedLength + 1);
    }

    [TestMethod]
    [DataRow(long.MaxValue)]
    [DataRow(0L)]
    public async Task InvalidMetadataBoundsAreRefusedBeforeReadingBytes(long bound)
    {
        var store = new SiloTestStore("local-silo", "instance-bucket");
        store.Plant(Key, [1]);
        var read = () => SiloVerifier.ReadCurrentAsync(store, Key, bound, CancellationToken.None).AsTask();

        (await read.Should().ThrowAsync<ArgumentOutOfRangeException>().ConfigureAwait(false)).Which.ParamName.Should().Be("maximumBytes");
        store.BytesServed(Key).Should().Be(0);
    }

    [TestMethod]
    public async Task AOneByteMetadataBoundAcceptsAnExactFitAndReadsOneExtraByteToRefuseAnOversizedObject()
    {
        var store = new SiloTestStore("local-silo", "instance-bucket");
        var version = store.Plant(Key, [1]);

        var exact = await SiloVerifier.ReadCurrentAsync(store, Key, 1, CancellationToken.None).ConfigureAwait(false);

        exact!.Value.VersionId.Should().Be(version);
        exact.Value.Bytes.Should().Equal([1]);
        store.BytesServed(Key).Should().Be(1);

        store.Plant(Key, [1, 2, 3]);
        var read = () => SiloVerifier.ReadCurrentAsync(store, Key, 1, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<SiloInvalidObjectException>().ConfigureAwait(false);
        store.BytesServed(Key).Should().Be(3, "the second read consumed only two bytes");
    }
}
