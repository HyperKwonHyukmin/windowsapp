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

    /// <summary>분석 창이 닫혔을 때(파일을 지웠을 수 있으니 정리 후보를 다시 센다). 스레드 풀에서 불린다.</summary>
    public static event Action? AnalyzerExited;

    /// <summary>
    /// 분석 프로그램을 따로 실행한다(기다리지 않고 종료 알림만 받는다).
    /// folder 가 없으면 폴더 선택 창부터, expired 면 정리 후보만 모아 연다.
    /// </summary>
    public static void StartAnalyzer(string? folder, bool expired = false)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "DeskKeeper.Analyzer.exe");
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (expired)
                psi.ArgumentList.Add("--expired");
            else if (folder is not null)
                psi.ArgumentList.Add(folder);
            var process = Process.Start(psi);
            if (process is null)
                return;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                process.Dispose();
                AnalyzerExited?.Invoke();
            };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show($"분석 프로그램을 실행하지 못했습니다.\n{exe}\n\n{ex.Message}",
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
