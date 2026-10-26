using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LexExport;

// How much of the machine an export may use. One core always stays free, so the PC stays usable
// while players extract: the process's affinity leaves out core 0 (which covers every thread,
// LEX's included), and our own Parallel loops run at most that many at once. --background (the
// game's extraction while it's being played) takes half the cores at below-normal priority.
//
// Windows 11 may also throttle a process it considers in the background (EcoQoS: efficiency
// cores, lower clocks), which a console tool started by the game can look like. A foreground
// export opts out; --background opts in.
public static class ParallelLimits
{
    public static ParallelOptions Options { get; private set; } = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };

    public static int Cores => Options.MaxDegreeOfParallelism;

    public static void Apply(bool background)
    {
        int total = Environment.ProcessorCount;
        int cores = background ? Math.Max(1, total / 2) : Math.Max(1, total - 1);
        Options = new ParallelOptions { MaxDegreeOfParallelism = cores };
        using Process self = Process.GetCurrentProcess();
        if (background)
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        SetPowerThrottling(self, background);
        // Affinity masks cover 64 processors; past that (or on one core) leave it to the OS.
        if (total < 2 || total > 64)
            return;
        // The highest cores, leaving core 0 and the ones below them to everything else.
        long mask = 0;
        for (int i = total - cores; i < total; i++)
            mask |= 1L << i;
        try
        {
            self.ProcessorAffinity = (IntPtr)mask;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not limit the extractor to {cores} cores: {ex.Message}");
        }
    }

    // ProcessPowerThrottling with EXECUTION_SPEED in its control mask: on in the state mask turns
    // EcoQoS on, off opts out of it. Windows 10 before 1709 doesn't know it; nothing changes there.
    private static void SetPowerThrottling(Process process, bool throttled)
    {
        var state = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = ExecutionSpeed,
            StateMask = throttled ? ExecutionSpeed : 0,
        };
        if (!SetProcessInformation(process.Handle, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>()))
            Console.Error.WriteLine($"Could not set the extractor's power throttling (error {Marshal.GetLastWin32Error()}).");
    }

    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);
}
