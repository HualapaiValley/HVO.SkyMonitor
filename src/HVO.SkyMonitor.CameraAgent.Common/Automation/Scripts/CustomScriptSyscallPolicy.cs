using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

/// <summary>
/// Applied by Bubblewrap after namespace setup and before the static interpreter executes. Native
/// code is also subject to this allowlist: no namespaces/mounts, sockets, anonymous files, SysV IPC,
/// io_uring, keys, tracing, or new executable mappings. Unknown syscalls fail closed.
/// </summary>
internal static class CustomScriptSyscallPolicy
{
    internal static byte[] Create() => Create(RuntimeInformation.ProcessArchitecture);

    internal static byte[] Create(Architecture architecture)
    {
        if (architecture != Architecture.X64) throw new PlatformNotSupportedException();
        var instructions = new List<(ushort Code, byte True, byte False, uint Value)>();
        Load(4); // seccomp_data.arch; reject compatibility ABIs and x32 syscall numbers.
        Jump(0xc000003eU, 1, 0);
        Return(0x80000000); // SECCOMP_RET_KILL_PROCESS
        Load(0);
        // clone3 must return ENOSYS for libc's ordinary fork fallback. clone is limited to no namespaces.
        Jump(435, 0, 1);
        Return(0x00050026);
        Jump(56U, 0, 4);
        Load(16);
        instructions.Add((0x45, 0, 1, 0x7e020080)); // CLONE_NEW* and CLONE_NEWTIME
        Return(0x00050001);
        Return(0x7fff0000);
        foreach (var number in new[] { 9U, 10U })
        {
            Jump(number, 0, 4);
            Load(32); // mmap/mprotect protection argument (args[2]).
            instructions.Add((0x45, 0, 1, 4)); // PROT_EXEC
            Return(0x00050001);
            Return(0x7fff0000);
        }
        // Static busybox ash/sleep and bounded POSIX file operations. No executable mmap is needed.
        uint[] allowed = [0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25,
                26, 27, 28, 32, 33, 34, 35, 39, 57, 58, 59, 60, 61, 62, 63, 72, 73, 74, 75, 76, 77, 78,
                79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 95, 96, 97, 98, 99, 100, 102, 104,
                107, 108, 110, 111, 112, 131, 137, 138, 158, 186, 201, 202, 204, 218, 219, 228, 230, 231,
                234, 257, 258, 262, 263, 264, 267, 269, 270, 271, 273, 280, 281, 292, 293, 302, 318, 332, 334, 439];
        // Verified against a trace of the shipped static BusyBox: ash wait uses rt_sigsuspend,
        // glob uses getdents64, test -r uses getgroups, and touch uses utimensat. Only the qualified
        // x86-64 ABI is accepted; other architectures cannot generate or execute this policy.
        uint[] shellFeatures = [130, 217, 115, 280];
        foreach (var number in allowed.Concat(shellFeatures).Distinct())
        {
            Jump(number, 0, 1);
            Return(0x7fff0000);
        }
        Return(0x00050001); // EPERM, including memfd_create, unshare, mount, socket and unknown syscalls.
        var bytes = new byte[instructions.Count * 8];
        for (var index = 0; index < instructions.Count; index++)
        {
            var item = instructions[index];
            var span = bytes.AsSpan(index * 8, 8);
            BinaryPrimitives.WriteUInt16LittleEndian(span, item.Code);
            span[2] = item.True;
            span[3] = item.False;
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], item.Value);
        }
        return bytes;

        void Load(uint offset) => instructions.Add((0x20, 0, 0, offset));
        void Jump(uint value, byte yes, byte no) => instructions.Add((0x15, yes, no, value));
        void Return(uint value) => instructions.Add((0x06, 0, 0, value));
    }
}
