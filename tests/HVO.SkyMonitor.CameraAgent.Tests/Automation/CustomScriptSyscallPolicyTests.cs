using System.Buffers.Binary;
using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class CustomScriptSyscallPolicyTests
{
    [TestMethod]
    public void QualifiedX64PolicyPermitsTheVerifiedShellOperationsAndRetainsTheSecurityBoundary()
    {
        var policy = CustomScriptSyscallPolicy.Create(Architecture.X64);
        // rt_sigsuspend, wait4, getdents64, getgroups, utimensat, access/faccessat, faccessat2.
        uint[] shell = [130, 61, 217, 115, 280, 21, 439];
        foreach (var syscall in shell) Assert.AreEqual(0x7fff0000U, Evaluate(policy, false, syscall), "Required shell syscall " + syscall);
        // memfd_create, unshare, mount, socket, io_uring_setup and unknown future syscall.
        uint[] denied = [319, 272, 165, 41, 425, 9999];
        foreach (var syscall in denied) Assert.AreEqual(0x00050001U, Evaluate(policy, false, syscall), "Forbidden syscall " + syscall);
        Assert.AreEqual(0x00050001U, Evaluate(policy, false, 9U, 32, 4), "Executable mmap remains forbidden.");
        Assert.AreEqual(0x00050001U, Evaluate(policy, false, 56U, 16, 0x10000000), "Namespace clone remains forbidden.");
        Assert.AreEqual(0x80000000U, Evaluate(policy, true, 0), "A different syscall ABI must be killed.");
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => CustomScriptSyscallPolicy.Create(Architecture.X86));
    }

    [TestMethod]
    [DataRow(Architecture.Arm64)]
    [DataRow(Architecture.Arm)]
    [DataRow(Architecture.X86)]
    public async Task UnqualifiedArchitecturesRefuseAvailabilityPolicyAndExecution(Architecture architecture)
    {
        Assert.AreEqual("script.sandbox-architecture-unqualified", LinuxCustomScriptSandbox.ProbeUnavailableReason(architecture));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => CustomScriptSyscallPolicy.Create(architecture));
        var result = await LinuxCustomScriptSandbox.ExecuteAsync(CustomScriptTaskAdapterTests.Script("exit 42"), architecture, default).ConfigureAwait(false);
        Assert.AreEqual("isolation-unavailable", result.Status);
        Assert.IsNull(result.ExitCode);
        Assert.AreEqual(0L, result.StdoutBytes + result.StderrBytes);
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
