using System.Runtime.InteropServices;
using Velopack;

namespace APS;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    private const int AttachParentProcess = -1;

    /// <summary>
    /// This is a WinExe, so it starts with no console and every Console.Write from
    /// a CLI diagnostic is silently discarded. Attach to the launching terminal
    /// (or open one) and rebind stdout before any diagnostic runs. Also routes a
    /// copy to a file when the caller passes one, which is the reliable way to
    /// capture output from a GUI-subsystem process.
    /// </summary>
    private static void StartConsole(string? teeFile = null)
    {
        if (!AttachConsole(AttachParentProcess)) AllocConsole();

        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        if (!string.IsNullOrWhiteSpace(teeFile))
        {
            var file = new StreamWriter(teeFile, append: false) { AutoFlush = true };
            Console.SetOut(new TeeWriter(writer, file));
        }
        else
        {
            Console.SetOut(writer);
        }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _a, _b;
        public TeeWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
        public override System.Text.Encoding Encoding => _a.Encoding;
        public override void Write(char value) { _a.Write(value); _b.Write(value); }
        public override void Write(string? value) { _a.Write(value); _b.Write(value); }
        public override void WriteLine(string? value) { _a.WriteLine(value); _b.WriteLine(value); }
        public override void Flush() { _a.Flush(); _b.Flush(); }
    }

    /// <summary>
    /// Runs a diagnostic with its exceptions printed rather than thrown. An
    /// unhandled exception in a WinExe raises a modal Windows Error Reporting
    /// dialog, which hangs a non-interactive run forever instead of failing.
    /// </summary>
    private static void RunDiagnostic(string[] args, Action body)
    {
        string? tee = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--out", StringComparison.OrdinalIgnoreCase)) tee = args[i + 1];
        }

        StartConsole(tee);
        try
        {
            // Set the success code BEFORE running the body, not after. Assigning 0
            // afterwards silently discards any non-zero code the body set, so a
            // command that reported a failure still exited 0 — which makes every
            // one of these useless in a script.
            Environment.ExitCode = 0;
            body();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Environment.ExitCode = 1;
        }
        finally
        {
            Console.Out.Flush();
        }
    }

    private static bool Is(string[] args, string command) =>
        args.Length > 0 && args[0].Equals(command, StringComparison.OrdinalIgnoreCase);

    private static int IntArg(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out int parsed)) return parsed;
        }

        return fallback;
    }

    /// <summary>
    /// Runs a WinForms message loop for a fixed time, then returns.
    ///
    /// <para>ProfileGuard polls on a WinForms timer, which does not tick without a
    /// message loop. This is the same loop the tray runs, which is the point — a
    /// diagnostic that exercises a different mechanism than the app is not
    /// evidence about the app.</para>
    /// </summary>
    private static void PumpFor(int seconds, Action body)
    {
        body();

        using var stop = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds) * 1000 };
        stop.Tick += (_, _) => { stop.Stop(); Application.ExitThread(); };
        stop.Start();

        Application.Run();
    }

    /// <summary>
    /// Resolves the profile named on the command line. Named lookup rather than
    /// "first profile if none given": applying a profile changes real devices, and
    /// guessing which one the user meant is not a good default for that.
    /// </summary>
    private static AudioProfile? FindProfile(string[] args, out string error)
    {
        var profiles = ProfileManager.LoadProfiles();
        if (profiles.Count == 0)
        {
            error = "No profiles saved yet. Capture one first: --capture \"My profile\"";
            return null;
        }

        if (args.Length < 2)
        {
            error = "Name a profile. Available: " + string.Join(", ", profiles.Select(p => $"'{p.Name}'"));
            return null;
        }

        var match = profiles.FirstOrDefault(p => p.Name.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            error = $"No profile called '{args[1]}'. Available: " +
                    string.Join(", ", profiles.Select(p => $"'{p.Name}'"));
            return null;
        }

        error = "";
        return match;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // The installer invokes this executable for fast install/update hooks. Handle
        // them before normal startup, which touches settings and audio devices.
        VelopackApp.Build()
            // A downloaded package waits for the user's explicit Install choice.
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.RemoveForUninstall())
            .Run();

        // Every path below reads or writes settings, so resolve their location first.
        AppPaths.Initialise();

        if (Is(args, "--preflight"))
        {
            RunDiagnostic(args, () =>
            {
                Console.WriteLine($"{AppInfo.Branded} {AppInfo.Version}");
                Console.WriteLine();
                var results = Preflight.Run();
                Console.Write(Preflight.Format(results));
                Console.WriteLine();
                Console.WriteLine($"Start with Windows: {StartupRegistration.Current}");
                Environment.ExitCode = results.Any(c => !c.Passed && c.Fatal) ? 1 : 0;
            });
            return;
        }

        if (Is(args, "--dump-devices"))
        {
            // Non-destructive: every endpoint in every state, with both identities.
            // This is the first thing to run when a profile misbehaves.
            RunDiagnostic(args, () =>
            {
                AudioInterop.AssertLayout();
                Console.WriteLine("Audio interop struct layout: OK");
                Console.WriteLine($"Default-device route: {PolicyConfig.RouteDescription}");
                Console.WriteLine();
                Console.Write(AudioEngine.DumpDevices());
            });
            return;
        }

        if (Is(args, "--current"))
        {
            RunDiagnostic(args, () => Console.Write(AudioEngine.DescribeDefaults()));
            return;
        }

        if (Is(args, "--list-profiles"))
        {
            RunDiagnostic(args, () =>
            {
                var profiles = ProfileManager.LoadProfiles();
                if (profiles.Count == 0)
                {
                    Console.WriteLine("No profiles saved yet. Capture one: --capture \"My profile\"");
                    return;
                }

                var live = AudioEngine.GetAllDevices(includeInactive: false);
                foreach (var p in profiles) Console.Write(ProfileEngine.DescribeProfile(p, live));
            });
            return;
        }

        if (Is(args, "--capture"))
        {
            RunDiagnostic(args, () =>
            {
                string name = args.Length > 1 ? args[1] : "Captured profile";
                var captured = ProfileEngine.Capture(name);

                var all = ProfileManager.LoadProfiles();

                // Capturing over an existing name replaces its devices and keeps
                // everything else — id, hotkey, watch settings, and its place in the
                // list. Removing and appending instead, as this used to, silently
                // dropped the hotkey and moved the profile to the bottom of the tray
                // menu, which is a surprising amount of damage for a command whose
                // job is "these devices, under that name".
                int at = all.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (at >= 0)
                {
                    var existing = all[at];
                    existing.Output = captured.Output;
                    existing.OutputComms = captured.OutputComms;
                    existing.Input = captured.Input;
                    captured = existing;
                }
                else
                {
                    all.Add(captured);
                }

                if (!ProfileManager.TrySaveProfiles(all, out string saveError))
                {
                    Console.WriteLine($"FAILED to save: {saveError}");
                    Environment.ExitCode = 1;
                    return;
                }

                var live = AudioEngine.GetAllDevices(includeInactive: false);
                Console.WriteLine(at >= 0 ? $"Updated the devices in '{name}':" : $"Captured '{name}':");
                Console.Write(ProfileEngine.DescribeProfile(captured, live));
            });
            return;
        }

        if (Is(args, "--test-apply"))
        {
            // Resolves everything and reports, changing nothing. This is what makes
            // the two-tier device matching observable.
            RunDiagnostic(args, () =>
            {
                var target = FindProfile(args, out string error);
                if (target == null) { Console.WriteLine(error); Environment.ExitCode = 1; return; }

                var live = AudioEngine.GetAllDevices(includeInactive: false);
                Console.Write(ProfileEngine.DescribeProfile(target, live));

                var pending = ProfileEngine.Compare(target);
                Console.WriteLine();
                Console.WriteLine($"Already active: {pending.Count == 0}");
                foreach (var difference in pending) Console.WriteLine($"    pending: {difference}");
                Console.WriteLine("(nothing was changed)");
            });
            return;
        }

        if (Is(args, "--apply"))
        {
            RunDiagnostic(args, () =>
            {
                var target = FindProfile(args, out string error);
                if (target == null) { Console.WriteLine(error); Environment.ExitCode = 1; return; }

                Console.WriteLine($"Applying '{target.Name}'...");
                bool ok = AudioSafety.Apply(target, out string message);
                Console.WriteLine(ok ? $"SUCCESS: {message}" : $"FAILED: {message}");
                if (!ok) Environment.ExitCode = 1;

                Console.WriteLine();
                Console.Write(AudioEngine.DescribeDefaults());
            });
            return;
        }

        // Everything below needs WinForms; the diagnostics above deliberately do
        // not, so they stay runnable even when UI initialisation would block.
        ApplicationConfiguration.Initialize();

        if (Is(args, "--screenshot-icon"))
        {
            // Preview the embedded icon frames at tray sizes, including 16px.
            //
            // Two rows: plain, and the lock-badged variant shown
            // while a watched profile is in effect. The badge exists to make that
            // state visible at 16px in a tray, so it has to be checked at 16px
            // beside the icon it has to stay distinguishable from.
            int[] sizes = { 16, 20, 24, 32, 48 };
            const int pad = 12;
            int width = pad + sizes.Sum(s => s + pad);
            int rowHeight = 48 + pad;

            using var sheet = new Bitmap(width, pad + rowHeight * 2);
            using (var g = Graphics.FromImage(sheet))
            {
                // Two grounds: the taskbar is dark, but a light theme is not.
                g.Clear(Color.FromArgb(32, 32, 32));
                g.FillRectangle(Brushes.Gainsboro, 0, 0, width, pad);

                foreach (bool badged in new[] { false, true })
                {
                    int y = pad + (badged ? rowHeight : 0);
                    int x = pad;

                    foreach (int size in sizes)
                    {
                        using var icon = AppIcon.Create(size, badged);
                        g.DrawIcon(icon, new Rectangle(x, y + (48 - size) / 2, size, size));
                        x += size + pad;
                    }
                }
            }

            string iconPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "icon-preview.png";
            sheet.Save(iconPath, System.Drawing.Imaging.ImageFormat.Png);

            StartConsole();
            Console.WriteLine($"Icon preview saved to {iconPath} " +
                              $"({string.Join(", ", sizes)}px; top row plain, bottom row watching)");
            Console.Out.Flush();
            return;
        }

        if (Is(args, "--test-pin"))
        {
            // Makes ProfileGuard observable. The tray shows drift as a balloon and
            // nothing else; this prints every decision, so "it noticed within a
            // couple of seconds" and "it stopped and said why" can be seen rather
            // than inferred.
            //
            // This applies the profile first, exactly as clicking it in the tray
            // would, so it does change real devices.
            RunDiagnostic(args, () =>
            {
                var target = FindProfile(args, out string error);
                if (target == null) { Console.WriteLine(error); Environment.ExitCode = 1; return; }

                // Allow time to change a default manually and observe several polls.
                int seconds = IntArg(args, "--seconds", 180);

                Console.WriteLine($"Applying '{target.Name}'...");
                if (!AudioSafety.Apply(target, out string message))
                {
                    Console.WriteLine($"FAILED: {message}");
                    Environment.ExitCode = 1;
                    return;
                }

                Console.WriteLine(message);
                Console.WriteLine();
                Console.WriteLine($"Watching '{target.Name}' for {seconds}s. " +
                                  $"Auto-reapply is {(target.AutoReapply ? "ON" : "off")}. " +
                                  "Change a default in Sound settings and watch what happens.");
                Console.WriteLine($"Guards: {ProfileGuard.PollSeconds}s poll " +
                                  $"(PollSeconds in {Path.GetFileName(AppPaths.SettingsFile)}, " +
                                  $"default {ProfileGuard.DefaultPollSeconds}, " +
                                  $"{ProfileGuard.MinPollSeconds}-{ProfileGuard.MaxPollSeconds} allowed) — and " +
                                  $"{ProfileGuard.RunawayLimit} reapplies per {ProfileGuard.RunawayWindowSeconds}s.");
                Console.WriteLine();

                ProfileGuard? guard = null;
                var began = DateTime.Now;

                PumpFor(seconds, () =>
                {
                    guard = new ProfileGuard();

                    guard.Drifted += (_, e) =>
                        Console.WriteLine($"  {(DateTime.Now - began).TotalMilliseconds,8:F0} ms  DRIFTED  " +
                                          $"{e.What} is now {e.NowOn}" +
                                          $"{(e.CanReapply ? "  (offer to switch back)" : "  (cannot switch back)")}");

                    guard.Reapplied += (_, e) =>
                        Console.WriteLine($"  {(DateTime.Now - began).TotalMilliseconds,8:F0} ms  REAPPLIED  " +
                                          $"{e.What} had become {e.NowOn}");

                    guard.Stopped += (_, e) =>
                    {
                        Console.WriteLine($"  {(DateTime.Now - began).TotalMilliseconds,8:F0} ms  " +
                                          $"STOPPED ({(e.Automatic ? "by itself" : "asked to")}): {e.Reason}");

                        // Nothing left to observe, and sitting out the rest of the
                        // timeout only invites the reader to wonder whether
                        // something else is still coming.
                        if (e.Automatic) Application.ExitThread();
                    };

                    guard.Watch(target, userInitiated: true);
                });

                guard?.Dispose();

                Console.WriteLine();
                Console.Write(AudioEngine.DescribeDefaults());
            });
            return;
        }


        if (Is(args, "--screenshot-update"))
        {
            // Renders the update dialog for a made-up release. Nothing is downloaded:
            // the manager points nowhere and the install callback refuses.
            var asset = new VelopackAsset
            {
                PackageId = "APS-AudioProfileSystem",
                Version = SemanticVersion.Parse("9.9.9"),
                NotesMarkdown = "# APS update\n\nAPS can now update itself from the tray menu.\n\n" +
                                "- Installed copies check quietly in the background.\n" +
                                "- Your profiles and hotkeys stay where they are.\n" +
                                "- A release's notes appear here before you install it.\n"
            };
            var manager = new UpdateManager("https://example.invalid/releases");
            using var form = new UpdateForm(manager, new UpdateInfo(asset, false), _ => false);
            form.Show();
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));

            string outPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "update-preview.png";
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);

            StartConsole();
            Console.WriteLine($"Update dialog preview saved to {outPath} ({form.Width}x{form.Height})");
            Console.Out.Flush();
            return;
        }

        if (Is(args, "--screenshot-menu"))
        {
            // The tray menu is a window we draw ourselves, so unlike a
            // ContextMenuStrip it can be rendered to a file and actually checked —
            // including at high DPI, where clipping shows up.
            var profiles = ProfileManager.LoadProfiles();

            float scale = 1f;
            string? state = null;
            string? target = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--scale", StringComparison.OrdinalIgnoreCase) &&
                    float.TryParse(args[i + 1], out float parsed)) scale = parsed;

                // --state <drag|rename|update|delete|drifted> --profile "<name>" renders one
                // of the states a row can be in. None of them can be seen in a still
                // of the resting menu, and every one of them is drawn at a height the
                // menu computed rather than a height a layout engine chose — which is
                // exactly the kind of thing that clips.
                if (args[i].Equals("--state", StringComparison.OrdinalIgnoreCase)) state = args[i + 1];
                if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase)) target = args[i + 1];
            }

            int at = target == null
                ? 0
                : profiles.FindIndex(p => p.Name.Equals(target, StringComparison.OrdinalIgnoreCase));

            if (state != null && at < 0)
            {
                StartConsole();
                Console.WriteLine($"No profile called '{target}'. Available: " +
                                  string.Join(", ", profiles.Select(p => $"'{p.Name}'")));
                Console.Out.Flush();
                Environment.ExitCode = 1;
                return;
            }

            bool drifted = state != null && state.Equals("drifted", StringComparison.OrdinalIgnoreCase);

            var entries = state == null || state.Equals("drag", StringComparison.OrdinalIgnoreCase)
                ? TrayContext.BuildPreviewEntries(profiles)
                : drifted
                    ? TrayContext.BuildPreviewEntries(profiles, chosenAt: at)
                    : TrayContext.BuildPreviewEntries(profiles, at, state);

            using var popup = TrayPopup.CreateForCapture(entries, scale);

            if (state != null && state.Equals("drag", StringComparison.OrdinalIgnoreCase))
            {
                popup.SimulateDragForCapture(at, Math.Max(0, at - 2));
            }
            using var bmp = new Bitmap(popup.Width, popup.Height);
            using (var g = Graphics.FromImage(bmp)) popup.Render(g);

            string outPath = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "tray-menu-preview.png";
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);

            StartConsole();
            Console.WriteLine($"Tray menu preview saved to {outPath} ({popup.Width}x{popup.Height}, scale {scale})");
            Console.Out.Flush();
            return;
        }

        using var mutex = new Mutex(true, AppInfo.SingleInstanceMutex, out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show(
                $"{AppInfo.Branded} is already running in your system tray.",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // Environment checks before anything touches the audio configuration. A
        // fatal result stops here rather than failing confusingly later.
        var checks = Preflight.Run();
        if (!Preflight.ReportProblems(checks)) return;

        // Read before MarkFirstRunComplete clears it — the tray needs to know, so
        // it can explain where it went.
        bool firstRun = Preflight.IsFirstRun;

        if (firstRun)
        {
            // Starting with Windows is the default: a tray utility that only works
            // once you remember to launch it is a utility you stop using. It is a
            // ticked item in the tray menu, so it is visible and one click to undo.
            StartupRegistration.Set(true, out _);
            Preflight.MarkFirstRunComplete();
        }
        else
        {
            // Re-point a stale entry, or restore one that went missing, without
            // ever overriding a deliberate "off".
            StartupRegistration.Reconcile();
        }

        Application.Run(new TrayContext(firstRun));
    }
}
