using DeskKeeper.Core;

namespace DeskKeeper.Tray;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 보내기 메뉴의 "임시 표시": 설정에 적고 바로 끝난다(상주 프로그램과 별개로 실행)
        if (args.Length > 0 && args[0] == "--mark")
        {
            MarkFiles(args[1..]);
            return;
        }

        // 상주 프로그램은 하나만 뜨게 한다
        using var mutex = new Mutex(true, @"Local\DeskKeeper.Tray", out bool createdNew);
        if (!createdNew)
            return;

        Application.Run(new TrayContext());
    }

    private static void MarkFiles(string[] paths)
    {
        var files = paths.Where(File.Exists).ToList();
        int folders = paths.Count(Directory.Exists);
        var store = new SettingsStore();
        try
        {
            store.ReloadIfChanged();
            var until = DateTime.Today.AddDays(store.Current.MarkDays);
            if (files.Count > 0)
                store.SetMarks(files, until);

            var message = files.Count > 0
                ? $"파일 {files.Count}개를 임시로 표시했습니다.\n{until:yyyy-MM-dd} 이후 정리 후보로 알려 드립니다."
                : "표시할 파일이 없습니다.";
            if (folders > 0)
                message += $"\n\n폴더 {folders}개는 건너뛰었습니다(파일만 표시할 수 있습니다).";
            MessageBox.Show(message, "DeskKeeper 임시 표시", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "DeskKeeper 임시 표시", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
