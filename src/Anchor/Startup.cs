using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Anchor.Core;
using Microsoft.Win32;

namespace Anchor;

/// <summary>
/// Starting with Windows. Normally a per-user "Run" entry (no admin needed).
/// "Start as admin" uses a scheduled task instead, because Windows won't elevate Run entries.
/// </summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Anchor";
    public const string TaskName = @"Opifex\Anchor";

    static string ExePath => Environment.ProcessPath!;

    /// <summary>Makes the Run entry match the settings (called at every start, so it follows the exe if it moved).</summary>
    public static void Apply(AppSettings s)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            bool wantRun = s.StartWithWindows && !s.StartAsAdmin;
            string value = $"\"{ExePath}\"";
            if (wantRun)
            {
                if (key.GetValue(RunValue) as string != value) key.SetValue(RunValue, value);
            }
            else if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue, false);
        }
        catch (Exception ex) { Log.Write("Couldn't update start-with-Windows: " + ex.Message); }
    }

    public static bool AdminTaskExists()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Creates or removes the "start as admin at sign-in" task. Shows a Windows admin prompt.
    /// Returns false if the person said no to the prompt or it failed.</summary>
    public static bool SetAdminTask(bool on)
    {
        string args;
        string? xmlPath = null;
        if (on)
        {
            xmlPath = Path.Combine(Path.GetTempPath(), "anchor-task.xml");
            File.WriteAllText(xmlPath, TaskXml(), Encoding.Unicode);
            args = $"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f";
        }
        else args = $"/delete /tn \"{TaskName}\" /f";

        try
        {
            var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit(15000);
            Log.Write($"Admin start task {(on ? "created" : "removed")}: exit {p.ExitCode}");
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log.Write("Admin prompt declined"); return false; }
        catch (Exception ex) { Log.Write("Admin task change failed: " + ex.Message); return false; }
        finally { if (xmlPath != null) try { File.Delete(xmlPath); } catch { } }
    }

    static string TaskXml()
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string esc(string s) => System.Security.SecurityElement.Escape(s)!;
        return $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Starts Anchor by Opifex Studios as administrator when you sign in.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{esc(user)}</UserId></LogonTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>{esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author"><Exec><Command>{esc(ExePath)}</Command></Exec></Actions>
</Task>
""";
    }

    /// <summary>Starts a new Anchor as administrator, which takes over from this one. False if declined.</summary>
    public static bool RelaunchAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath, $"--replace {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; }
        catch (Exception ex) { Log.Write("Relaunch as admin failed: " + ex.Message); return false; }
    }
}
