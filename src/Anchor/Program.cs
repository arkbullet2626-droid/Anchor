namespace Anchor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Only one Anchor per Windows sign-in.
        using var mutex = new Mutex(true, @"Local\OpifexStudios.Anchor", out bool first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Anchor.Core.Log.Write("Unhandled: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Anchor.Core.Log.Write("Unhandled: " + e.ExceptionObject);

        using var app = new TrayApp();
        Application.Run(app);
        return 0;
    }
}
