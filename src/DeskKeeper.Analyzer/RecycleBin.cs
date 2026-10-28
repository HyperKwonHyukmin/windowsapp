using System.Runtime.InteropServices;

namespace DeskKeeper.Analyzer;

/// <summary>
/// 파일을 휴지통으로 보낸다. 휴지통에 못 들어가는 파일(네트워크 드라이브, 휴지통보다 큰 파일)은
/// 영구 삭제 전에 Windows 가 반드시 다시 묻게 한다(FOF_WANTNUKEWARNING).
/// </summary>
static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    /// <summary>한 번의 셸 작업으로 보낸다. 사용자가 중간에 취소했으면 false.</summary>
    public static bool Send(IWin32Window owner, IEnumerable<string> paths)
    {
        var op = new ShFileOpStruct
        {
            Hwnd = owner.Handle,
            Func = FoDelete,
            // 경로 목록은 \0 으로 구분하고 \0\0 으로 끝난다
            From = string.Join('\0', paths) + "\0\0",
            // 일반 확인 창은 우리가 먼저 띄웠으므로 생략하되, 영구 삭제 경고는 남긴다
            Flags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning,
        };
        int result = SHFileOperation(ref op);
        return result == 0 && !op.AnyOperationsAborted;
    }
}
