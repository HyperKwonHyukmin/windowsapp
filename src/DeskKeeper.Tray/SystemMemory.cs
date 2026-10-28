using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DeskKeeper.Tray;

/// <summary>
/// 시스템 메모리 사용률(GlobalMemoryStatusEx 한 번 호출 — 프로세스 목록을 읽지 않아 상주 부담이 없다).
/// 원인 찾기와 끄기는 SessionBoard 메모리 탭에 맡긴다.
/// </summary>
static class SystemMemory
{
    public const string SessionBoardUrl = "http://127.0.0.1:4777/";

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>물리 메모리 사용률(%)과 전체·사용 중 GB. 실패하면 null.</summary>
    public static (int Percent, double UsedGb, double TotalGb)? Read()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref s))
            return null;
        const double Gb = 1024.0 * 1024 * 1024;
        return ((int)s.MemoryLoad, (s.TotalPhys - s.AvailPhys) / Gb, s.TotalPhys / Gb);
    }

    /// <summary>SessionBoard 서버가 켜져 있는지(포트에 짧게 접속해 본다).</summary>
    public static bool IsSessionBoardRunning()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", 4777).Wait(300) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException)
        {
            return false;
        }
    }
}
