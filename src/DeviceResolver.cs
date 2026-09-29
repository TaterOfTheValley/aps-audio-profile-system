namespace APS;

/// <summary>How a saved reference was matched against live hardware.</summary>
public enum MatchKind
{
    /// <summary>The stored endpoint id is live and active. The normal case.</summary>
    Exact,

    /// <summary>The id was stale but the fingerprint found the device somewhere
    /// else — typically the same hardware in a different USB port.</summary>
    Fingerprint,

    /// <summary>Not connected, or connected but not active.</summary>
    Absent,

    /// <summary>The fingerprint matched more than one active device, so which one
    /// was meant is genuinely unknown.</summary>
    Ambiguous
}

public readonly record struct Resolution(MatchKind Kind, AudioDevice? Device, string Explanation)
{
    public bool Resolved => Device != null && (Kind == MatchKind.Exact || Kind == MatchKind.Fingerprint);
}

/// <summary>
/// Turns a saved <see cref="DeviceRef"/> back into a live endpoint.
///
/// This exists because audio endpoint ids are not stable the way DLS's monitor
/// device paths are. Windows mints a new id per physical port, so the same
/// speakers moved one socket along become a device the profile has never seen.
/// On the development machine three separate devices had accumulated three or
/// four ids each — all but one of them ghosts in the NotPresent state.
///
/// So identity is two-tier: the exact id first because it is unambiguous, then
/// the (desc, interface) fingerprint because it survives the move. When the
/// fingerprint is what hit, the caller heals the stored id, so the fallback path
/// runs once per port change rather than on every single apply.
/// </summary>
internal static class DeviceResolver
{
    /// <summary>
    /// Resolves one reference against a live device list.
    ///
    /// The list is passed in rather than fetched here so a caller resolving a
    /// whole profile — or a whole tray menu — enumerates once instead of once
    /// per reference. An enumeration can be dozens of endpoints.
    /// </summary>
    public static Resolution Resolve(DeviceRef reference, IReadOnlyList<AudioDevice> live)
    {
        if (reference.IsUnusable)
        {
            return new Resolution(MatchKind.Absent, null,
                "This profile was saved without enough information to identify the device. Re-capture it.");
        }

        // --- Tier 1: the exact endpoint id.
        //
        // Compared whole and never parsed. Only an Active device counts: a
        // NotPresent ghost with a matching id is the device unplugged, not the
        // device found.
        if (!string.IsNullOrWhiteSpace(reference.EndpointId))
        {
            var exact = live.FirstOrDefault(d =>
                d.IsActive &&
                string.Equals(d.EndpointId, reference.EndpointId, StringComparison.OrdinalIgnoreCase));

            if (exact != null)
            {
                return new Resolution(MatchKind.Exact, exact, exact.Display);
            }
        }

        // --- Tier 2: the fingerprint.
        //
        // Desc and InterfaceName only. Never FriendlyName: it carries a numeric
        // disambiguator Windows renumbers as devices come and go, so matching on
        // it would fail exactly when a device set has changed — which is the one
        // situation this fallback exists for.
        if (string.IsNullOrWhiteSpace(reference.Desc) || string.IsNullOrWhiteSpace(reference.InterfaceName))
        {
            return new Resolution(MatchKind.Absent, null, $"{reference.Describe()} is not connected");
        }

        var candidates = live.Where(d =>
            d.IsActive &&
            d.Flow == reference.Flow &&
            string.Equals(d.Desc, reference.Desc, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.InterfaceName, reference.InterfaceName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 1)
        {
            var found = candidates[0];
            return new Resolution(MatchKind.Fingerprint, found,
                $"{found.Display} (reconnected on a different port)");
        }

        if (candidates.Count > 1)
        {
            // Two identical devices are genuinely indistinguishable at this point,
            // and picking one at random would route audio somewhere the user did
            // not choose while looking like it worked. Saying so is better.
            return new Resolution(MatchKind.Ambiguous, null,
                $"{candidates.Count} devices match “{reference.Describe()}” — pick one in the editor");
        }

        return new Resolution(MatchKind.Absent, null, $"{reference.Describe()} is not connected");
    }

    /// <summary>
    /// Rewrites the stored endpoint id after a fingerprint match, and refreshes
    /// the display hint on every successful match.
    ///
    /// Returns true only when the persisted data actually changed, so the caller
    /// can save once per apply instead of once per reference — and can skip the
    /// write entirely in the common case where nothing moved.
    /// </summary>
    public static bool HealInPlace(DeviceRef reference, Resolution resolution)
    {
        if (resolution.Device == null) return false;

        // The hint is not persisted, so refreshing it never counts as a change.
        reference.DisplayHint = resolution.Device.Display;

        if (resolution.Kind != MatchKind.Fingerprint) return false;

        if (string.Equals(reference.EndpointId, resolution.Device.EndpointId, StringComparison.OrdinalIgnoreCase))
            return false;

        reference.EndpointId = resolution.Device.EndpointId;
        return true;
    }

    /// <summary>
    /// Resolves every reference in a profile against one enumeration.
    ///
    /// Both flows are gathered because a profile spans outputs and inputs, and
    /// only active devices are considered — an inactive endpoint cannot be made
    /// a default, so offering it would only produce a failure later.
    /// </summary>
    public static Dictionary<DeviceRef, Resolution> ResolveProfile(AudioProfile profile)
    {
        var live = AudioEngine.GetAllDevices(includeInactive: false);
        return ResolveProfile(profile, live);
    }

    public static Dictionary<DeviceRef, Resolution> ResolveProfile(
        AudioProfile profile, IReadOnlyList<AudioDevice> live)
    {
        var result = new Dictionary<DeviceRef, Resolution>();
        foreach (var reference in profile.Refs)
        {
            result[reference] = Resolve(reference, live);
        }
        return result;
    }
}
