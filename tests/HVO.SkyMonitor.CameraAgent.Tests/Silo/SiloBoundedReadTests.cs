using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed class SiloBoundedReadTests
{
    private const string Key = "object";
    private const long MetadataReadLimit = 64L * 1024 * 1024;

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
    public async Task InvalidMetadataBoundsAreRefusedBeforeStoreAccess()
    {
        var store = new SiloTestStore("local-silo", "instance-bucket") { Unreachable = true };

        foreach (var bound in new[] { MetadataReadLimit + 1, long.MaxValue, 0L })
        {
            var read = () => SiloVerifier.ReadCurrentAsync(store, Key, bound, CancellationToken.None).AsTask();

            var exception = await read.Should().ThrowAsync<ArgumentOutOfRangeException>(
                "the limit {0} must be rejected before contacting the store", bound).ConfigureAwait(false);
            exception.Which.ParamName.Should().Be("maximumBytes");
        }
    }

    [TestMethod]
    public async Task MetadataBoundsAtAndOneByteBelowTheCeilingAreAccepted()
    {
        var store = new SiloTestStore("local-silo", "instance-bucket");
        var version = store.Plant(Key, [1]);

        foreach (var bound in new[] { MetadataReadLimit, MetadataReadLimit - 1 })
        {
            var result = await SiloVerifier.ReadCurrentAsync(store, Key, bound, CancellationToken.None).ConfigureAwait(false);

            result.Should().NotBeNull("the limit {0} must allow the read", bound);
            result!.Value.VersionId.Should().Be(version);
            result.Value.Bytes.Should().Equal([1]);
        }
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
