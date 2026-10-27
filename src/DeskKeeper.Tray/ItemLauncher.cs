using System.ComponentModel;
using System.Diagnostics;
using DeskKeeper.Core;

namespace DeskKeeper.Tray;

/// <summary>런처 항목 실행과 탐색기에서 위치 열기.</summary>
static class ItemLauncher
{
    public static void Start(LauncherItem item)
    {
        try
        {
            var psi = new ProcessStartInfo(item.Path)
            {
                // 셸 실행: exe·폴더·문서·바로가기(.lnk)·URL을 모두 연결 프로그램으로 연다
                UseShellExecute = true,
                Arguments = item.Arguments ?? "",
                // 작업 폴더를 지정하지 않으면 프로그램이 있는 폴더(해석 도구가 상대 경로를 쓰는 경우 대비)
                WorkingDirectory = item.WorkingDirectory ?? DefaultWorkingDirectory(item.Path),
            };
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"실행하지 못했습니다.\n{item.Path}\n\n{ex.Message}",
                "DeskKeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public static void RevealInExplorer(string path)
    {
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
    }

    private static string DefaultWorkingDirectory(string path) =>
        File.Exists(path) ? Path.GetDirectoryName(path) ?? "" : "";
}
