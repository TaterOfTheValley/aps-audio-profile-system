using System.Runtime.InteropServices;
using Velopack;

namespace APS;

/// <summary>
/// The tray icon and its menu — the surface this app is actually used through.
///
/// Ported from DLS's TrayContext, keeping its structure: a drawn popup opened on
/// the click rather than handed to NotifyIcon, a file watcher so external edits
/// to the settings file are picked up, and hotkey registration split out from the
/// menu rebuild.
/// </summary>
public class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly HotkeyManager _hotkeyManager;
    private readonly FileSystemWatcher _profileWatcher;
    private readonly System.Windows.Forms.Timer _profileReloadTimer;
    private readonly System.Windows.Forms.Timer _undoTimer;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private UpdateManager _updateManager = UpdateService.CreateManager();
    private readonly ProfileGuard _guard;

    private List<TrayPopup.Entry> _entries = new();
    private List<AudioProfile> _profiles;
    private AudioProfile? _activeProfile;
    private bool _profileChangePending;
    private string _lastHandledProfileSignature = "";
    private bool _reloadingProfiles;
    private UpdateForm? _updateForm;
    private UpdateInfo? _availableUpdate;
    private bool _checkingUpdates;

    /// <summary>
    /// Which profile has an inline prompt open on its row, and which prompt.
    ///
    /// <para>The id rather than the profile, because the settings file can be
    /// reloaded from disk while the menu is open and every profile object is
    /// replaced when it is. A prompt holding an object nobody else holds any more
    /// would rename a profile that no longer exists.</para>
    ///
    /// <para>This is not the old sub-page in another form. The prompt takes over one
    /// row of the list and leaves everything else on screen — which is the whole
    /// point of it not being a dialog either.</para>
    /// </summary>
    private string? _inlineProfileId;
    private Inline _inlineMode;

    private enum Inline { None, Rename, NameNew, Update, Delete }

    /// <summary>Where the menu was opened. Nothing reopens it any more — every
    /// question is answered on the row — but the popup re-anchors here after each
    /// redraw, so a menu that grows or shrinks moves only at the top.</summary>
    private Point _menuAnchor;

    /// <summary>What clicking the current balloon should do, or null when it is
    /// just an announcement. NotifyIcon has one BalloonTipClicked event for every
    /// balloon it has ever shown, so the meaning of a click has to be carried
    /// alongside the tip that is currently up.</summary>
    private Action? _balloonAction;

    /// <summary>Set when a drift balloon has just gone up, so the stop that follows
    /// it does not put a second one on top saying the same thing. Explicit rather
    /// than inferred from <see cref="_balloonAction"/>, which can be left over from
    /// an unrelated tip.</summary>
    private bool _driftAnnounced;

    /// <param name="firstRun">
    /// True the very first time this user launches APS. Drives a one-off balloon
    /// explaining where the app went — see the constructor's tail for why that is
    /// not optional.
    /// </param>
    public TrayContext(bool firstRun = false)
    {
        _profiles = ProfileManager.LoadProfiles();
        _hotkeyManager = new HotkeyManager();

        // Watching for a profile being knocked out from under you. ProfileGuard
        // polls rather than subscribing to Windows' device notifications — the
        // reason is a crash, and it is written up on the class itself.
        _guard = new ProfileGuard();
        _guard.Drifted += OnDrifted;
        _guard.Reapplied += OnReapplied;
        _guard.Stopped += OnGuardStopped;

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcon.Shared,
            Text = AppInfo.Name,
            Visible = true
        };

        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };

        _notifyIcon.BalloonTipClosed += (_, _) => _balloonAction = null;

        // The popup is a window we own, so it opens on the click rather than being
        // handed to NotifyIcon. Cursor position is the anchor, which lands
        // correctly whichever edge the taskbar is on.
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right && e.Button != MouseButtons.Left) return;

            // Every fresh click on the icon starts with a plain list. A half-typed
            // name left over from a menu that was dismissed by clicking elsewhere is
            // not something to be handed back an hour later.
            CloseInline();

            // Opening the menu is a question about right now, and the guard's poll is
            // deliberately slow (see ProfileGuard.PollMs). Asking it to look before
            // the menu is built means what the menu says is current, however long ago
            // the last poll was.
            _guard.CheckNow();

            ShowMenu(Cursor.Position);
        };

        _lastHandledProfileSignature = GetProfileSignature();

        // Watch wherever settings actually live, not the executable's folder.
        _profileWatcher = new FileSystemWatcher(
            AppPaths.SettingsDirectory, Path.GetFileName(AppPaths.SettingsFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _profileWatcher.Changed += (_, _) => _profileChangePending = true;
        _profileWatcher.Created += (_, _) => _profileChangePending = true;
        _profileWatcher.Renamed += (_, _) => _profileChangePending = true;

        // Debounced: a single save produces several file events.
        _profileReloadTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _profileReloadTimer.Tick += (_, _) =>
        {
            if (!_profileChangePending) return;
            _profileChangePending = false;
            ReloadProfilesFromDisk();
        };
        _profileReloadTimer.Start();

        // Only ticks while an undo is live, so the countdown in the menu stays
        // honest without polling the rest of the time.
        _undoTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _undoTimer.Tick += (_, _) =>
        {
            if (!AudioSafety.CanUndo) _undoTimer.Stop();
        };

        AudioSafety.UndoStateChanged += (_, _) =>
        {
            if (AudioSafety.CanUndo) _undoTimer.Start();
        };

        // The first check waits until startup has finished. Subsequent checks are
        // infrequent, both for the user's sake and GitHub's anonymous API limit.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 12 * 60 * 60 * 1000;
            await CheckForUpdatesAsync(manual: false);
        };
        if (_updateManager.IsInstalled) _updateTimer.Start();

        RefreshMenuAndHotkeys();
        DetectActiveProfile();

        if (!string.IsNullOrWhiteSpace(ProfileManager.LastNotice))
        {
            ShowBalloon(6000, AppInfo.Name, ProfileManager.LastNotice, ToolTipIcon.Info);
        }
        else if (firstRun)
        {
            // APS has no window of its own, so a first launch
            // otherwise produces no visible sign whatsoever that anything
            // happened — and Windows 11 puts new tray icons in the hidden
            // overflow by default, so "it didn't start" is the reasonable
            // conclusion rather than a mistaken one. Say where it went.
            ShowBalloon(10000, $"{AppInfo.Name} is running",
                "APS lives in the notification area — click the ^ arrow by the clock if you " +
                "don't see it, and drag it onto the taskbar to keep it there. " +
                "Click the icon for your profiles.",
                ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// The DPI of the monitor under the click. Read per-open rather than cached: the
    /// taskbar can sit on a display with a different scale factor than the one the
    /// app started on.
    ///
    /// <para>Deliberately not <c>Graphics.FromHwnd(IntPtr.Zero).DpiX</c> (GDI's
    /// GetDeviceCaps): that call only ever reports the primary monitor's DPI, and on
    /// a per-monitor-DPI-aware process it is not guaranteed to track a scaling
    /// change made after launch — plugging in a new monitor, or moving the scaling
    /// slider, could leave it reporting whatever DPI was in effect when the process
    /// started. <see cref="GetDpiForMonitor"/> is the API Windows documents for a
    /// live, per-monitor value.</para>
    /// </summary>
    private static float TrayScale(Point anchor)
    {
        var pt = new POINT { X = anchor.X, Y = anchor.Y };
        IntPtr monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
        {
            return dpiX / 96f;
        }

        // Only reachable if the monitor lookup itself fails, which needs no live
        // per-monitor value to fall back on anyway.
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        float dpi = g.DpiX <= 0 ? 96f : g.DpiX;
        return dpi / 96f;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // -----------------------------------------------------------------------
    // Watching the live profile
    // -----------------------------------------------------------------------

    /// <summary>
    /// Shows a balloon, optionally with something for a click to do. Always goes
    /// through here so <see cref="_balloonAction"/> is set — or cleared — for
    /// every tip, and a click on an announcement cannot invoke whatever the
    /// previous balloon meant.
    /// </summary>
    private void ShowBalloon(int ms, string title, string text, ToolTipIcon icon, Action? onClick = null)
    {
        _balloonAction = onClick;
        _notifyIcon.ShowBalloonTip(ms, title, text, icon);
    }

    /// <summary>
    /// The devices moved and the profile is no longer in effect. Say so, and make
    /// getting back one click.
    ///
    /// <para>This is the default behaviour and the reason the app is worth having:
    /// the complaint was never "Windows changed my device", it was not knowing it
    /// had happened and then hunting through Sound settings to undo it. Being told,
    /// with the fix attached, answers both.</para>
    /// </summary>
    private void OnDrifted(object? sender, DriftEventArgs e)
    {
        var profile = e.Profile;

        if (!e.CanReapply)
        {
            // Offering a switch that is going to fail is worse than not offering, so
            // this one is left to the stop that follows — its reason names the
            // device that went away, which is the useful half.
            return;
        }

        _driftAnnounced = true;

        ShowBalloon(10000, $"{AppInfo.Name} — “{profile.Name}” is no longer active",
            $"Something changed {e.What} to {e.NowOn}.\n" +
            $"Click here to switch back to “{profile.Name}”.",
            ToolTipIcon.Info,
            () => SwitchToProfile(profile));
    }

    /// <summary>
    /// The opt-in behaviour: it was put back. Saying so is not optional — a profile
    /// that silently restores devices is indistinguishable from a machine that
    /// ignores your settings, which is the confusion this app exists to remove.
    ///
    /// <para><b>Nothing to click.</b> This used to offer "click here to stop watching
    /// for now", which is the one thing a click here must not do. Putting it back is
    /// what was asked for, and a toast whose entire surface is a button that cancels
    /// the feature is a trap: people click a notification to make it go away, not to
    /// answer it, and the cost of that misread is the setting silently off and the
    /// next change going unfixed.</para>
    ///
    /// <para><see cref="OnDrifted"/> keeps its click because it is the opposite case
    /// — there the click does the thing you wanted and could not otherwise get
    /// without opening Sound settings, it says so on the balloon before you click it,
    /// and "Undo last switch" is waiting in the menu if it was not what you meant.
    /// Turning watching off is a decision rather than a fix, and decisions belong
    /// somewhere you can see the state you are changing.</para>
    /// </summary>
    private void OnReapplied(object? sender, ReapplyEventArgs e)
    {
        RefreshMenuAndHotkeys();

        ShowBalloon(6000, $"{AppInfo.Name} — put it back",
            $"Something changed {e.What} to {e.NowOn}. " +
            $"Restored “{e.Profile.Name}”.",
            ToolTipIcon.Info);
    }

    private void OnGuardStopped(object? sender, GuardStoppedEventArgs e)
    {
        UpdateTrayIcon();
        RefreshMenuAndHotkeys();

        bool announced = _driftAnnounced;
        _driftAnnounced = false;

        // A person who just unticked the box does not need to be told what they did,
        // and a drift that already put up its own balloon does not need a second.
        if (!e.Automatic || announced) return;

        ShowBalloon(8000, $"{AppInfo.Name} — “{e.ProfileName}” is no longer active",
            $"{Capitalise(e.Reason)}.", ToolTipIcon.Warning);
    }

    /// <summary>
    /// Starts or stops watching to match the profile now in effect. Called wherever
    /// the live profile changes, so there is one rule for it rather than a Watch
    /// scattered through every action.
    /// </summary>
    /// <param name="userInitiated">True when this follows a click rather than the
    /// tray noticing something. Only a person can undo a conceded runaway — see
    /// <see cref="ProfileGuard.GaveUp"/>.</param>
    private void SyncGuard(AudioProfile? live, string stopReason, bool userInitiated = false)
    {
        if (live != null && live.Watched)
        {
            _guard.Watch(live, userInitiated);
        }
        else
        {
            _guard.StopWatching(stopReason);
        }

        UpdateTrayIcon();
    }

    /// <summary>
    /// The badge is the only place watching is visible without opening anything, so
    /// it follows the guard rather than the profile's Watched flag — those differ
    /// whenever watching has been stopped for the session.
    ///
    /// <para>Both icons are cached in <see cref="AppIcon"/>, so this is a field
    /// assignment: an icon built per swap would leak an unmanaged handle on every
    /// profile switch.</para>
    /// </summary>
    private void UpdateTrayIcon()
    {
        _notifyIcon.Icon = _guard.IsWatching ? AppIcon.SharedBadged : AppIcon.Shared;
    }

    private static string Capitalise(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // -----------------------------------------------------------------------
    // Settings file watching
    // -----------------------------------------------------------------------

    private string GetProfileSignature()
    {
        try
        {
            if (!File.Exists(ProfileManager.ConfigPath)) return "";
            var info = new FileInfo(ProfileManager.ConfigPath);
            return $"{info.LastWriteTimeUtc.Ticks}:{info.Length}";
        }
        catch
        {
            return "";
        }
    }

    private void MarkProfileFileHandled() => _lastHandledProfileSignature = GetProfileSignature();

    private void ReloadProfilesFromDisk()
    {
        if (_reloadingProfiles) return;

        string signature = GetProfileSignature();
        if (string.IsNullOrEmpty(signature) || signature == _lastHandledProfileSignature) return;

        _reloadingProfiles = true;
        try
        {
            var loaded = ProfileManager.LoadProfiles();
            _profiles = loaded;
            _lastHandledProfileSignature = signature;

            // The poll interval lives in the same file, so an edit to it arrives on
            // the same reload as an edit to a profile.
            _guard.RefreshInterval();

            DetectActiveProfile();
            RefreshMenuAndHotkeys();
        }
        finally
        {
            _reloadingProfiles = false;
        }
    }

    // -----------------------------------------------------------------------
    // Menu
    // -----------------------------------------------------------------------

    /// <summary>
    /// Re-registers global hotkeys. Split out from the menu rebuild because it
    /// needs no device query: DLS separated these because an editor autosave has
    /// to refresh hotkeys immediately, and doing that through a full menu rebuild
    /// meant enumerating hardware every time the user paused typing. The same
    /// argument applies here, where an enumeration can be dozens of endpoints.
    /// </summary>
    private void RegisterHotkeys()
    {
        _hotkeyManager.UnregisterAll();
        foreach (var profile in _profiles)
        {
            var p = profile;
            if (p.NeedsRecapture || string.IsNullOrWhiteSpace(p.Hotkey)) continue;
            _hotkeyManager.Register(p.Hotkey, () => SwitchToProfile(p));
        }
    }

    /// <summary>Opens the menu at the pointer.</summary>
    private void ShowMenu(Point anchor)
    {
        _menuAnchor = anchor;
        TrayPopup.Show(BuildEntries, anchor, TrayScale(anchor), MoveProfileTo);
    }

    /// <summary>What the popup calls to draw itself, including after a click it
    /// stayed open for.</summary>
    private List<TrayPopup.Entry> BuildEntries()
    {
        RefreshMenuAndHotkeys();
        return _entries;
    }

    private void RefreshMenuAndHotkeys()
    {
        RegisterHotkeys();
        _entries = BuildProfileList();
    }

    private List<TrayPopup.Entry> BuildProfileList()
    {
        var entries = new List<TrayPopup.Entry>();

        var devices = ReadDevices();
        var live = devices.Live;
        var liveMatches = devices.Matches;
        bool anyLive = liveMatches.Values.Any(v => v);

        if (_profiles.Count == 0)
        {
            entries.Add(new TrayPopup.HeadingEntry { Text = "NO PROFILES YET" });
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = "Add a profile",
                Detail = "from what's in use now",
                Emphasis = true,
                KeepOpen = true,
                Invoke = CaptureCurrentProfile
            });
        }
        else
        {
            entries.Add(new TrayPopup.HeadingEntry
            {
                Text = "PROFILES",

                // A drag has no control to point at, so the heading says it is
                // there — and only while there is somewhere to drag to.
                Hint = _profiles.Count > 1 ? "drag to reorder" : null
            });
        }

        foreach (var profile in _profiles)
        {
            var p = profile;

            if (p.Id == _inlineProfileId && _inlineMode != Inline.None)
            {
                entries.Add(InlineEntry(p));
                continue;
            }

            // Live means the machine actually looks like this right now. Chosen
            // means it is the last one switched to and nothing matches any more —
            // the defaults were changed outside this app, or a device came and went.
            //
            // These used to be one flag, with chosen falling back to live. That put a
            // lit dot beside a profile the machine had drifted away from, so the menu
            // answered "which one am I on?" with the name of one you are not on. They
            // are two states and they now look like two.
            bool isLive = liveMatches[p.Id];
            bool isChosen = !anyLive && _activeProfile?.Id == p.Id;
            string? unavailable = UnavailableReason(p, live);

            entries.Add(new TrayPopup.ProfileEntry
            {
                Profile = p,
                IsLive = isLive,
                IsChosen = isChosen,
                Subtitle = Subtitle(p, live),
                UnavailableReason = unavailable,
                Enabled = unavailable == null,
                Invoke = unavailable == null ? () => SwitchToProfile(p) : null,
                Actions = ProfileActions(p)
            });
        }

        entries.Add(new TrayPopup.SeparatorEntry());

        // Said out loud rather than left to be inferred from an absent dot. "None of
        // the rows above is lit" is a thing you have to already know how to read, and
        // the question people open this menu with is exactly the one it answers.
        //
        // Only when there are profiles to not match: the empty state has its own
        // heading and does not need telling that nothing matches nothing.
        if (_profiles.Count > 0 && !anyLive)
        {
            entries.Add(new TrayPopup.HeadingEntry
            {
                Text = "NOT ONE OF YOUR PROFILES",
                Hint = _activeProfile != null ? $"last picked: {_activeProfile.Name}" : null
            });
        }

        // "What am I actually on right now" — half the reason this app exists, and
        // answering it should cost one click and no reading of a settings page.
        entries.Add(new TrayPopup.StatusEntry { Rows = devices.NowRows });

        entries.Add(new TrayPopup.SeparatorEntry());

        if (AudioSafety.CanUndo)
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = "Undo last switch",
                Detail = $"{AudioSafety.RemainingSeconds}s",
                Emphasis = true,
                Invoke = UndoLastSwitch
            });
        }

        if (_profiles.Count > 0)
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                // "Add a profile" rather than "Save current devices as a profile":
                // what somebody is doing here is adding to the list above, and where
                // the devices come from is a detail of how, not what. It stays the
                // detail — the row sits directly under the "Now" block, so "what's in
                // use now" has just been spelled out two rows above it.
                //
                // Deliberately the same phrase the update glyph uses, because they
                // are the same gesture pointed at different targets: one makes a new
                // profile out of what is in use now, the other puts it into a profile
                // that already exists.
                Text = "Add a profile",
                Detail = "from what's in use now",

                // Stays open: what it does is put a name field on the new row, and a
                // field on a menu that just closed is no use to anybody.
                KeepOpen = true,
                Invoke = CaptureCurrentProfile
            });
        }

        // There is no editor yet. Opening the settings file is honest about where
        // profiles live and is genuinely usable; pretending there is an editor
        // would not be.
        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Edit profiles…",
            Detail = Path.GetFileName(AppPaths.SettingsFile),
            Invoke = OpenSettingsFile
        });

        entries.Add(new TrayPopup.SeparatorEntry());

        var watchTarget = _activeProfile ?? _profiles.FirstOrDefault(p => liveMatches[p.Id]);
        if (watchTarget != null)
        {
            entries.AddRange(WatchEntries(watchTarget, name: watchTarget.Name));
        }

        if (_availableUpdate != null)
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = $"Update to APS {_availableUpdate.TargetFullRelease.Version}",
                Emphasis = true,
                Invoke = ShowUpdateForm
            });
        }

        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Check for updates…",
            // The installed version, right where the question "am I up to date?" is
            // asked — the one place in the menu someone looking for it will look.
            Detail = _checkingUpdates ? "checking" : AppInfo.Version,
            Enabled = !_checkingUpdates,
            Invoke = () => _ = CheckForUpdatesAsync(manual: true)
        });

        // Only an installed copy can update itself, so only an installed copy has a
        // channel to pick.
        if (_updateManager.IsInstalled)
        {
            bool alpha = UpdateService.Channel == UpdateChannel.Alpha;
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = "Include alpha builds",
                Checked = alpha,
                // Unticking does not step back to an older release, so someone who
                // just did it on an alpha would otherwise see nothing happen.
                Detail = !alpha && UpdateService.RunningPrerelease ? "on an alpha now" : null,
                KeepOpen = true,
                Invoke = ToggleAlphaUpdates
            });
        }

        var startup = StartupRegistration.Current;
        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Start with Windows",
            Checked = startup == StartupRegistration.State.On,
            Detail = startup == StartupRegistration.State.BlockedByWindows ? "blocked in Task Manager" : null,
            // A Task Manager "Disable" can only be undone in Task Manager —
            // rewriting the Run key would leave the tick on while Windows still
            // refused to start the app, which is worse than not offering it.
            Enabled = startup != StartupRegistration.State.BlockedByWindows,
            KeepOpen = true,
            Invoke = ToggleStartup
        });

        entries.Add(new TrayPopup.CommandEntry { Text = "Exit", Invoke = ExitThread });

        return entries;
    }

    // -----------------------------------------------------------------------
    // Updates
    // -----------------------------------------------------------------------

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (!_updateManager.IsInstalled)
        {
            if (manual && MessageBox.Show(
                    "This copy of APS was run as a standalone EXE. Install APS once with " +
                    "APS-AudioProfileSystem-win-Setup.exe to enable in-app updates. Open the release page?",
                    "APS updates", MessageBoxButtons.YesNo, MessageBoxIcon.Information)
                == DialogResult.Yes)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        UpdateService.RepositoryUrl + "/releases") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not open the release page: {ex.Message}",
                        "APS updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            return;
        }

        if (_checkingUpdates) return;
        _checkingUpdates = true;
        RefreshMenuAndHotkeys();
        bool recheck = false;
        try
        {
            var manager = _updateManager;
            var found = await manager.CheckForUpdatesAsync();

            // The channel changed while this was in flight, so the answer is about
            // releases nobody is asking after any more. Ask again, on the new one.
            if (!ReferenceEquals(manager, _updateManager))
            {
                recheck = true;
                return;
            }

            bool newlyFound = found != null &&
                _availableUpdate?.TargetFullRelease.Version != found.TargetFullRelease.Version;
            _availableUpdate = found;
            RefreshMenuAndHotkeys();

            if (found == null)
            {
                if (manual) MessageBox.Show($"APS {AppInfo.Version} is up to date.",
                    "APS updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (manual)
            {
                ShowUpdateForm();
            }
            else if (newlyFound)
            {
                ShowBalloon(6000, "APS update available",
                    $"Version {found.TargetFullRelease.Version} is ready. Click here to review and install it.",
                    ToolTipIcon.Info, ShowUpdateForm);
            }
        }
        catch (Exception ex)
        {
            if (manual) MessageBox.Show($"Could not check for updates: {ex.Message}",
                "APS updates", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _checkingUpdates = false;
            RefreshMenuAndHotkeys();

            // After the flag is cleared, or the second check would refuse to start.
            if (recheck) await CheckForUpdatesAsync(manual);
        }
    }

    private void ToggleAlphaUpdates()
    {
        var wanted = UpdateService.Channel == UpdateChannel.Alpha ? UpdateChannel.Stable : UpdateChannel.Alpha;
        if (!UpdateService.Set(wanted, out string error))
        {
            ShowBalloon(6000, "APS updates", $"Could not save that choice: {error}", ToolTipIcon.Warning);
            return;
        }

        // What was found belongs to the old channel, so it goes until the new one has
        // been asked. The check is quiet: a balloon if something is there, nothing if
        // not, since the person only ticked a box.
        _updateManager = UpdateService.CreateManager();
        _availableUpdate = null;
        RefreshMenuAndHotkeys();
        _ = CheckForUpdatesAsync(manual: false);
    }

    private void ShowUpdateForm()
    {
        if (_availableUpdate == null) return;
        if (_updateForm is { IsDisposed: false })
        {
            _updateForm.BringToFront();
            return;
        }

        _updateForm = new UpdateForm(_updateManager, _availableUpdate, ApplyUpdate);
        _updateForm.FormClosed += (_, _) => _updateForm = null;
        _updateForm.Show();
        _updateForm.BringToFront();
    }

    private bool ApplyUpdate(VelopackAsset release)
    {
        // Nothing to save first: profiles are written the moment they change, and
        // the guard is stopped by ExitThreadCore like every other exit.
        _updateManager.WaitExitThenApplyUpdates(release);
        ExitThread();
        return true;
    }

    /// <summary>
    /// The two tick boxes that used to be called pinning.
    ///
    /// <para><b>Why the words changed.</b> "Pin" said what the code did, not what
    /// the user gets: nothing is fastened to anything, and the word gave no clue
    /// that the default behaviour is a notification rather than a correction —
    /// people read "pinned" as "locked", then wondered why Windows had moved their
    /// microphone anyway. These two lines say the two things that actually happen,
    /// in the order you would decide them: watch this, and — separately — put it
    /// back without asking.</para>
    ///
    /// <para>Two boxes rather than three states on one, unchanged from before:
    /// "tell me" and "fix it" are different promises and a single control that
    /// cycles through both is a control you have to click twice to understand.</para>
    /// </summary>
    /// <param name="name">The profile these two lines are about. Named rather than
    /// implied: they sit below the list, not on a row, so nothing else on screen says
    /// which profile they mean.</param>
    private List<TrayPopup.Entry> WatchEntries(AudioProfile profile, string name)
    {
        var entries = new List<TrayPopup.Entry>
        {
            new TrayPopup.CommandEntry
            {
                // Names what it does for you rather than what APS does about it.
                // "Keep an eye on" described the mechanism — a thing watching — and
                // left the useful half, that you get told, to be guessed at.
                Text = $"Tell me when “{name}” changes",
                Detail = WatchDetail(profile),
                Checked = profile.Watched,
                KeepOpen = true,
                Invoke = () => ToggleWatched(profile)
            }
        };

        if (profile.Watched)
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = "Put it back automatically",

                // What it does, not what it stops doing. "Otherwise it just tells
                // you" described the unticked state from inside the ticked one,
                // which is a hint you have to hold two states in your head to read.
                // The interval is the honest answer to the question the label
                // raises — automatically, yes, but how soon? — and it is a setting
                // now, so the row shows what it is currently set to.
                Detail = $"within {ProfileGuard.PollSeconds}s",
                Checked = profile.AutoReapply,
                KeepOpen = true,
                Invoke = () => ToggleAutoReapply(profile)
            });
        }

        return entries;
    }

    /// <summary>
    /// The three things you do to a profile rather than with it, as the glyphs at the
    /// right of its row.
    ///
    /// <para>They were a page of their own until reordering became a drag. A drag
    /// needs the list in front of you, which made the list the only place a profile
    /// can be reordered from — and once it is, keeping rename and delete somewhere
    /// else means two places to maintain one profile, and a "Move up" you press while
    /// looking at a screen with nothing on it to move past.</para>
    ///
    /// <para>Update is here rather than folded into rename because it is the
    /// operation people actually mean when a profile has gone stale: same profile,
    /// same name, same hotkey, different devices. Deleting and re-adding loses all
    /// three.</para>
    /// </summary>
    private List<TrayPopup.RowAction> ProfileActions(AudioProfile profile) => new()
    {
        new TrayPopup.RowAction
        {
            Glyph = TrayPopup.RowGlyph.Rename,
            Label = "Rename this profile",
            Invoke = () => OpenInline(profile, Inline.Rename)
        },
        new TrayPopup.RowAction
        {
            Glyph = TrayPopup.RowGlyph.Update,

            // "Update it to what's in use now" rather than "Point it at the devices
            // in use now": pointing is what the code does, updating is what the
            // person wanted. The circling arrow already reads as update, and the
            // phrase is the one "Add a profile" uses, so the two operations sound
            // related — which they are.
            Label = "Update it to what's in use now",
            Invoke = () => OpenInline(profile, Inline.Update)
        },
        new TrayPopup.RowAction
        {
            Glyph = TrayPopup.RowGlyph.Delete,
            Label = "Delete this profile",
            Danger = true,
            Invoke = () => OpenInline(profile, Inline.Delete)
        }
    };

    private void OpenInline(AudioProfile profile, Inline mode)
    {
        _inlineProfileId = profile.Id;
        _inlineMode = mode;
        _reuseDevices = true;
    }

    /// <summary>Backing out of a prompt. Distinct from <see cref="CloseInline"/>, which
    /// the three commands also call on their way to changing something: this is the
    /// case where nothing happens but the row going back to normal, and so the only
    /// one that can safely skip the enumeration.</summary>
    private void CancelInline()
    {
        CloseInline();
        _reuseDevices = true;
    }

    private void CloseInline()
    {
        _inlineProfileId = null;
        _inlineMode = Inline.None;
    }

    /// <summary>
    /// The row a profile shows while it is being asked about: a name field, or a
    /// question with two buttons.
    ///
    /// <para>All three of these were modal dialogs. A dialog cannot appear over the
    /// tray popup — it closes on <c>Deactivate</c> — so each one meant the menu
    /// vanishing, a window opening somewhere else, and the menu being rebuilt
    /// afterwards to put you back. Asking on the row costs nothing but the row's own
    /// height, and the list you are asking about stays on screen while you
    /// answer.</para>
    /// </summary>
    private TrayPopup.Entry InlineEntry(AudioProfile p)
    {
        switch (_inlineMode)
        {
            case Inline.Rename:
            case Inline.NameNew:
                return new TrayPopup.EditEntry
                {
                    Key = p.Id,
                    // Sits on the subtitle line of the row, beside the keys
                    // that work, and gives way to the reason a name is refused as
                    // soon as there is one. Short because that line is shared —
                    // and the glyph that opened it has already said the long
                    // version on the same line a moment before.
                    Title = _inlineMode == Inline.NameNew
                        ? "Name this new profile"
                        : "Rename this profile",
                    Initial = p.Name,
                    Validate = candidate => NameProblem(p, candidate),
                    Commit = name => CommitRename(p, name),
                    Cancel = CancelInline
                };

            case Inline.Update:
                return new TrayPopup.PromptEntry
                {
                    Title = $"Update “{p.Name}”?",

                    // One line, on the subtitle line of the row it replaces. What
                    // it will take is the "Now" block two rows below this one, so
                    // spelling the devices out here said everything twice; what it
                    // keeps is everything else, which is shorter put that way round.
                    Detail = "Takes what is in use now; keeps the rest.",
                    ConfirmText = "Update",
                    Confirm = () => UpdateProfileDevices(p),
                    Cancel = CancelInline
                };

            default:
                return new TrayPopup.PromptEntry
                {
                    Title = $"Delete “{p.Name}”?",

                    // "Saved" is carrying the reassurance that the devices
                    // themselves are untouched, in the four characters a shared
                    // line can spare for it.
                    Detail = "Only the saved profile goes. No undo.",
                    ConfirmText = "Delete",
                    Danger = true,
                    Confirm = () => DeleteProfile(p),
                    Cancel = CancelInline
                };
        }
    }

    /// <summary>
    /// Why this name cannot be used, or null.
    ///
    /// <para>Uniqueness is not tidiness: <c>--apply "&lt;name&gt;"</c> looks profiles
    /// up by name, so two called the same thing would make one of them unreachable
    /// from the command line and a script would silently apply the wrong devices. The
    /// field says so while you type rather than rejecting the name after you have
    /// committed to it.</para>
    /// </summary>
    private string? NameProblem(AudioProfile profile, string candidate)
    {
        if (candidate.Length == 0) return "A profile needs a name.";

        return _profiles.Any(p => p.Id != profile.Id &&
                                  p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            ? "There is already a profile with that name."
            : null;
    }

    /// <summary>Everything one menu rebuild needs to know about the hardware. Read in
    /// one go and kept together so it can be reused whole — a rebuild either asked the
    /// machine or it did not, and half a snapshot would put a fresh "Now" block next
    /// to stale rows.</summary>
    private sealed record MenuDevices(IReadOnlyList<AudioDevice> Live,
                                      Dictionary<string, bool> Matches,
                                      List<(string Label, string Value)> NowRows);

    private MenuDevices? _devices;

    /// <summary>Set by the one kind of click that cannot have changed the hardware —
    /// opening or dismissing a prompt on a row. Consumed by the next
    /// <see cref="ReadDevices"/> and cleared there, so anything that forgets to set it
    /// pays for an enumeration rather than showing a stale menu.</summary>
    private bool _reuseDevices;

    /// <summary>
    /// One enumeration for the whole menu, shared across every profile — not one per
    /// profile.
    ///
    /// <para>It can cost a tenth of a second or more, which is
    /// fine once when the menu opens and not fine on every click the menu stays open
    /// for. Clicking the bin glyph cannot change which devices are plugged in, so
    /// paying for the answer again buys nothing and puts a stall between the click and
    /// the confirmation row appearing — long enough to read as the menu hesitating
    /// before it redraws.</para>
    /// </summary>
    private MenuDevices ReadDevices()
    {
        bool reuse = _reuseDevices;
        _reuseDevices = false;

        // The profile list can be replaced wholesale by a reload from disk between the
        // snapshot and its reuse, and a match keyed by an id no longer in the list is
        // not a match for anything.
        if (reuse && _devices != null && _profiles.All(p => _devices.Matches.ContainsKey(p.Id)))
        {
            return _devices;
        }

        var enumerator = AudioInterop.Enumerator;
        var live = AudioEngine.GetDevices(enumerator, Flow.Render, includeInactive: false);
        live.AddRange(AudioEngine.GetDevices(enumerator, Flow.Capture, includeInactive: false));

        var matches = new Dictionary<string, bool>();
        foreach (var p in _profiles)
        {
            matches[p.Id] = ProfileEngine.MatchesCurrent(p, enumerator, live);
        }

        return _devices = new MenuDevices(live, matches, CurrentRows(enumerator));
    }

    /// <summary>
    /// The "Now" rows, collapsed when roles agree. A machine whose three render
    /// roles all point at one device should say so in one line; only a machine
    /// that has them split needs three.
    /// </summary>
    private static List<(string Label, string Value)> CurrentRows(IMMDeviceEnumerator enumerator)
    {
        var rows = new List<(string, string)>();

        var outConsole = AudioEngine.GetDefault(enumerator, Flow.Render, Role.Console);
        var outComms = AudioEngine.GetDefault(enumerator, Flow.Render, Role.Communications);
        var input = AudioEngine.GetDefault(enumerator, Flow.Capture, Role.Console);

        rows.Add(("out", outConsole?.Display ?? "(none)"));

        if (!SameId(outConsole, outComms))
        {
            rows.Add(("calls", outComms?.Display ?? "(none)"));
        }

        rows.Add(("in", input?.Display ?? "(none)"));
        return rows;
    }

    private static bool SameId(AudioDevice? a, AudioDevice? b) =>
        string.Equals(a?.EndpointId ?? "", b?.EndpointId ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>A one-line summary of what a profile sets, for the row under its name.</summary>
    private static string Subtitle(AudioProfile profile, IReadOnlyList<AudioDevice> live)
    {
        if (profile.IsEmpty) return "no devices set";

        var parts = new List<string>();

        if (profile.Output != null) parts.Add(Label(profile.Output, live));
        if (profile.OutputComms != null) parts.Add("calls: " + Label(profile.OutputComms, live));
        if (profile.Input != null) parts.Add("mic: " + Label(profile.Input, live));

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// The compact form, because a subtitle carries up to three devices on one
    /// line. Using the full friendly name here truncated so hard that the
    /// microphone fell off the end entirely — a summary that silently drops a
    /// third of what it is summarising is worse than no summary.
    /// </summary>
    private static string Label(DeviceRef reference, IReadOnlyList<AudioDevice> live, bool full = false)
    {
        var resolution = DeviceResolver.Resolve(reference, live);
        if (resolution.Device == null) return reference.Describe();
        return full ? resolution.Device.Display : resolution.Device.ShortName;
    }

    /// <summary>
    /// Whether APS is actually watching, in a few words beside the tick.
    ///
    /// <para>The tick and this line say different things on purpose. The tick is
    /// the profile's saved intent; this is whether APS has its eye on anything
    /// right now. They disagree whenever a watched profile is not the one in
    /// effect, or when watching was stopped for the session — and a tick implying
    /// APS is guarding your devices when it is not is exactly the kind of quiet lie
    /// that makes a tool untrustworthy.</para>
    /// </summary>
    private string? WatchDetail(AudioProfile profile)
    {
        if (!profile.Watched) return null;

        if (_guard.Watched?.Id == profile.Id) return profile.AutoReapply ? "putting it back" : "watching now";
        return _activeProfile?.Id == profile.Id ? "paused for now" : "when it’s active";
    }

    /// <summary>Why a profile cannot be applied right now, or null when it can.</summary>
    private static string? UnavailableReason(AudioProfile profile, IReadOnlyList<AudioDevice> live)
    {
        if (profile.NeedsRecapture) return "needs re-capturing";
        if (profile.IsEmpty) return "no devices set";

        foreach (var reference in profile.Refs)
        {
            var resolution = DeviceResolver.Resolve(reference, live);
            if (!resolution.Resolved) return resolution.Explanation;
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Actions
    // -----------------------------------------------------------------------

    private void SwitchToProfile(AudioProfile profile)
    {
        if (AudioSafety.Apply(profile, out string message))
        {
            _activeProfile = profile;
            _notifyIcon.Text = $"{AppInfo.Name} · {profile.Name}";

            // Applying a watched profile is the moment watching starts. Switching
            // to an unwatched one is a deliberate handing of control back to
            // Windows, so whatever was being watched stops being watched.
            SyncGuard(profile, $"switched to “{profile.Name}”, which is not watched", userInitiated: true);

            RefreshMenuAndHotkeys();
        }
        else
        {
            ShowBalloon(4000, AppInfo.Name + " — Failed", message, ToolTipIcon.Error);
        }
    }

    private void UndoLastSwitch()
    {
        if (AudioSafety.Undo(out string message))
        {
            // The undo restores whatever the devices were, which is not necessarily
            // any saved profile. Nothing is held until DetectActiveProfile finds a
            // profile that genuinely matches again.
            _guard.StopWatching("the switch was undone");

            // DetectActiveProfile sets the tooltip itself, from what the devices
            // actually are now. Overwriting it here threw that away.
            DetectActiveProfile();
        }
        else
        {
            ShowBalloon(3000, AppInfo.Name + " — Failed", message, ToolTipIcon.Error);
        }
        RefreshMenuAndHotkeys();
    }

    private void CaptureCurrentProfile()
    {
        var created = ProfileEngine.Capture(UniqueName());
        _profiles.Add(created);

        if (!ProfileManager.TrySaveProfiles(_profiles, out string saveError))
        {
            _profiles.Remove(created);
            ShowBalloon(4000, AppInfo.Name + " — Failed", saveError, ToolTipIcon.Error);
            return;
        }

        MarkProfileFileHandled();
        _activeProfile = created;

        // The new row opens as a name field with the placeholder selected. It has a
        // name nobody chose and the one thing everybody wants to do next is fix that,
        // and this is the one case where the field can be opened without anybody
        // having asked for it — the profile did not exist a moment ago.
        //
        // No balloon: the menu stays open, so the new row appearing with its live dot
        // lit says "saved" better than a notification that has to be read before it
        // fades.
        OpenInline(created, Inline.NameNew);
    }

    private string UniqueName()
    {
        int n = _profiles.Count + 1;
        while (_profiles.Any(p => p.Name.Equals($"Profile {n}", StringComparison.OrdinalIgnoreCase))) n++;
        return $"Profile {n}";
    }

    // -----------------------------------------------------------------------
    // Managing profiles
    // -----------------------------------------------------------------------

    /// <summary>
    /// Renames a profile.
    ///
    /// <para>Names are kept unique, and not for tidiness: <c>--apply "&lt;name&gt;"</c>
    /// looks profiles up by name, so two called the same thing would make one of
    /// them unreachable from the command line and a script silently apply the wrong
    /// devices. The dialog says so while you type rather than rejecting the name
    /// after you have committed to it.</para>
    /// </summary>
    /// <summary>
    /// Takes the name the field was left holding. The field itself has already
    /// refused an empty or duplicate one — see <see cref="NameProblem"/> — so this
    /// only has to save it.
    /// </summary>
    private void CommitRename(AudioProfile profile, string chosen)
    {
        CloseInline();

        if (chosen.Equals(profile.Name, StringComparison.Ordinal)) return;

        string previous = profile.Name;
        profile.Name = chosen;

        if (ProfileManager.TrySaveProfiles(_profiles, out string saveError))
        {
            MarkProfileFileHandled();
            if (_activeProfile?.Id == profile.Id) _notifyIcon.Text = $"{AppInfo.Name} · {profile.Name}";
        }
        else
        {
            profile.Name = previous;
            ShowBalloon(4000, AppInfo.Name + " — Failed", saveError, ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// Points an existing profile at whatever the devices are right now, keeping its
    /// name, hotkey, position and settings.
    ///
    /// <para>The alternative — delete and add it again — loses all four, and the
    /// hotkey silently along with them. This is the operation people actually mean
    /// when a profile has gone stale: same profile, different devices.</para>
    /// </summary>
    private void UpdateProfileDevices(AudioProfile profile)
    {
        CloseInline();

        var captured = ProfileEngine.Capture(profile.Name);
        var previous = profile.Clone(newId: false);

        profile.Output = captured.Output;
        profile.OutputComms = captured.OutputComms;
        profile.Input = captured.Input;

        if (ProfileManager.TrySaveProfiles(_profiles, out string saveError))
        {
            MarkProfileFileHandled();
            _activeProfile = profile;

            // The guard is defending a snapshot of the devices this profile used to
            // name. It is now watching for drift away from the wrong thing, so it is
            // re-armed against what the profile says today — and the profile matches
            // the machine by construction, so re-arming can change nothing.
            SyncGuard(profile, $"“{profile.Name}” is not being watched", userInitiated: true);
        }
        else
        {
            profile.Output = previous.Output;
            profile.OutputComms = previous.OutputComms;
            profile.Input = previous.Input;
            ShowBalloon(4000, AppInfo.Name + " — Failed", saveError, ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// Puts a profile at a position in the list — what a drag in the tray commits,
    /// and what Ctrl+Up / Ctrl+Down do a step at a time.
    ///
    /// <para>Order is the order in the settings file, which is the order the tray
    /// shows and therefore the order that matters.</para>
    ///
    /// <para>This replaced a pair of "Move up" / "Move down" commands on the
    /// profile's own page. They worked, and they were the wrong shape: the page they
    /// lived on showed one profile, so the list the move was rearranging was the one
    /// thing not on screen while you rearranged it. A drag can only be done on a list
    /// you can see, which is the whole argument for it.</para>
    /// </summary>
    private void MoveProfileTo(AudioProfile profile, int to)
    {
        int from = _profiles.IndexOf(profile);
        if (from < 0) return;

        to = Math.Clamp(to, 0, _profiles.Count - 1);
        if (to == from) return;

        _profiles.RemoveAt(from);
        _profiles.Insert(to, profile);

        if (ProfileManager.TrySaveProfiles(_profiles, out string saveError))
        {
            MarkProfileFileHandled();
        }
        else
        {
            _profiles.RemoveAt(to);
            _profiles.Insert(from, profile);
            ShowBalloon(4000, AppInfo.Name + " — Failed", saveError, ToolTipIcon.Error);
        }
    }

    private void DeleteProfile(AudioProfile profile)
    {
        CloseInline();

        int at = _profiles.IndexOf(profile);
        _profiles.Remove(profile);

        if (ProfileManager.TrySaveProfiles(_profiles, out string saveError))
        {
            MarkProfileFileHandled();

            if (_activeProfile?.Id == profile.Id)
            {
                _activeProfile = null;
                _notifyIcon.Text = AppInfo.Name;
            }

            // Nothing left to watch, and the hotkey has to stop working the moment
            // the profile it applied is gone.
            if (_guard.Watched?.Id == profile.Id) _guard.StopWatching($"“{profile.Name}” was deleted");
            RegisterHotkeys();
        }
        else
        {
            _profiles.Insert(Math.Clamp(at, 0, _profiles.Count), profile);
            ShowBalloon(4000, AppInfo.Name + " — Failed", saveError, ToolTipIcon.Error);
        }
    }

    private void ToggleWatched(AudioProfile profile)
    {
        _reuseDevices = true;
        profile.Watched = !profile.Watched;

        if (!ProfileManager.TrySaveProfiles(_profiles, out string saveError))
        {
            profile.Watched = !profile.Watched;
            ShowBalloon(4000, AppInfo.Name + " — Failed", saveError, ToolTipIcon.Error);
            return;
        }

        MarkProfileFileHandled();

        // Ticking the box while this profile is the live one starts watching it
        // immediately, rather than only from the next apply. The tick has to mean
        // what it says the moment it is ticked.
        if (LooksLive(profile))
        {
            SyncGuard(profile, $"you stopped watching “{profile.Name}”", userInitiated: true);
        }
    }

    /// <summary>
    /// Turns automatic switching back on or off for a profile.
    ///
    /// <para>Takes effect on the profile being watched right now, not only from the
    /// next apply: a box that means something different from the moment it is
    /// ticked is a box nobody can trust.</para>
    /// </summary>
    private void ToggleAutoReapply(AudioProfile profile)
    {
        _reuseDevices = true;
        profile.AutoReapply = !profile.AutoReapply;

        if (!ProfileManager.TrySaveProfiles(_profiles, out string saveError))
        {
            profile.AutoReapply = !profile.AutoReapply;
            ShowBalloon(4000, AppInfo.Name + " — Failed", saveError, ToolTipIcon.Error);
            return;
        }

        MarkProfileFileHandled();
    }

    /// <summary>
    /// Whether the machine looks like this profile, answered from the snapshot the
    /// open menu was built from rather than by asking the hardware again.
    ///
    /// <para>Ticking a box cannot change which devices are plugged in, and
    /// <see cref="IsLiveProfile"/> is a second full enumeration on top of the one the
    /// rebuild is about to do — two tenths of a second between the click and the
    /// tick appearing. Falls back to asking when there is no snapshot, so a caller
    /// reached without a menu open still gets an answer.</para>
    /// </summary>
    private bool LooksLive(AudioProfile profile) =>
        _devices != null && _devices.Matches.TryGetValue(profile.Id, out bool match)
            ? match
            : IsLiveProfile(profile);

    /// <summary>Whether the machine currently looks like this profile — as opposed
    /// to it merely being the last one clicked.</summary>
    private static bool IsLiveProfile(AudioProfile profile)
    {
        var enumerator = AudioInterop.Enumerator;
        var live = AudioEngine.GetDevices(enumerator, Flow.Render, includeInactive: false);
        live.AddRange(AudioEngine.GetDevices(enumerator, Flow.Capture, includeInactive: false));
        return ProfileEngine.MatchesCurrent(profile, enumerator, live);
    }

    /// <summary>
    /// Opens the settings file in Notepad, explicitly.
    ///
    /// <para><b>Not</b> ShellExecute on the file itself. The settings file is named
    /// <c>.aps</c>, so Windows sees the extension "aps" and hands it to whatever
    /// claims it on the system — an extension this short and generic is exactly
    /// the kind another installed app registers a file association for. A menu item
    /// called "Edit profiles" that launches some unrelated app is not a bug the user
    /// can be expected to diagnose.</para>
    ///
    /// <para>Notepad is named explicitly because it is guaranteed present on every
    /// Windows install and is guaranteed to be a text editor.</para>
    /// </summary>
    private void OpenSettingsFile()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                ProfileManager.SaveProfiles(_profiles);
                MarkProfileFileHandled();
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = $"\"{AppPaths.SettingsFile}\"",
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            ShowBalloon(5000, AppInfo.Name + " — Failed",
                $"Could not open {AppPaths.SettingsFile}: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void ToggleStartup()
    {
        _reuseDevices = true;

        bool wanted = !StartupRegistration.IsEnabled;

        if (!StartupRegistration.Set(wanted, out string error))
        {
            ShowBalloon(4000, AppInfo.Name + " — Failed",
                $"Could not change the startup setting: {error}", ToolTipIcon.Error);
            return;
        }

        ShowBalloon(2000, AppInfo.Name,
            wanted ? "Will start with Windows." : "Will no longer start with Windows.", ToolTipIcon.Info);
    }

    private void DetectActiveProfile()
    {
        var matching = ProfileEngine.FindMatching(_profiles);

        _activeProfile = matching ?? _activeProfile;
        _notifyIcon.Text = _activeProfile != null
            ? $"{AppInfo.Name} · {_activeProfile.Name}"
            : AppInfo.Name;

        // Watching starts when a watched profile is genuinely in effect, which
        // includes launch: APS starts with Windows, so if watching only began at
        // the first click, the profile would go unguarded for exactly the part of
        // the day nobody is looking at the tray.
        //
        // A profile that matches needs no correction to arm, so arming here can
        // never itself change a device.
        //
        // _activeProfile is not enough — it falls back to the last profile clicked
        // when nothing matches, and watching a profile the machine is not actually
        // on would fight every change rather than guard anything.
        SyncGuard(matching, "the devices no longer match a watched profile");
    }

    /// <summary>Builds tray entries for the --screenshot-menu diagnostic.</summary>
    /// <param name="inlineAt">Index of the profile whose row should be drawn in an
    /// inline state, or -1 for a plain list.</param>
    /// <param name="inlineState">"rename", "update" or "delete".</param>
    /// <param name="chosenAt">
    /// Renders the menu as it looks when the machine matches no profile at all, with
    /// this one marked as the last that was picked.
    ///
    /// <para>Forced rather than observed, because the state cannot be reached from a
    /// command line: it needs the real devices to have moved away from every profile
    /// on the machine, and a diagnostic that can only be run by first breaking your
    /// own audio is a diagnostic nobody runs. Everything it forces is a flag the real
    /// menu sets from <c>liveMatches</c>; what is being checked is the drawing.</para>
    /// </param>
    internal static List<TrayPopup.Entry> BuildPreviewEntries(
        List<AudioProfile> profiles, int inlineAt = -1, string? inlineState = null,
        int chosenAt = -1)
    {
        var enumerator = AudioInterop.Enumerator;
        var live = AudioEngine.GetDevices(enumerator, Flow.Render, includeInactive: false);
        live.AddRange(AudioEngine.GetDevices(enumerator, Flow.Capture, includeInactive: false));

        var entries = new List<TrayPopup.Entry>
        {
            new TrayPopup.HeadingEntry
            {
                Text = "PROFILES",
                Hint = profiles.Count > 1 ? "drag to reorder" : null
            }
        };

        for (int index = 0; index < profiles.Count; index++)
        {
            var p = profiles[index];

            if (index == inlineAt && inlineState != null)
            {
                entries.Add(PreviewInlineEntry(p, inlineState));
                continue;
            }

            string? unavailable = UnavailableReason(p, live);
            entries.Add(new TrayPopup.ProfileEntry
            {
                Profile = p,
                IsLive = chosenAt < 0 && ProfileEngine.MatchesCurrent(p, enumerator, live),
                IsChosen = index == chosenAt,
                Subtitle = Subtitle(p, live),
                UnavailableReason = unavailable,
                Enabled = unavailable == null,
                Invoke = () => { },

                // Labels and glyphs only — what the screenshot is evidence about is
                // whether three of them fit beside a long device name, at high DPI.
                Actions = new List<TrayPopup.RowAction>
                {
                    new()
                    {
                        Glyph = TrayPopup.RowGlyph.Rename,
                        Label = "Rename this profile",
                        Invoke = () => { }
                    },
                    new()
                    {
                        Glyph = TrayPopup.RowGlyph.Update,
                        Label = "Update it to what's in use now",
                        Invoke = () => { }
                    },
                    new()
                    {
                        Glyph = TrayPopup.RowGlyph.Delete,
                        Label = "Delete this profile",
                        Danger = true,
                        Invoke = () => { }
                    }
                }
            });
        }

        entries.Add(new TrayPopup.SeparatorEntry());

        // The real menu's rule, kept in step by hand like everything else here.
        bool anyLive = entries.OfType<TrayPopup.ProfileEntry>().Any(e => e.IsLive);
        if (profiles.Count > 0 && !anyLive)
        {
            var chosen = chosenAt >= 0 && chosenAt < profiles.Count ? profiles[chosenAt] : null;
            entries.Add(new TrayPopup.HeadingEntry
            {
                Text = "NOT ONE OF YOUR PROFILES",
                Hint = chosen != null ? $"last picked: {chosen.Name}" : null
            });
        }

        entries.Add(new TrayPopup.StatusEntry { Rows = CurrentRows(enumerator) });
        entries.Add(new TrayPopup.SeparatorEntry());
        entries.Add(new TrayPopup.CommandEntry { Text = "Undo last switch", Detail = "14s", Emphasis = true, Invoke = () => { } });
        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Add a profile",
            Detail = "from what's in use now",
            Invoke = () => { }
        });
        entries.Add(new TrayPopup.CommandEntry { Text = "Edit profiles…", Detail = ".aps", Invoke = () => { } });
        entries.Add(new TrayPopup.SeparatorEntry());

        var first = profiles.FirstOrDefault();
        if (first != null)
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = $"Tell me when “{first.Name}” changes",
                // The live tray reads this from ProfileGuard; the preview has no
                // guard, so it renders the state the screenshot is for.
                Detail = first.Watched ? (first.AutoReapply ? "putting it back" : "watching now") : null,
                Checked = first.Watched,
                Invoke = () => { }
            });
        }

        if (first is { Watched: true })
        {
            entries.Add(new TrayPopup.CommandEntry
            {
                Text = "Put it back automatically",
                Detail = $"within {ProfileGuard.PollSeconds}s",
                Checked = first.AutoReapply,
                Invoke = () => { }
            });
        }

        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Check for updates…",
            Detail = AppInfo.Version,
            Invoke = () => { }
        });
        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Include alpha builds",
            Checked = false,
            Invoke = () => { }
        });
        entries.Add(new TrayPopup.CommandEntry
        {
            Text = "Start with Windows",
            Checked = StartupRegistration.IsEnabled,
            Invoke = () => { }
        });
        entries.Add(new TrayPopup.CommandEntry { Text = "Exit", Invoke = () => { } });
        return entries;
    }

    /// <summary>
    /// One row's inline state, for the diagnostic.
    ///
    /// <para>Built here rather than by pointing <see cref="InlineEntry"/> at a
    /// pretend context, for the same reason <see cref="BuildPreviewEntries"/> exists
    /// at all: that one reads the guard and the live profile, neither of which a
    /// command-line render has, and threading "pretend" state through the real menu
    /// is how a screenshot stops being evidence about the real menu. The words and
    /// the shape are copied; nothing behind them is.</para>
    /// </summary>
    private static TrayPopup.Entry PreviewInlineEntry(AudioProfile p, string state)
    {
        switch (state.ToLowerInvariant())
        {
            case "rename":
                return new TrayPopup.EditEntry
                {
                    Key = p.Id,
                    Title = "Rename this profile",
                    Initial = p.Name,
                    Validate = _ => null,
                    Commit = _ => { },
                    Cancel = () => { }
                };

            case "update":
                return new TrayPopup.PromptEntry
                {
                    Title = $"Update “{p.Name}”?",
                    Detail = "Takes what is in use now; keeps the rest.",
                    ConfirmText = "Update",
                    Confirm = () => { },
                    Cancel = () => { }
                };

            default:
                return new TrayPopup.PromptEntry
                {
                    Title = $"Delete “{p.Name}”?",
                    Detail = "Only the saved profile goes. No undo.",
                    ConfirmText = "Delete",
                    Danger = true,
                    Confirm = () => { },
                    Cancel = () => { }
                };
        }
    }

    protected override void ExitThreadCore()
    {
        _guard.Dispose();
        _undoTimer.Stop();
        _undoTimer.Dispose();
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _profileReloadTimer.Stop();
        _profileReloadTimer.Dispose();
        _profileWatcher.Dispose();
        TrayPopup.Dismiss();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _hotkeyManager.Dispose();
        base.ExitThreadCore();
    }
}
