namespace DeskKeeper.Analyzer;

static class Program
{
    /// <summary>
    /// 사용법: DeskKeeper.Analyzer.exe [폴더 또는 파일 경로] | --expired
    /// 파일이면 그 파일이 있는 폴더를, 경로가 없으면 폴더 선택 창을, --expired 면 정리 후보만 연다.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Contains("--expired"))
        {
            Application.Run(new AnalyzerForm(null));
            return;
        }

        // 보내기 메뉴로 여러 개를 넘기면 첫 번째만 쓴다
        var folder = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (folder is not null && File.Exists(folder))
            folder = Path.GetDirectoryName(folder);
        if (folder is null || !Directory.Exists(folder))
        {
            folder = AnalyzerForm.PickFolder(null, folder);
            if (folder is null)
                return;
        }
        Application.Run(new AnalyzerForm(folder));
    }
}
