namespace Anchor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // "--replace <pid>": started by "Restart as administrator"; wait for the old Anchor to quit.
        if (args.Length == 2 && args[0] == "--replace" && int.TryParse(args[1], out int oldPid))
            ReplacingPid = oldPid;

        // Only one Anchor per Windows sign-in.
        using var mutex = new Mutex(false, @"Local\OpifexStudios.Anchor");
        bool owned;
        try { owned = mutex.WaitOne(ReplacingPid != 0 ? TimeSpan.FromSeconds(15) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;

        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Anchor.Core.Log.Write("Unhandled: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Anchor.Core.Log.Write("Unhandled: " + e.ExceptionObject);

            using var app = new TrayApp();
            Application.Run(app);
            return 0;
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Set when this Anchor replaced an older one (so it doesn't greet again).</summary>
    public static int ReplacingPid { get; private set; }
}
