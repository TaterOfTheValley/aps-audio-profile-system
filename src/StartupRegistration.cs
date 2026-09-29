using Microsoft.Win32;

namespace APS;

/// <summary>
/// "Start with Windows", via the per-user Run key.
///
/// HKCU rather than HKLM deliberately: no elevation, no installer, and the entry
/// shows up in Task Manager's Startup tab where people already look for this.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Windows records a Task Manager "Disable" here rather than deleting the Run
    /// value. Without reading it, our checkbox would claim the app starts with
    /// Windows while Windows quietly refuses to launch it.
    /// </summary>
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public enum State
    {
        Off,

        /// <summary>Registered and Windows will honour it.</summary>
        On,

        /// <summary>Registered, but switched off in Task Manager's Startup tab. Only
        /// the user can re-enable it there — writing the Run key again does nothing.</summary>
        BlockedByWindows
    }

    /// <summary>Full path of the running executable, quoted for the Run key.</summary>
    private static string CommandLine
    {
        get
        {
            string path = Environment.ProcessPath ?? Application.ExecutablePath;
            return $"\"{path}\"";
        }
    }

    public static State Current
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                if (run?.GetValue(AppInfo.StartupValueName) is not string value) return State.Off;

                // A stale entry pointing at a previous location is not "on" in any
                // useful sense — it would launch the wrong file, or nothing.
                if (!PathMatches(value)) return State.Off;

                return IsDisabledInTaskManager() ? State.BlockedByWindows : State.On;
            }
            catch
            {
                return State.Off;
            }
        }
    }

    public static bool IsEnabled => Current == State.On;

    /// <summary>Adds or removes the Run entry. Returns false with a reason if the
    /// registry refused — a locked-down machine should say so, not fail silently.</summary>
    public static bool Set(bool enabled, out string error)
    {
        error = string.Empty;
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (run == null)
            {
                error = "Windows would not open the startup registry key.";
                return false;
            }

            if (enabled) run.SetValue(AppInfo.StartupValueName, CommandLine, RegistryValueKind.String);
            else run.DeleteValue(AppInfo.StartupValueName, throwOnMissingValue: false);

            RememberIntent(enabled);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Removes the launch entry during uninstall, retaining the user's
    /// preference if they later reinstall.</summary>
    public static void RemoveForUninstall()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(AppInfo.StartupValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Uninstall still proceeds if a policy has locked this registry key.
        }
    }

    private const string SettingsKey = @"Software\APS";
    private const string IntentValue = "StartWithWindows";

    private static void RememberIntent(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
            key?.SetValue(IntentValue, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // The Run key is still the thing that matters; this only helps repair it.
        }
    }

    /// <summary>What the user last chose, independent of whether the Run entry
    /// survived. Null when they have never chosen.</summary>
    private static bool? Intent
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
                return key?.GetValue(IntentValue) is int v ? v != 0 : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Makes the Run entry match what the user asked for. Called at every startup, so:
    ///
    ///  - moving the exe re-points a now-stale path the first time it is launched
    ///  - an entry removed by a cleanup tool comes back
    ///  - an explicit "off" is never undone, because intent is stored separately
    ///
    /// A Task Manager disable is deliberately left alone: rewriting the Run key would
    /// not re-enable it, and would leave the UI claiming something untrue.
    /// </summary>
    public static void Reconcile()
    {
        try
        {
            if (Intent != true) return;
            if (IsDisabledInTaskManager()) return;

            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run == null) return;

            if (run.GetValue(AppInfo.StartupValueName) is string value && PathMatches(value)) return;

            run.SetValue(AppInfo.StartupValueName, CommandLine, RegistryValueKind.String);
        }
        catch
        {
            // Best effort. A failure here only means the user has to re-tick the box.
        }
    }

    private static bool PathMatches(string registryValue)
    {
        string current = Environment.ProcessPath ?? Application.ExecutablePath;
        string stored = registryValue.Trim().Trim('"');
        return string.Equals(
            Path.GetFullPath(stored), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// StartupApproved holds a 12-byte blob per entry; byte 0 is the flag. Even values
    /// (2, 6) mean enabled, odd (3, 7) mean the user disabled it in Task Manager.
    /// </summary>
    private static bool IsDisabledInTaskManager()
    {
        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (approved?.GetValue(AppInfo.StartupValueName) is not byte[] blob || blob.Length == 0)
            {
                return false;
            }

            return (blob[0] & 1) != 0;
        }
        catch
        {
            return false;
        }
    }
}
