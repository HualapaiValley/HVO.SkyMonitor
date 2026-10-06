using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class FixedDisplayTransferEquivalenceTests
{
    [TestMethod]
    [DataRow(129, 33, false, 0)]
    [DataRow(131, 35, true, 8)]
    [DataRow(257, 65, true, 32)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded pixel fixtures are reproducible numerical test data, not security material.")]
    public void BoundedTransferMatchesScientificReconstructionExactly(int width, int height, bool color, int padding)
    {
        var stride = width * 2 + padding;
        var raw = new byte[stride * height];
        var random = new Random(1130);
        var linear = new double[width * height];
        FixedDisplayTransferOptions[] policies = [new(), new(0, 65535, 1), new(100, 30000, .1), new(128, 256.125, 10), new(64.125, 4095, 2.2)];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(y * stride + x * 2), (ushort)random.Next(65536));
        var layout = new FrameLayoutDescriptor(width, height, stride, color ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned,
            color ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None, 0, 65535, raw.Length);
        foreach (var policy in policies)
        {
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    linear[y * width + x] = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(y * stride + x * 2)) - policy.BlackLevel;
            var reference = new byte[linear.Length * (color ? 3 : 1)];
            if (color)
            {
                var reconstructed = LinearBayerReconstruction.Reconstruct(linear, Enumerable.Repeat(true, linear.Length).ToArray(), width, height);
                for (var index = 0; index < linear.Length; index++)
                {
                    reference[index * 3] = Transfer(reconstructed.Red.Span[index], policy);
                    reference[index * 3 + 1] = Transfer(reconstructed.Green.Span[index], policy);
                    reference[index * 3 + 2] = Transfer(reconstructed.Blue.Span[index], policy);
                }
            }
            else
                for (var index = 0; index < linear.Length; index++) reference[index] = Transfer(linear[index], policy);
            CollectionAssert.AreEqual(reference, FixedDisplayTransfer.Apply(layout, raw, policy));
        }
    }

    private static byte Transfer(double value, FixedDisplayTransferOptions policy)
        => (byte)Math.Round(255 * Math.Pow(Math.Clamp(value / (policy.WhiteLevel - policy.BlackLevel), 0, 1), 1 / policy.Gamma));
}
