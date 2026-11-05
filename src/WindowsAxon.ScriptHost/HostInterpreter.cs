using System.Diagnostics;
using System.Text;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WindowsAxon.ScriptHost;

internal static class HostInterpreter
{
    internal static async Task<string> RunAsync(ProcessStartInfo start, string input,
                                                CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {start.FileName}.");
        using var job = ProcessJob.Attach(process);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                job?.Terminate();
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The process may exit before cancellation reaches it; keep the original cancellation outcome.
            }
        });
        try
        {
            var output = Capture(process.StandardOutput, cancellationToken);
            var error = Capture(process.StandardError, cancellationToken);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken);
            var text = await output;
            var errors = await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{start.FileName} exited with {process.ExitCode}: {errors} {text}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            // A successful script may have launched an application that should outlive the script.
            job?.AllowChildProcessesToContinue();
            cancellationToken.ThrowIfCancellationRequested();
            return (text + (errors.Length > 0 ? "\n" + errors : "")).Trim();
        }
        catch
        {
            job.Terminate();
            throw;
        }
    }

    private static async Task<string> Capture(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        var cut = false;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            var remaining = 1024 * 1024 - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(count, remaining));
            }

            if (count > remaining)
            {
                cut = true;
            }
        }
        return result + (cut ? "\n(output truncated at 1 MB)" : "");
    }
}

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
