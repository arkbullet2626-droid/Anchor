using System.Reflection;
using Anchor.Core;
using WindowList = Anchor.Core.Windows;

namespace Anchor;

/// <summary>The tray icon and its menu. Timing decisions live in <see cref="Watcher"/>; the work happens
/// in <see cref="Engine"/>, off the UI thread.</summary>
sealed class TrayApp : ApplicationContext
{
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Opifex", "Anchor");

    readonly Engine _engine = new(Folder);
    readonly Watcher _watcher = new(() => DateTime.UtcNow);
    readonly SystemEvents _events = new();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _header, _restore, _save, _undo, _pause;
    readonly System.Windows.Forms.Timer _tick;
    int _busy; // 1 while a snapshot, restore or undo is running
    DateTime _lastSnapshotUtc = DateTime.UtcNow;
    RestoreResult? _autoResult; // first auto pass, combined with the follow-up for one message

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

        _events.DisplayChanged += why => _watcher.Disturb(why);
        _events.Sleeping += _watcher.Sleep;
        _events.Woke += _watcher.Wake;
        _events.Locked += _watcher.Lock;
        _events.Unlocked += _watcher.Unlock;
        _events.ScreenOff += _watcher.ScreenOff;
        _events.ScreenOn += _watcher.ScreenOn;

        _tick = new System.Windows.Forms.Timer { Interval = 1000 };
        _tick.Tick += async (_, _) => await OnTick();
        _tick.Start();

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
        Notify("Anchor is running. It remembers where your windows are and puts them back when Windows scrambles them.");
    }

    async Task OnTick()
    {
        if (Volatile.Read(ref _busy) != 0) return;
        Watcher.Action action;
        try
        {
            string sig = Geometry.Signature(Monitors.GetAll());
            bool holdOff = _watcher.State == Watcher.Phase.Disturbed && WindowList.FullscreenAppInFront();
            action = _watcher.Tick(sig, holdOff);
        }
        catch (Exception ex) { Log.Write("Tick failed: " + ex.Message); return; }
        if (action == Watcher.Action.None) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;

        try
        {
            switch (action)
            {
                case Watcher.Action.Snapshot:
                    var now = DateTime.UtcNow;
                    double calm = (now - _lastSnapshotUtc).TotalSeconds;
                    _lastSnapshotUtc = now;
                    await Task.Run(() => _engine.TakeAutoSnapshot(calm));
                    UpdateTooltip();
                    break;

                case Watcher.Action.Restore:
                {
                    string reason = _watcher.LastReason;
                    var r = await Task.Run(() => _engine.AutoRestore(followUp: false, out var why) is { } x ? x : Skip(why));
                    _autoResult = r;
                    if (r == null) Log.Write($"No automatic restore after \"{reason}\"");
                    break;
                }

                case Watcher.Action.FollowUp:
                {
                    var r = await Task.Run(() => _engine.AutoRestore(followUp: true, out var why) is { } x ? x : Skip(why));
                    int moved = (_autoResult?.Moved ?? 0) + (r?.Moved ?? 0);
                    if (moved > 0)
                    {
                        var combined = new RestoreResult { Moved = moved };
                        foreach (var s in (_autoResult?.SkippedAdmin ?? new()).Concat(r?.SkippedAdmin ?? new()).Distinct())
                            combined.SkippedAdmin.Add(s);
                        Notify(combined.Summary() + " Undo is in the menu if you didn't want that.");
                    }
                    _autoResult = null;
                    _lastSnapshotUtc = DateTime.UtcNow;
                    UpdateTooltip();
                    break;
                }
            }
        }
        catch (Exception ex) { Log.Write($"{action} failed: {ex}"); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    static RestoreResult? Skip(string why)
    {
        if (!string.IsNullOrEmpty(why)) Log.Write("Skipped automatic restore: " + why);
        return null;
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
            Notify(r == null || r.Moved == 0 ? "Nothing to undo." :
                   r.Moved == 1 ? "Moved 1 window back to where it was." : $"Moved {r.Moved} windows back to where they were.");
        }
        catch (Exception ex) { Log.Write("Undo failed: " + ex); }
        finally { Interlocked.Exchange(ref _busy, 0); RefreshMenu(); }
    }

    /// <summary>Waits (briefly) for a running snapshot to finish so actions never overlap.</summary>
    async Task<bool> WaitTurn()
    {
        for (int i = 0; i < 100; i++)
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0) return true;
            await Task.Delay(100);
        }
        return false;
    }

    void TogglePause()
    {
        _watcher.Paused = !_watcher.Paused;
        Log.Write(_watcher.Paused ? "Paused" : "Resumed");
        _lastSnapshotUtc = DateTime.UtcNow;
        RefreshMenu();
        UpdateTooltip();
    }

    void RefreshMenu()
    {
        string setup = _engine.CurrentSetupName;
        _header.Text = string.IsNullOrEmpty(setup) ? $"Anchor {Version}" : $"Anchor {Version}  ·  {setup}";
        _undo.Enabled = _engine.LastUndo != null;
        _pause.Text = _watcher.Paused ? "Resume" : "Pause";
        _pause.Checked = _watcher.Paused;
        _restore.ToolTipText = "Puts windows back to your saved layout for these monitors, or to the last snapshot if you haven't saved one.";
        _save.ToolTipText = "Remembers where every window is right now, for these monitors.";
    }

    void UpdateTooltip()
    {
        string text = _watcher.Paused ? "Anchor (paused)" :
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
        _tick.Stop();
        _engine.Flush();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _events.Dispose(); _tray.Dispose(); }
        base.Dispose(disposing);
    }
}
