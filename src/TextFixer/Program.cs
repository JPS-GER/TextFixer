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
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        using var mutex = new Mutex(true, Bridge.BridgeProtocol.AppMutexName, out var first);
        if (!first)
        {
            MessageBox.Show("TextFixer is already running. Use Alt+Q or its tray icon.", "TextFixer");
            return 0;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplication(startHidden: args.Contains(Autostart.Argument)));
        return 0;
    }    
}
