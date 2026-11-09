using System.Buffers.Binary;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class CustomScriptSyscallPolicyTests
{
    [TestMethod]
    [DataRow(Architecture.X64)]
    [DataRow(Architecture.Arm64)]
    public void BothArchitecturesPermitTheVerifiedShellOperationsAndRetainTheSecurityBoundary(Architecture architecture)
    {
        var arm = architecture == Architecture.Arm64;
        var policy = CustomScriptSyscallPolicy.Create(architecture);
        // rt_sigsuspend, wait4, getdents64, getgroups, utimensat, access/faccessat, faccessat2.
        uint[] shell = arm ? [133, 260, 61, 158, 88, 48, 439] : [130, 61, 217, 115, 280, 21, 439];
        foreach (var syscall in shell) Assert.AreEqual(0x7fff0000U, Evaluate(policy, arm, syscall), "Required shell syscall " + syscall);
        // memfd_create, unshare, mount, socket, io_uring_setup and unknown future syscall.
        uint[] denied = arm ? [279, 97, 40, 198, 425, 9999] : [319, 272, 165, 41, 425, 9999];
        foreach (var syscall in denied) Assert.AreEqual(0x00050001U, Evaluate(policy, arm, syscall), "Forbidden syscall " + syscall);
        Assert.AreEqual(0x00050001U, Evaluate(policy, arm, arm ? 222U : 9U, 32, 4), "Executable mmap remains forbidden.");
        Assert.AreEqual(0x00050001U, Evaluate(policy, arm, arm ? 220U : 56U, 16, 0x10000000), "Namespace clone remains forbidden.");
        Assert.AreEqual(0x80000000U, Evaluate(policy, !arm, 0), "A different syscall ABI must be killed.");
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => CustomScriptSyscallPolicy.Create(Architecture.X86));
    }

    private static uint Evaluate(byte[] policy, bool arm, uint syscall, int argumentOffset = 0, uint argumentValue = 0)
    {
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(data, syscall);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), arm ? 0xc00000b7U : 0xc000003eU);
        if (argumentOffset != 0) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(argumentOffset), argumentValue);
        uint accumulator = 0;
        for (var index = 0; index < policy.Length / 8; index++)
        {
            var instruction = policy.AsSpan(index * 8, 8);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(instruction[4..]);
            switch (BinaryPrimitives.ReadUInt16LittleEndian(instruction))
            {
                case 0x20: accumulator = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)value)); break;
                case 0x15: index += accumulator == value ? instruction[2] : instruction[3]; break;
                case 0x45: index += (accumulator & value) != 0 ? instruction[2] : instruction[3]; break;
                case 0x06: return value;
                default: Assert.Fail("Unsupported BPF instruction in the qualification evaluator."); break;
            }
        }
        Assert.Fail("The policy must return a bounded disposition.");
        return 0;
    }
}
