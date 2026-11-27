using System.Drawing.Imaging;
using System.Reflection;

namespace Anchor.Screenshots;

/// <summary>
/// Opens Anchor's real tray menu and Settings window and saves them as PNGs.
/// Usage: Screenshots.exe <output folder>
///
/// Staged for the website (everything else is exactly what Anchor shows):
///  - the menu header names an example two-monitor setup instead of the CI machine's virtual monitor;
///  - "Undo last restore" is enabled, as it is right after Anchor has put windows back.
/// </summary>
static class Program
{
    const string ExampleSetup = "Samsung LC32G7xT + ASUS VG279QM";
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "screenshots");
        Directory.CreateDirectory(outDir);

        // No first-run greeting balloon in the way.
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Opifex", "Anchor");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "welcomed"), "screenshots");

        ApplicationConfiguration.Initialize();
        var app = (ApplicationContext)Activator.CreateInstance(typeof(TrayApp), nonPublic: true)!;
        int exit = 1;

        var timer = new System.Windows.Forms.Timer { Interval = 2500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Menu(app, Path.Combine(outDir, "anchor-tray-menu.png"));
                Settings(app, Path.Combine(outDir, "anchor-settings.png"));
                exit = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { Application.Exit(); }
        };
        timer.Start();
        Application.Run(app);
        return exit;
    }

    static void Menu(ApplicationContext app, string file)
    {
        var tray = (NotifyIcon)Field(app, "_tray");
        var menu = tray.ContextMenuStrip!;
        typeof(TrayApp).GetMethod("RefreshMenu", Any)!.Invoke(app, null);
        ((ToolStripMenuItem)Field(app, "_header")).Text = $"Anchor {TrayApp.Version}  ·  {ExampleSetup}";
        ((ToolStripMenuItem)Field(app, "_undo")).Enabled = true;

        // RefreshMenu runs again when the menu opens; re-apply the staging after it.
        menu.Opened += (_, _) =>
        {
            ((ToolStripMenuItem)Field(app, "_header")).Text = $"Anchor {TrayApp.Version}  ·  {ExampleSetup}";
            ((ToolStripMenuItem)Field(app, "_undo")).Enabled = true;
            menu.Invalidate();
        };
        menu.Show(new Point(200, 150));
        Pump(900);
        Save(menu.Bounds, file);
        menu.Close();
        Pump(300);
    }

    static void Settings(ApplicationContext app, string file)
    {
        typeof(TrayApp).GetMethod("ShowSettings", Any)!.Invoke(app, null);
        Pump(1200);
        var form = Application.OpenForms.OfType<Form>().First(f => f.Text == "Anchor settings");
        form.Location = new Point(60, 40);
        form.Activate();
        form.ActiveControl = null; // no focus rectangle on a checkbox
        Pump(900);
        Save(form.Bounds, file);
        form.Close();
    }

    static object Field(object o, string name) => o.GetType().GetField(name, Any)!.GetValue(o)!;

    static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); }
    }

    static void Save(Rectangle r, string file)
    {
        using var bmp = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        bmp.Save(file, ImageFormat.Png);
        Console.WriteLine($"{Path.GetFileName(file)}: {r.Width}x{r.Height}");
    }
}
