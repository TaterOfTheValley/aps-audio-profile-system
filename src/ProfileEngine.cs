using System.Text;

namespace APS;

/// <summary>
/// Capturing, comparing and applying profiles — the layer between the saved data
/// and the Windows audio configuration.
/// </summary>
internal static class ProfileEngine
{
    /// <summary>
    /// Records what APS itself just set, so the profile guard can tell its own
    /// changes from someone else's.
    ///
    /// As measured: setting a role to the device it *already is* still
    /// fires a full notification burst, so suppression cannot be "ignore events
    /// whose value already matches" — it has to be a time-windowed match on what
    /// was actually set. Recorded here because this is the only place that sets
    /// anything.
    /// </summary>
    public static readonly List<(Flow Flow, Role Role, string EndpointId, DateTime WhenUtc)> RecentSelfChanges = new();

    /// <summary>
    /// Records one role APS just set. Public rather than private because
    /// <see cref="PinEnforcer"/> also sets roles, and a correction that did not
    /// register itself here would be seen as an external override on the very
    /// next callback — the app chasing its own tail until the runaway cap stopped
    /// it. Anything in APS that calls PolicyConfig.SetDefault must call this.
    /// </summary>
    public static void RecordSelfChange(Flow flow, Role role, string endpointId)
    {
        lock (RecentSelfChanges)
        {
            RecentSelfChanges.Add((flow, role, endpointId, DateTime.UtcNow));

            // Bounded: only the last few seconds can ever matter to the enforcer.
            var cutoff = DateTime.UtcNow.AddSeconds(-10);
            RecentSelfChanges.RemoveAll(c => c.WhenUtc < cutoff);
        }
    }

    // -----------------------------------------------------------------------
    // Capture
    // -----------------------------------------------------------------------

    /// <summary>
    /// Snapshots the current defaults as a new profile.
    ///
    /// Roles are assigned so that the common case — all three agreeing — produces
    /// one output reference claiming all three, rather than two references saying
    /// the same thing. See AudioProfile.OutputComms for why the role list, not the
    /// slot, is the authority.
    /// </summary>
    public static AudioProfile Capture(string name)
    {
        var enumerator = AudioInterop.Enumerator;

        var profile = new AudioProfile { Name = name };

        var console = AudioEngine.GetDefault(enumerator, Flow.Render, Role.Console);
        var multimedia = AudioEngine.GetDefault(enumerator, Flow.Render, Role.Multimedia);
        var comms = AudioEngine.GetDefault(enumerator, Flow.Render, Role.Communications);

        if (console != null)
        {
            var roles = new List<Role> { Role.Console };

            // Windows links Console and Multimedia, so they almost always agree.
            // Only split them if the system really has them apart.
            if (SameId(console, multimedia)) roles.Add(Role.Multimedia);
            if (SameId(console, comms)) roles.Add(Role.Communications);

            profile.Output = DeviceRef.From(console, roles.ToArray());
        }

        if (multimedia != null && !SameId(console, multimedia))
        {
            // Rare, but recording it wrongly would silently change the machine.
            profile.Output ??= DeviceRef.From(multimedia, Role.Multimedia);
        }

        if (comms != null && !SameId(console, comms))
        {
            profile.OutputComms = DeviceRef.From(comms, Role.Communications);
        }

        var input = AudioEngine.GetDefault(enumerator, Flow.Capture, Role.Console);
        var inputMm = AudioEngine.GetDefault(enumerator, Flow.Capture, Role.Multimedia);
        var inputComms = AudioEngine.GetDefault(enumerator, Flow.Capture, Role.Communications);

        if (input != null)
        {
            var roles = new List<Role> { Role.Console };
            if (SameId(input, inputMm)) roles.Add(Role.Multimedia);
            if (SameId(input, inputComms)) roles.Add(Role.Communications);
            profile.Input = DeviceRef.From(input, roles.ToArray());
        }

        return profile;
    }

    // -----------------------------------------------------------------------
    // Apply
    // -----------------------------------------------------------------------

    /// <summary>
    /// Applies a profile, or applies nothing at all.
    ///
    /// <para><b>All-or-nothing is deliberate.</b> If any device cannot be
    /// resolved, nothing is set and the caller is told which one. A half-applied
    /// audio profile — speakers moved, microphone not — is worse than no change,
    /// because it produces exactly the "which device am I actually on?" confusion
    /// this app exists to remove, while looking like it worked.</para>
    /// </summary>
    public static bool Apply(AudioProfile profile, out string message)
    {
        if (profile.IsEmpty)
        {
            message = $"'{profile.Name}' has no devices set, so there is nothing to apply.";
            return false;
        }

        var live = AudioEngine.GetAllDevices(includeInactive: false);
        var resolutions = DeviceResolver.ResolveProfile(profile, live);

        // Check everything before changing anything.
        var blocked = resolutions.Where(r => !r.Value.Resolved).ToList();
        if (blocked.Count > 0)
        {
            message = blocked.Count == 1
                ? blocked[0].Value.Explanation
                : "Cannot apply: " + string.Join("; ", blocked.Select(b => b.Value.Explanation));
            return false;
        }

        var failures = new List<string>();
        bool healed = false;

        foreach (var (reference, resolution) in resolutions)
        {
            var device = resolution.Device!;

            if (DeviceResolver.HealInPlace(reference, resolution)) healed = true;

            foreach (var role in reference.Roles)
            {
                if (PolicyConfig.SetDefault(device.EndpointId, role, out string error))
                {
                    RecordSelfChange(reference.Flow, role, device.EndpointId);
                }
                else
                {
                    failures.Add($"{device.Display} as {Describe(role)}: {error}");
                }
            }
        }

        // A reference that healed now points at the id it was actually found at,
        // so the slow fingerprint path runs once per port change, not forever.
        // Saved once here rather than once per reference.
        if (healed)
        {
            var all = ProfileManager.LoadProfiles();
            var stored = all.FirstOrDefault(p => p.Id == profile.Id);
            if (stored != null)
            {
                stored.Output = profile.Output?.Clone();
                stored.OutputComms = profile.OutputComms?.Clone();
                stored.Input = profile.Input?.Clone();
                ProfileManager.TrySaveProfiles(all, out _);
            }
        }

        if (failures.Count > 0)
        {
            message = $"Applied '{profile.Name}', but {string.Join("; ", failures)}.";
            return false;
        }

        var pending = Compare(profile);
        message = pending.Count == 0
            ? $"Applied '{profile.Name}'."
            : $"Applied '{profile.Name}', but {string.Join("; ", pending)}.";
        return true;
    }

    // -----------------------------------------------------------------------
    // Compare
    // -----------------------------------------------------------------------

    /// <summary>
    /// What still differs between this profile and the live configuration. Empty
    /// means the profile is currently in effect. Drives the tray's live dot and
    /// the --test-apply report.
    /// </summary>
    public static List<string> Compare(AudioProfile profile)
    {
        var enumerator = AudioInterop.Enumerator;
        var live = AudioEngine.GetDevices(enumerator, Flow.Render, includeInactive: false);
        live.AddRange(AudioEngine.GetDevices(enumerator, Flow.Capture, includeInactive: false));
        return Compare(profile, enumerator, live);
    }

    public static List<string> Compare(AudioProfile profile, IMMDeviceEnumerator enumerator,
                                       IReadOnlyList<AudioDevice> live)
    {
        var differences = new List<string>();

        foreach (var reference in profile.Refs)
        {
            var resolution = DeviceResolver.Resolve(reference, live);
            if (!resolution.Resolved)
            {
                differences.Add(resolution.Explanation);
                continue;
            }

            foreach (var role in reference.Roles)
            {
                var current = AudioEngine.GetDefault(enumerator, reference.Flow, role);
                if (current == null ||
                    !string.Equals(current.EndpointId, resolution.Device!.EndpointId, StringComparison.OrdinalIgnoreCase))
                {
                    differences.Add($"{Describe(role)} is {current?.Display ?? "unset"}, not {resolution.Device!.Display}");
                }
            }
        }

        return differences;
    }

    /// <summary>Whether the machine currently looks like this profile.</summary>
    public static bool MatchesCurrent(AudioProfile profile, IMMDeviceEnumerator enumerator,
                                      IReadOnlyList<AudioDevice> live) =>
        !profile.IsEmpty && Compare(profile, enumerator, live).Count == 0;

    /// <summary>The first saved profile that matches the live configuration, if any.</summary>
    public static AudioProfile? FindMatching(IEnumerable<AudioProfile> profiles)
    {
        var enumerator = AudioInterop.Enumerator;
        var live = AudioEngine.GetDevices(enumerator, Flow.Render, includeInactive: false);
        live.AddRange(AudioEngine.GetDevices(enumerator, Flow.Capture, includeInactive: false));

        return profiles.FirstOrDefault(p => MatchesCurrent(p, enumerator, live));
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// A human name for a role. Windows shows Console and Multimedia together as
    /// "Default Device" and keeps Communications separate, so the words here
    /// match what the user sees in Sound settings rather than the API's names.
    /// </summary>
    private static string Describe(Role role) => role switch
    {
        Role.Communications => "the device for calls",
        _ => "the default device"
    };

    private static bool SameId(AudioDevice? a, AudioDevice? b) =>
        a != null && b != null &&
        string.Equals(a.EndpointId, b.EndpointId, StringComparison.OrdinalIgnoreCase);

    /// <summary>The --list-profiles / --test-apply report for one profile.</summary>
    public static string DescribeProfile(AudioProfile profile, IReadOnlyList<AudioDevice> live)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{profile.Name}{(profile.Watched ? "  [watched]" : "")}" +
                      $"{(string.IsNullOrWhiteSpace(profile.Hotkey) ? "" : $"  {profile.Hotkey}")}" +
                      $"{(profile.NeedsRecapture ? "  (NEEDS RE-CAPTURE)" : "")}");

        if (profile.IsEmpty)
        {
            sb.AppendLine("    (no devices set)");
            return sb.ToString();
        }

        foreach (var reference in profile.Refs)
        {
            var resolution = DeviceResolver.Resolve(reference, live);
            string roles = string.Join(", ", reference.Roles.Select(r => r.ToString()));
            sb.AppendLine($"    {reference.Flow,-7} {resolution.Kind,-11} {resolution.Explanation}");
            sb.AppendLine($"            roles: {roles}");
            sb.AppendLine($"            {reference.EndpointId}");
            sb.AppendLine($"            fingerprint: desc=\"{reference.Desc}\"  iface=\"{reference.InterfaceName}\"");
        }

        return sb.ToString();
    }
}
