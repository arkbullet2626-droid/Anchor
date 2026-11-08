using System.Diagnostics;
using Anchor.Core;

namespace Anchor;

/// <summary>A small, plain settings window.</summary>
sealed class SettingsForm : Form
{
    readonly AppSettings _s;
    readonly string _folder;
    readonly Action _saved;
    readonly CheckBox _startup, _admin, _notes, _updates;
    bool _loading = true;

    public SettingsForm(AppSettings settings, string folder, string version, Icon icon, Action saved, Action relaunched)
    {
        _s = settings; _folder = folder; _saved = saved;

        Text = "Anchor settings";
        Icon = icon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9.5f);
        Padding = new Padding(18, 14, 18, 14);
        ShowInTaskbar = true;

        var col = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
        };
        Controls.Add(col);

        Label Heading(string t) => new()
        {
            Text = t, AutoSize = true, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4),
        };
        Label Note(string t) => new()
        {
            Text = t, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(20, 0, 0, 8),
        };
        CheckBox Check(string t, bool v) => new() { Text = t, Checked = v, AutoSize = true, Margin = new Padding(0, 4, 0, 2) };

        col.Controls.Add(new Label
        {
            Text = $"Anchor {version}", AutoSize = true, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2),
        });
        col.Controls.Add(new Label
        {
            Text = "by Opifex Studios. Your windows stay anchored where you put them.", AutoSize = true, ForeColor = SystemColors.GrayText,
        });

        col.Controls.Add(Heading("General"));
        col.Controls.Add(_startup = Check("Start Anchor when Windows starts", _s.StartWithWindows));
        col.Controls.Add(Note("Recommended. Anchor can only put windows back while it's running."));
        col.Controls.Add(_notes = Check("Show a note when windows are put back", _s.ShowRestoreNotes));
        col.Controls.Add(Note("Turn off to have Anchor work silently."));

        col.Controls.Add(Heading("Admin windows"));
        col.Controls.Add(Note(
            "Windows doesn't let normal apps move windows of programs running as administrator " +
            "(like Task Manager). Anchor skips those unless it runs as administrator too.") .With(l => l.Margin = new Padding(0, 0, 0, 6)));
        bool elevated = global::Anchor.Core.Windows.SelfIsAdmin;
        col.Controls.Add(_admin = Check("Always start Anchor as administrator", _s.StartAsAdmin));
        col.Controls.Add(Note("Windows will ask for permission once, when you turn this on or off."));
        var relaunch = new Button
        {
            Text = elevated ? "Anchor is running as administrator" : "Restart Anchor as administrator now",
            AutoSize = true, Enabled = !elevated, Margin = new Padding(0, 2, 0, 4), Padding = new Padding(6, 2, 6, 2),
        };
        relaunch.Click += (_, _) =>
        {
            if (Startup.RelaunchAsAdmin()) { Close(); relaunched(); }
        };
        col.Controls.Add(relaunch);

        col.Controls.Add(Heading("Privacy"));
        col.Controls.Add(_updates = Check("Check for updates once a day", _s.CheckForUpdates));
        col.Controls.Add(Note(
            "The only thing Anchor ever does online. It asks GitHub for Anchor's newest version number and sends nothing about you. " +
            "Your window layouts never leave this PC."));

        var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 10, 0, 0), WrapContents = false };
        LinkLabel Link(string t, Action a)
        {
            var l = new LinkLabel { Text = t, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
            l.LinkClicked += (_, _) => a();
            return l;
        }
        links.Controls.Add(Link("Open Anchor's folder", () => Open(_folder)));
        links.Controls.Add(Link("opifexstudios.ca", () => Open("https://opifexstudios.ca")));
        links.Controls.Add(Link("Source code", () => Open("https://github.com/arkbullet2626-droid/Anchor")));
        col.Controls.Add(links);

        var close = new Button { Text = "Close", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0), Padding = new Padding(10, 2, 10, 2) };
        close.Click += (_, _) => Close();
        col.Controls.Add(close);
        AcceptButton = CancelButton = close;

        _startup.CheckedChanged += (_, _) => Changed();
        _notes.CheckedChanged += (_, _) => Changed();
        _updates.CheckedChanged += (_, _) => Changed();
        _admin.CheckedChanged += (_, _) => AdminChanged();
        _loading = false;
    }

    void Changed()
    {
        if (_loading) return;
        _s.StartWithWindows = _startup.Checked;
        _s.ShowRestoreNotes = _notes.Checked;
        _s.CheckForUpdates = _updates.Checked;
        if (!_s.StartWithWindows && _s.StartAsAdmin)
        {
            // Turning off start-with-Windows also removes the admin start task.
            if (Startup.SetAdminTask(false)) { _s.StartAsAdmin = false; Silently(() => _admin.Checked = false); }
        }
        Commit();
    }

    void AdminChanged()
    {
        if (_loading) return;
        bool want = _admin.Checked;
        if (Startup.SetAdminTask(want))
        {
            _s.StartAsAdmin = want;
            if (want && !_s.StartWithWindows) { _s.StartWithWindows = true; Silently(() => _startup.Checked = true); }
            Commit();
        }
        else Silently(() => _admin.Checked = !want); // said no to the Windows prompt: undo the tick
    }

    void Commit()
    {
        _s.Save(_folder);
        Startup.Apply(_s);
        _saved();
        Log.Write($"Settings: startup={_s.StartWithWindows} admin={_s.StartAsAdmin} notes={_s.ShowRestoreNotes} updates={_s.CheckForUpdates}");
    }

    void Silently(Action a) { _loading = true; try { a(); } finally { _loading = false; } }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}

static class ControlExtensions
{
    public static T With<T>(this T c, Action<T> a) where T : Control { a(c); return c; }
}
