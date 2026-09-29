namespace APS;

/// <summary>The devices moved out from under a watched profile.</summary>
internal sealed class DriftEventArgs : EventArgs
{
    public required AudioProfile Profile { get; init; }

    /// <summary>What has the role now, in short form. "something else" when it
    /// could not be named — a toast must never show a raw endpoint id.</summary>
    public string NowOn { get; init; } = "";

    /// <summary>Which role moved, in the words Windows' own Sound settings use.</summary>
    public string What { get; init; } = "";

    /// <summary>False when the profile's device is no longer connected, in which
    /// case offering to switch back would only produce a failure.</summary>
    public bool CanReapply { get; init; }
}

/// <summary>A profile APS put back by itself, for a profile with AutoReapply on.</summary>
internal sealed class ReapplyEventArgs : EventArgs
{
    public required AudioProfile Profile { get; init; }
    public string NowOn { get; init; } = "";
    public string What { get; init; } = "";
}

/// <summary>Why the guard stopped watching. Reasons are sentences, not enum names.</summary>
internal sealed class GuardStoppedEventArgs : EventArgs
{
    public string ProfileName { get; init; } = "";
    public string Reason { get; init; } = "";

    /// <summary>True when the guard stopped itself rather than being asked to.</summary>
    public bool Automatic { get; init; }
}

/// <summary>
/// Notices when a watched profile stops being the one in effect.
///
/// <para><b>Why this polls rather than subscribing.</b> Windows does publish
/// device-change notifications, through <c>IMMNotificationClient</c>, and the
/// first build of this used them. Registering that callback destabilises the
/// process: under a burst of default changes it dies with an access violation on
/// the notification thread — no managed frames, in coreclr or combase depending
/// on timing, and once with a heap-corruption code instead. It reproduced in the
/// real tray, not only in a diagnostic, and it reproduced with a callback object
/// that did nothing at all but return S_OK, which rules out everything APS does
/// in response. The short version is that the notification client is not safe to
/// register here and the reason is not in APS's own code.</para>
///
/// <para>So: six cheap reads on a timer instead. It costs
/// <see cref="AudioEngine.GetDefaultId"/> once per claimed role every
/// <see cref="PollMs"/> ms — two COM calls each, no enumeration, no property
/// stores — and it uses only the call path that <c>--apply</c> has exercised
/// thousands of times without incident. The cost is latency: a change is noticed
/// on the next poll rather than instantly. For "your devices moved, do you want
/// them back?" that is not a meaningful difference, and it buys an app that does
/// not fall over.</para>
///
/// <para><b>What it does about drift</b> depends on the profile.
/// <see cref="AudioProfile.AutoReapply"/> off — the default — raises
/// <see cref="Drifted"/> once and stops watching: the profile genuinely is not in
/// effect any more, and the tray offers to switch back. On, and it puts the
/// devices back itself, with a cap so that losing an argument with another
/// application ends rather than continuing forever in the background.</para>
///
/// <para><b>Threading.</b> A WinForms timer, so every tick runs on the UI thread,
/// which is the one thread APS does audio work on.</para>
/// </summary>
internal sealed class ProfileGuard : IDisposable
{
    /// <summary>
    /// How often to look, in seconds, when the settings file does not say otherwise.
    ///
    /// <para><b>Five seconds, having been two and then thirty.</b> Two was chosen so
    /// a toast would land while you still remembered changing something, and it fires
    /// six COM calls 43,200 times a day on a machine that is idle for most of them.
    /// Thirty was the correction, and it overshot. The argument for it — that being
    /// told half a minute later is the same information — holds for the toast and not
    /// for <see cref="AudioProfile.AutoReapply"/>, where the interval is exactly how
    /// long somebody spends listening to the wrong device before APS puts it back.
    /// Half a minute of that is a long time when the reason you turned it on was not
    /// wanting to notice at all.</para>
    ///
    /// <para>Five is where it landed after being tried at ten and still reading as a
    /// lag. It is 17,280 ticks a day against two seconds' 43,200 — the same order of
    /// magnitude, which is the honest way to put it — and the reason that is
    /// affordable is what each tick actually costs: <see cref="AudioEngine.GetDefaultId"/>
    /// per claimed role, two COM calls each, no enumeration and no property stores.
    /// The thing thirty was protecting against was never this loop.</para>
    ///
    /// <para>None of this governs the tray menu. The menu reads the live devices
    /// itself every time it opens, and <see cref="CheckNow"/> has the guard look at
    /// the same moment, so the interval only decides what happens while nobody is
    /// looking.</para>
    /// </summary>
    public const int DefaultPollSeconds = 5;

    /// <summary>
    /// Bounds on a hand-edited interval.
    ///
    /// <para>There is no UI for this — "Edit profiles…" opens the settings file — so
    /// the number arrives unvalidated and has to be survivable at both ends. Two at
    /// the bottom because that is what the first build did, which is a precedent
    /// rather than a recommendation. Five minutes at the top because past that
    /// AutoReapply has stopped being a correction, and because
    /// <see cref="RunawayWindowSeconds"/> has to stay longer than
    /// <see cref="RunawayLimit"/> polls for the cap to be reachable at all.</para>
    ///
    /// <para>Clamped where it is read rather than where it is loaded, so an
    /// out-of-range number in the file is ignored rather than quietly rewritten. A
    /// settings file that changes under you because you typed something the app did
    /// not like is worse than one that politely does something sane.</para>
    /// </summary>
    public const int MinPollSeconds = 2;

    /// <inheritdoc cref="MinPollSeconds"/>
    public const int MaxPollSeconds = 300;

    /// <summary>The configured interval, bounded. Missing and zero both read as "not
    /// set", which is the default rather than a busy loop.</summary>
    public static int PollSeconds =>
        ProfileManager.PollSeconds <= 0
            ? DefaultPollSeconds
            : Math.Clamp(ProfileManager.PollSeconds, MinPollSeconds, MaxPollSeconds);

    public static int PollMs => PollSeconds * 1000;

    /// <summary>Reapplies allowed inside <see cref="RunawayWindowSeconds"/> before
    /// the guard concedes. Only reachable with AutoReapply on.</summary>
    public const int RunawayLimit = 3;

    /// <summary>
    /// The window the runaway cap counts in.
    ///
    /// <para>Derived from <see cref="PollSeconds"/> rather than independent of it:
    /// corrections can only happen one per poll, so a window shorter than
    /// <see cref="RunawayLimit"/> polls would prune every earlier reapply before the
    /// next one arrived and the cap could never trip at all. Twenty polls is
    /// comfortably more than the three it takes to conclude that something else is
    /// fighting back.</para>
    ///
    /// <para>Ten minutes is the floor under that, because at the default interval
    /// twenty polls is only 200 seconds and three corrections that far apart are not
    /// yet a fight. The formula takes over once the interval is long enough to need
    /// it, which is the case the derivation exists for.</para>
    /// </summary>
    public static int RunawayWindowSeconds => Math.Max(600, PollSeconds * 20);

    private readonly System.Windows.Forms.Timer _poll;
    private readonly List<DateTime> _reapplies = new();

    private AudioProfile? _watched;

    /// <summary>The endpoint each claimed role held when watching started. Drift is
    /// measured against this rather than against the profile's stored ids, because
    /// an apply can heal an id and the two would then disagree.</summary>
    private Dictionary<(Flow Flow, Role Role), string> _expected = new();

    /// <summary>A tick can outlast its interval — a reapply is several COM calls —
    /// and the timer would happily start a second one on top.</summary>
    private bool _checking;

    public event EventHandler<DriftEventArgs>? Drifted;
    public event EventHandler<ReapplyEventArgs>? Reapplied;
    public event EventHandler<GuardStoppedEventArgs>? Stopped;

    public ProfileGuard()
    {
        _poll = new System.Windows.Forms.Timer { Interval = PollMs };
        _poll.Tick += (_, _) => Tick();
    }

    /// <summary>
    /// Picks up an interval changed in the settings file.
    ///
    /// <para>Only assigned when it differs, because assigning Interval restarts a
    /// running timer — doing it unconditionally on every reload would push the next
    /// tick back each time and a file being written repeatedly could starve the poll
    /// entirely.</para>
    /// </summary>
    public void RefreshInterval()
    {
        if (_poll.Interval != PollMs) _poll.Interval = PollMs;
    }

    public bool IsWatching => _watched != null;

    public AudioProfile? Watched => _watched;

    /// <summary>
    /// True once a runaway has been conceded. Cleared only by a person doing
    /// something, because the tray re-arms whenever a watched profile is found to be
    /// live — and the moment after conceding it usually still is, so re-arming on
    /// its own would restart the argument with a fresh budget. A cap is only a cap
    /// if giving up sticks.
    /// </summary>
    public bool GaveUp { get; private set; }

    /// <summary>
    /// Starts watching a profile that is live right now.
    ///
    /// <para>The caller decides whether the profile is watched and whether it is
    /// genuinely in effect; this trusts both, and snapshots the current defaults as
    /// the state to defend.</para>
    /// </summary>
    /// <param name="userInitiated">True when a person asked — clicked a profile,
    /// ticked the box. Only that clears <see cref="GaveUp"/>.</param>
    public void Watch(AudioProfile profile, bool userInitiated = false)
    {
        if (GaveUp && !userInitiated) return;

        bool same = _watched != null && _watched.Id == profile.Id;

        GaveUp = false;
        _watched = profile;
        _expected = Snapshot(profile);

        // The budget belongs to the session, not to the apply, so re-applying the
        // same profile cannot be used to reset a runaway.
        if (!same) _reapplies.Clear();

        // Before Start, so a profile watched for the first time after the settings
        // were edited uses the interval that is in the file rather than the one that
        // was there when the guard was constructed.
        RefreshInterval();
        _poll.Start();
    }

    /// <summary>
    /// Looks now instead of waiting for the next poll.
    ///
    /// <para>Called when the tray menu is opened. With a 30-second poll the menu
    /// could otherwise be built from an answer half a minute old and show a live dot
    /// beside a profile that stopped being live twenty seconds ago — and the menu is
    /// precisely where "which profile am I on?" gets asked.</para>
    ///
    /// <para>Safe when nothing is being watched: <see cref="Tick"/> returns
    /// immediately.</para>
    /// </summary>
    public void CheckNow() => Tick();

    /// <summary>Stops watching, and says why in words a person can read.</summary>
    public void StopWatching(string reason) => Stop(reason, automatic: false);

    private void Stop(string reason, bool automatic)
    {
        if (_watched == null) return;

        string name = _watched.Name;

        _watched = null;
        _expected = new Dictionary<(Flow, Role), string>();
        _poll.Stop();

        Stopped?.Invoke(this, new GuardStoppedEventArgs
        {
            ProfileName = name,
            Reason = reason,
            Automatic = automatic
        });
    }

    /// <summary>What each role the profile claims is set to right now.</summary>
    private static Dictionary<(Flow Flow, Role Role), string> Snapshot(AudioProfile profile)
    {
        var enumerator = AudioInterop.Enumerator;
        var result = new Dictionary<(Flow, Role), string>();

        foreach (var reference in profile.Refs)
        {
            foreach (var role in reference.Roles)
            {
                result[(reference.Flow, role)] = AudioEngine.GetDefaultId(enumerator, reference.Flow, role);
            }
        }

        return result;
    }

    private void Tick()
    {
        if (_watched == null || _checking) return;

        _checking = true;
        try
        {
            Check(_watched);
        }
        catch
        {
            // A poll that fails is not worth stopping over, and certainly not worth
            // a dialog. There will be another.
        }
        finally
        {
            _checking = false;
        }
    }

    private void Check(AudioProfile profile)
    {
        var enumerator = AudioInterop.Enumerator;

        (Flow Flow, Role Role)? moved = null;
        string nowOnId = "";

        foreach (var (key, expected) in _expected)
        {
            string live = AudioEngine.GetDefaultId(enumerator, key.Flow, key.Role);

            // An empty read is not drift. It means the query failed or Windows has
            // no default for that role at this instant — during a device transition
            // both are momentary, and treating either as "someone took your device"
            // would fire a toast at the worst possible moment.
            if (live.Length == 0 || Same(live, expected)) continue;

            moved = key;
            nowOnId = live;
            break;
        }

        if (moved == null) return;

        var (flow, role) = moved.Value;

        // Named from a real enumeration, which only happens once drift is certain —
        // the whole point of the cheap poll is not to do this every two seconds.
        var liveDevices = AudioEngine.GetAllDevices(includeInactive: false);
        string nowOn = liveDevices
            .FirstOrDefault(d => string.Equals(d.EndpointId, nowOnId, StringComparison.OrdinalIgnoreCase))
            ?.ShortName ?? "something else";

        string what = Describe(flow, role);

        // Can the profile still be applied at all? A headset that was unplugged is
        // not an override to resist or an offer to make — Windows moving off a
        // device that no longer exists is correct behaviour.
        //
        // Nullable rather than FirstOrDefault: Resolution is a struct, so "none
        // found" would come back as a default whose Explanation is null, and the
        // check below would read as "blocked" on a profile that resolved fine.
        Resolution? blocked = null;
        foreach (var reference in profile.Refs)
        {
            var resolution = DeviceResolver.Resolve(reference, liveDevices);
            if (resolution.Resolved) continue;

            blocked = resolution;
            break;
        }

        if (blocked != null)
        {
            Drifted?.Invoke(this, new DriftEventArgs
            {
                Profile = profile,
                NowOn = nowOn,
                What = what,
                CanReapply = false
            });

            Stop(blocked.Value.Explanation, automatic: true);
            return;
        }

        if (!profile.AutoReapply)
        {
            // The profile is not in effect any more, so there is nothing left to
            // watch. Stopping here is also what keeps this from nagging: one toast
            // per change, not one per poll until you give in.
            Drifted?.Invoke(this, new DriftEventArgs
            {
                Profile = profile,
                NowOn = nowOn,
                What = what,
                CanReapply = true
            });

            Stop($"{what} was changed to {nowOn}", automatic: true);
            return;
        }

        PruneReapplies();
        if (_reapplies.Count >= RunawayLimit)
        {
            GaveUp = true;
            Stop("something else kept changing it back", automatic: true);
            return;
        }

        if (!ProfileEngine.Apply(profile, out string error))
        {
            Stop(error, automatic: true);
            return;
        }

        _reapplies.Add(DateTime.UtcNow);
        _expected = Snapshot(profile);

        Reapplied?.Invoke(this, new ReapplyEventArgs
        {
            Profile = profile,
            NowOn = nowOn,
            What = what
        });
    }

    private void PruneReapplies()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-RunawayWindowSeconds);
        _reapplies.RemoveAll(t => t < cutoff);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A role in the words Windows' own Sound settings use, rather than
    /// the API's.</summary>
    private static string Describe(Flow flow, Role role)
    {
        if (flow == Flow.Capture) return "your microphone";
        return role == Role.Communications ? "your device for calls" : "your default device";
    }

    public void Dispose()
    {
        _poll.Stop();
        _poll.Dispose();
        _watched = null;
    }
}
