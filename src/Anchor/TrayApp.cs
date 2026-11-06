using System.Reflection;
using Anchor.Core;

namespace Anchor;

/// <summary>The tray icon and its menu. All the real work happens in <see cref="Engine"/>, off the UI thread.</summary>
sealed class TrayApp : ApplicationContext
{
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Opifex", "Anchor");

    readonly Engine _engine = new(Folder);
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _header, _restore, _save, _undo, _pause;
    readonly System.Windows.Forms.Timer _snapTimer;
    int _busy; // 1 while a snapshot, restore or undo is running
    bool _paused;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    public TrayApp()
    {
        Log.Write($"Anchor {Version} started");

        _header = new ToolStripMenuItem("Anchor") { Enabled = false };
        _restore = new ToolStripMenuItem("Restore now", null, async (_, _) => await RestoreNow());
        _save = new ToolStripMenuItem("Save layout", null, async (_, _) => await SaveLayout());
        _undo = new ToolStripMenuItem("Undo last restore", null, async (_, _) => await Undo()) { Enabled = false };
        _pause = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        var quit = new ToolStripMenuItem("Quit", null, (_, _) => Quit());

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _header, new ToolStripSeparator(), _restore, _save, _undo, _pause, new ToolStripSeparator(), quit,
        });
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Anchor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Left-click opens the same menu as right-click.
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(_tray, null);
        };

        _snapTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _snapTimer.Tick += async (_, _) => await AutoSnapshot();
        _snapTimer.Start();
        _ = AutoSnapshot();

        FirstRunHello();
    }

    static Icon LoadIcon()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("anchor.ico");
        return s != null ? new Icon(s, SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    void FirstRunHello()
    {
        string flag = Path.Combine(Folder, "welcomed");
        if (File.Exists(flag)) return;
        try { File.WriteAllText(flag, DateTime.Now.ToString("O")); } catch { }
        Notify("Anchor is running. It quietly remembers where your windows are. Click the anchor in the tray for options.");
    }

    async Task AutoSnapshot()
    {
        if (_paused || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            await Task.Run(() => _engine.TakeAutoSnapshot());
            UpdateTooltip();
        }
        catch (Exception ex) { Log.Write("Snapshot failed: " + ex.Message); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    async Task RestoreNow()
    {
        if (!await WaitTurn()) return;
        try
        {
            var r = await Task.Run(() => _engine.RestoreNow());
            Notify(r == null ? "Nothing to restore yet for this monitor setup." : r.Summary());
        }
        catch (Exception ex) { Log.Write("Restore failed: " + ex); Notify("Something went wrong restoring windows. Details are in Anchor's log."); }
        finally { Interlocked.Exchange(ref _busy, 0); RefreshMenu(); }
    }

    async Task SaveLayout()
    {
        if (!await WaitTurn()) return;
        try
        {
            var s = await Task.Run(() => _engine.SaveLayout());
            Notify($"Saved where your {s.Windows.Count} windows are on {_engine.CurrentSetupName}. \"Restore now\" puts them back here.");
        }
        catch (Exception ex) { Log.Write("Save failed: " + ex); Notify("Couldn't save the layout. Details are in Anchor's log."); }
        finally { Interlocked.Exchange(ref _busy, 0); RefreshMenu(); }
    }

    async Task Undo()
    {
        if (!await WaitTurn()) return;
        try
        {
            var r = await Task.Run(() => _engine.UndoLast());
            Notify(r == null ? "Nothing to undo." : r.Moved == 0 ? "Nothing to undo." :
                   r.Moved == 1 ? "Moved 1 window back to where it was." : $"Moved {r.Moved} windows back to where they were.");
        }
        catch (Exception ex) { Log.Write("Undo failed: " + ex); }
        finally { Interlocked.Exchange(ref _busy, 0); RefreshMenu(); }
    }

    /// <summary>Waits (briefly) for a running snapshot to finish so actions never overlap.</summary>
    async Task<bool> WaitTurn()
    {
        for (int i = 0; i < 50; i++)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0) return true;
            await Task.Delay(100);
        }
        return false;
    }

    void TogglePause()
    {
        _paused = !_paused;
        Log.Write(_paused ? "Paused" : "Resumed");
        RefreshMenu();
        UpdateTooltip();
    }

    void RefreshMenu()
    {
        string setup = _engine.CurrentSetupName;
        _header.Text = string.IsNullOrEmpty(setup) ? $"Anchor {Version}" : $"Anchor {Version}  ·  {setup}";
        _undo.Enabled = _engine.LastUndo != null;
        _pause.Text = _paused ? "Resume" : "Pause";
        _pause.Checked = _paused;
        _restore.ToolTipText = "Puts windows back to your saved layout for these monitors, or to the last snapshot if you haven't saved one.";
        _save.ToolTipText = "Remembers where every window is right now, for these monitors.";
    }

    void UpdateTooltip()
    {
        string text = _paused ? "Anchor (paused)" :
            $"Anchor: watching {_engine.LastWindowCount} windows on {Plural(_engine.CurrentMonitorCount, "monitor")}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    static string Plural(int n, string w) => n == 1 ? $"1 {w}" : $"{n} {w}s";

    void Notify(string text)
    {
        Log.Write("Note: " + text);
        _tray.BalloonTipTitle = "Anchor";
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(5000);
    }

    void Quit()
    {
        Log.Write("Quit");
        _snapTimer.Stop();
        _engine.Flush();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _snapTimer.Dispose(); _tray.Dispose(); }
        base.Dispose(disposing);
    }
}
