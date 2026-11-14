namespace TextFixer;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (Bridge.NativeMessagingHost.IsNativeInvocation(args))
            return Bridge.NativeMessagingHost.RunAsync(args).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        using var mutex = new Mutex(true, Bridge.BridgeProtocol.AppMutexName, out var first);
        if (!first)
        {
            var shortcut = Services.AppSettings.Load().Shortcuts[Services.Shortcuts.Open]
                ?? Services.Shortcuts.Default(Services.Shortcuts.Open)!.Value;
            using var notice = new RunningNotice(shortcut.ToString());
            notice.ShowDialog();
            return 0;
        }
        Application.Run(new TrayApplication(startHidden: args.Contains(Autostart.Argument)));
        return 0;
    }    
}
