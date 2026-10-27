using System.Runtime;
using System.Runtime.InteropServices;

namespace DeskKeeper.Tray;

/// <summary>유휴 상태에서 GC 후 작업 메모리를 운영체제에 돌려준다.</summary>
static class MemoryTrimmer
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        EmptyWorkingSet(self.Handle);
    }
}
