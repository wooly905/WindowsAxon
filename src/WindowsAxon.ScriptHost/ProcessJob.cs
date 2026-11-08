using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsAxon.ScriptHost;

// Copied from WindowsAxon.Windows.ProcessJob; the host must not reference WindowsAxon.Windows.
internal sealed class ProcessJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private const uint KillOnJobClose = 0x2000;
    private const uint JobMemoryLimit = 0x200;
    private const uint MaxJobMemory = 1024u * 1024u * 1024u;

    private ProcessJob() : base(true)
    {
    }

    public static ProcessJob Attach(Process process)
    {
        var job = CreateJobObject(nint.Zero, null);
        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var limits = new Limits
        {
            Basic = new BasicLimits { Flags = KillOnJobClose | JobMemoryLimit },
            JobMemory = MaxJobMemory
        };
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Limits>())
            || !AssignProcessToJobObject(job,
                                         process.Handle))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            job.Dispose();
            if (!process.HasExited)
            {
                process.Kill(true);
            }

            throw error;
        }
        return job;
    }

    protected override bool ReleaseHandle() => CloseHandle(handle);

    public void AllowChildProcessesToContinue()
    {
        var limits = new Limits
        {
            Basic = new BasicLimits { Flags = JobMemoryLimit },
            JobMemory = MaxJobMemory
        };
        if (!SetInformationJobObject(this, 9, ref limits, (uint)Marshal.SizeOf<Limits>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Terminate()
    {
        if (!TerminateJobObject(this, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime;
        public long JobTime;
        public uint Flags;
        public nuint MinWorkingSet;
        public nuint MaxWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority;
        public uint Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Limits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory;
        public nuint JobMemory;
        public nuint PeakProcessMemory;
        public nuint PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ProcessJob CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(ProcessJob job, int infoClass, ref Limits limits, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(ProcessJob job, nint process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(ProcessJob job, uint exitCode);
}
