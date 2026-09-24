using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

/// <summary>
/// Regression fixture for issue #1033: one retained Combined pixel at 75 ADU displayed as 5/255 by the on-demand
/// Mono16 default and as 108/255 by the persisted combined-preview policy. The fixture reproduces the reported
/// histogram facts (black 66, default white 872, tuned white 115) so both mappings are checked on the same bytes.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class StageDisplayStretchRegressionTests
{
    private const int Width = 100;
    private const int Height = 100;
    private const ushort BackgroundAdu = 66;
    private const ushort ProbeAdu = 75;

    // The installed on-demand default (Mono16DisplayStretchOptions defaults) and the installer v3 CombinedPreview
    // step (#1007) are the two policies the user-visible mismatch was observed under.
    private static readonly Mono16DisplayStretchOptions OnDemandDefault = new();
    private static readonly Mono16DisplayStretchOptions RetainedCombinedPreview = new(0.5, 0.9997, 8);

    [TestMethod]
    public void SameCombinedPixelMapsTo5UnderOnDemandDefaultAnd108UnderRetainedPolicy()
    {
        var frame = CreateCombinedFrame();
        var probe = ProbeIndex(frame.PixelData);

        var onDemand = Mono16DisplayStretch.Apply(Width, Height, frame.PixelData, options: OnDemandDefault);
        var retained = Mono16DisplayStretch.Apply(Width, Height, frame.PixelData, options: RetainedCombinedPreview);

        Assert.AreEqual(ProbeAdu, ReadSample(frame.PixelData, probe));
        Assert.AreEqual((byte)5, onDemand[probe], "on-demand default 0.5/0.9999/4 maps 75 ADU to 5/255");
        Assert.AreEqual((byte)108, retained[probe], "retained combined-preview 0.5/0.9997/8 maps 75 ADU to 108/255");
        Assert.AreEqual(BackgroundAdu, Percentile(frame.PixelData, OnDemandDefault.BlackPercentile), "black point");
        Assert.AreEqual(872, Percentile(frame.PixelData, OnDemandDefault.WhitePercentile), "default white point");
        Assert.AreEqual(115, Percentile(frame.PixelData, RetainedCombinedPreview.WhitePercentile), "tuned white point");
        Assert.AreEqual(new Mono16DisplayStretchOptions(0.5, 0.9999, 4), OnDemandDefault,
            "the global on-demand default must not change silently; a deliberate change updates this assertion");
    }

    [TestMethod]
    public void DisplayStretchIsExactlyOneNonlinearTransferAndLeavesSourceBytesUnchanged()
    {
        var frame = CreateCombinedFrame();
        var before = Sha256(frame.PixelData.Span);

        var once = Mono16DisplayStretch.Apply(Width, Height, frame.PixelData, options: RetainedCombinedPreview);
        var again = Mono16DisplayStretch.Apply(Width, Height, frame.PixelData, options: RetainedCombinedPreview);

        Assert.AreEqual(before, Sha256(frame.PixelData.Span), "the linear Combined bytes are immutable");
        CollectionAssert.AreEqual(once, again, "the retained policy reproduces the same display bytes");
        // Feeding the 8-bit display product back through the stretch would be a second transfer: the probe pixel
        // becomes the new black point and collapses to 0. The encoded-preview product is Mono8, which the preview
        // path passes through without restretching, so exactly one transfer reaches the operator.
        var probe = ProbeIndex(frame.PixelData);
        var restretched = Mono16DisplayStretch.Apply(Width, Height, Widen(once), options: RetainedCombinedPreview);
        Assert.AreEqual((byte)108, once[probe]);
        Assert.AreEqual((byte)0, restretched[probe], "a second stretch changes the displayed value");
    }

    [TestMethod]
    public void FiveIdenticalFramesAverageToTheSameFrameNotASum()
    {
        var frame = CreateCombinedFrame();
        var sources = Enumerable.Range(0, 5).Select(_ => frame).ToArray();

        var mean = Linear16ArithmeticMean.Compute(sources);

        Assert.AreEqual(5, mean.SourceCount);
        Assert.AreEqual(Sha256(frame.PixelData.Span), Sha256(mean.PixelData.Span),
            "the arithmetic mean of identical frames is the frame itself, not a 5x exposure sum");
        Assert.AreEqual(ProbeAdu, ReadSample(mean.PixelData, ProbeIndex(frame.PixelData)));
        Assert.AreEqual(
            (byte)108,
            Mono16DisplayStretch.Apply(Width, Height, mean.PixelData, options: RetainedCombinedPreview)[ProbeIndex(frame.PixelData)]);
    }

    // 10,000 nonzero samples: 9,000 at the 66 ADU background, 990 at 75 ADU, 7 at 115, 2 at 872 and one bright
    // star at 4,000. Percentile targets are ceil(n*p): 0.5 -> 5,000 (66), 0.9997 -> 9,997 (115), 0.9999 -> 9,999 (872).
    private static Linear16Frame CreateCombinedFrame()
    {
        var samples = new ushort[Width * Height];
        var index = 0;
        Fill(samples, ref index, 9_000, BackgroundAdu);
        Fill(samples, ref index, 990, ProbeAdu);
        Fill(samples, ref index, 7, 115);
        Fill(samples, ref index, 2, 872);
        Fill(samples, ref index, 1, 4_000);
        Assert.AreEqual(samples.Length, index);
        var packed = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            packed[i * 2] = (byte)(samples[i] & 0xFF);
            packed[i * 2 + 1] = (byte)(samples[i] >> 8);
        }
        return new Linear16Frame(Width, Height, Width * 2, CameraPixelFormat.Mono16, packed);
    }

    private static void Fill(ushort[] samples, ref int index, int count, ushort value)
    {
        for (var i = 0; i < count; i++)
        {
            samples[index++] = value;
        }
    }

    private static int ProbeIndex(ReadOnlyMemory<byte> pixels)
    {
        for (var i = 0; i < pixels.Length / 2; i++)
        {
            if (ReadSample(pixels, i) == ProbeAdu)
            {
                return i;
            }
        }
        throw new AssertFailedException("The fixture contains no 75 ADU probe pixel.");
    }

    private static ushort ReadSample(ReadOnlyMemory<byte> pixels, int index)
        => (ushort)(pixels.Span[index * 2] | pixels.Span[index * 2 + 1] << 8);

    private static int Percentile(ReadOnlyMemory<byte> pixels, double percentile)
    {
        var values = new List<int>();
        for (var i = 0; i < pixels.Length / 2; i++)
        {
            var sample = ReadSample(pixels, i);
            if (sample > 0)
            {
                values.Add(sample);
            }
        }
        values.Sort();
        var target = Math.Max(1, (int)Math.Ceiling(values.Count * percentile));
        return values[target - 1];
    }

    private static ReadOnlyMemory<byte> Widen(byte[] display)
    {
        // Promote each Mono8 display byte to a little-endian Mono16 sample of the same frame geometry.
        var widened = new byte[display.Length * 2];
        for (var i = 0; i < display.Length; i++)
        {
            widened[i * 2] = display[i];
        }
        return widened;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
