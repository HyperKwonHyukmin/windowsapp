namespace DeskKeeper.Tray;

static class Program
{
    [STAThread]
    static void Main()
    {
        // 상주 프로그램은 하나만 뜨게 한다
        using var mutex = new Mutex(true, @"Local\DeskKeeper.Tray", out bool createdNew);
        if (!createdNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
