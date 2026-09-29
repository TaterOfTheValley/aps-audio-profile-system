using System.Text;
using Microsoft.Win32;

namespace APS;

/// <summary>
/// First-launch environment checks.
///
/// The .NET runtime is not checked here — if it were missing this code would
/// never run, and the apphost already shows its own dialog with a download link.
/// What is worth checking is what the apphost cannot see: whether the audio
/// interop marshals correctly on this platform, whether Windows reports any
/// endpoints, whether the undocumented call that sets a default device is
/// available at all, and whether profiles can actually be saved where the app
/// was put.
///
/// A healthy machine sees nothing. An "everything is fine" splash on first
/// launch is noise.
/// </summary>
internal static class Preflight
{
    internal sealed record Check(string Name, bool Passed, bool Fatal, string Detail);

    private const string SettingsKey = @"Software\APS";
    private const string StartupInitialisedValue = "StartupInitialised";

    /// <summary>
    /// True the first time this user ever runs the app. Used to apply first-run
    /// defaults exactly once, so a later "no thanks" is never undone.
    /// </summary>
    public static bool IsFirstRun
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
                return key?.GetValue(StartupInitialisedValue) == null;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void MarkFirstRunComplete()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
            key?.SetValue(StartupInitialisedValue, 1, RegistryValueKind.DWord);
        }
        catch
        {
            // If this cannot be written the defaults are simply applied again next
            // launch — annoying, never harmful.
        }
    }

    public static List<Check> Run()
    {
        var results = new List<Check>();

        // --- Windows version.
        var os = Environment.OSVersion.Version;
        bool modernEnough = os.Major > 10 || (os.Major == 10 && os.Build >= 10240);
        results.Add(new Check(
            "Windows version",
            modernEnough,
            Fatal: os.Major < 6,
            modernEnough
                ? $"Windows {os.Major}.{os.Minor} build {os.Build}"
                : $"Build {os.Build}. Windows 10 or later is recommended."));

        // --- Struct layout. A mismatch here corrupts memory rather than returning
        // an error, so it is worth failing loudly instead of misbehaving quietly.
        bool layoutOk = true;
        string layoutDetail = "Audio API structures match this platform";
        try
        {
            AudioInterop.AssertLayout();
        }
        catch (Exception ex)
        {
            layoutOk = false;
            layoutDetail = ex.Message;
        }
        results.Add(new Check("Audio API compatibility", layoutOk, Fatal: true, layoutDetail));

        // --- Can we see any endpoints at all?
        int render = 0, capture = 0, activeRender = 0, activeCapture = 0;
        string deviceDetail;
        try
        {
            if (layoutOk)
            {
                var enumerator = AudioInterop.CreateEnumerator();
                var r = AudioEngine.GetDevices(enumerator, Flow.Render, includeInactive: true);
                var c = AudioEngine.GetDevices(enumerator, Flow.Capture, includeInactive: true);
                render = r.Count;
                capture = c.Count;
                activeRender = r.Count(d => d.IsActive);
                activeCapture = c.Count(d => d.IsActive);
            }

            deviceDetail = render + capture > 0
                ? $"{activeRender} of {render} outputs and {activeCapture} of {capture} inputs are active"
                : "Windows reported no audio devices. A remote session or a stopped Audio service can cause this.";
        }
        catch (Exception ex)
        {
            deviceDetail = $"Could not read audio devices — {ex.GetType().Name}: {ex.Message}";
        }

        results.Add(new Check("Audio devices", render + capture > 0, Fatal: true, deviceDetail));

        // --- The undocumented setter.
        //
        // Fatal, unlike DLS's equivalent scaling check. DLS can still switch
        // layouts when scaling is unavailable, so degrading is right there. APS
        // without a working setter cannot do the single thing it exists for, and
        // it should say so at launch rather than accept profiles and silently
        // fail to apply any of them.
        bool policyOk = false;
        string policyDetail;
        try
        {
            policyOk = PolicyConfig.IsAvailable;
            policyDetail = policyOk
                ? $"Using {PolicyConfig.RouteDescription}"
                : PolicyConfig.RouteDescription;
        }
        catch (Exception ex)
        {
            policyDetail = $"Could not reach the device-change API — {ex.GetType().Name}: {ex.Message}";
        }

        results.Add(new Check("Default-device control", policyOk, Fatal: true, policyDetail));

        // --- Where profiles get saved.
        bool writable = TryWriteProbe(out string writeDetail);
        results.Add(new Check("Settings are writable", writable, Fatal: true, writeDetail));

        return results;
    }

    private static bool TryWriteProbe(out string detail)
    {
        string dir = AppPaths.SettingsDirectory;
        string probe = Path.Combine(dir, ".aps-write-test");

        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            detail = AppPaths.IsPortable ? $"{dir} (portable)" : dir;
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Cannot write to {dir} — {ex.GetType().Name}. " +
                     "Move the app somewhere your account can write, such as a folder under your user profile.";
            return false;
        }
    }

    public static string Format(IEnumerable<Check> checks)
    {
        var sb = new StringBuilder();
        foreach (var c in checks)
        {
            string mark = c.Passed ? "OK  " : c.Fatal ? "FAIL" : "WARN";
            sb.AppendLine($"[{mark}] {c.Name}");
            sb.AppendLine($"       {c.Detail}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Shows a dialog only when something is actually wrong, and returns false
    /// when the app cannot usefully continue.
    /// </summary>
    public static bool ReportProblems(List<Check> checks)
    {
        var problems = checks.Where(c => !c.Passed).ToList();
        if (problems.Count == 0) return true;

        bool fatal = problems.Any(p => p.Fatal);
        var sb = new StringBuilder();
        sb.AppendLine(fatal
            ? $"{AppInfo.Name} cannot run properly on this system:"
            : $"{AppInfo.Name} started, but one feature is unavailable:");
        sb.AppendLine();

        foreach (var p in problems)
        {
            sb.AppendLine($"• {p.Name}");
            sb.AppendLine($"  {p.Detail}");
            sb.AppendLine();
        }

        MessageBox.Show(
            sb.ToString().TrimEnd(),
            AppInfo.Branded,
            MessageBoxButtons.OK,
            fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);

        return !fatal;
    }
}
