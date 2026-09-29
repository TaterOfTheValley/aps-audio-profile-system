using System.Text.Json.Serialization;

namespace APS;

/// <summary>
/// A saved reference to one audio endpoint.
///
/// Two identities, deliberately, because neither is sufficient alone:
///
/// <para><b>EndpointId</b> is exact and unambiguous, but not durable. The same
/// physical device plugged into a different USB port gets a different one — on
/// the development machine a single pair of speakers had accumulated four. A
/// profile keyed on this alone stops matching the moment a dongle moves socket,
/// which is precisely the failure APS exists to prevent.</para>
///
/// <para><b>Desc + InterfaceName</b> is the fingerprint that survives that move.
/// Both come from properties Windows keeps clean of the volatile numeric prefix
/// that pollutes the friendly name, so they can be compared directly.</para>
///
/// <see cref="DeviceResolver"/> tries them in that order and heals the stored id
/// when the fallback hits, so the slow path runs once rather than forever.
/// </summary>
public sealed class DeviceRef
{
    /// <summary>Exact identity, e.g. "{0.0.0.00000000}.{ae69a82a-...}". Rewritten
    /// in place when a fingerprint match finds the device at a new id.</summary>
    public string EndpointId { get; set; } = "";

    /// <summary>PKEY_Device_DeviceDesc, e.g. "Speakers". Fingerprint part 1.</summary>
    public string Desc { get; set; } = "";

    /// <summary>PKEY_DeviceInterface_FriendlyName, e.g. "Example USB DAC". Fingerprint part 2.</summary>
    public string InterfaceName { get; set; } = "";

    public Flow Flow { get; set; }

    /// <summary>
    /// Which default slots this device should occupy. Explicit rather than
    /// implied: Windows keeps three, shows two, and on a machine running a
    /// virtual mixer they routinely point at different hardware.
    /// </summary>
    public List<Role> Roles { get; set; } = new();

    /// <summary>The label to show. Not persisted — it is refreshed from the live
    /// device on every resolve, because a stored name goes stale silently.</summary>
    [JsonIgnore]
    public string DisplayHint { get; set; } = "";

    /// <summary>A reference with neither identity can never resolve; treated as
    /// absent rather than kept. Derived, never stored, following DLS's rule that
    /// a computed property cannot disagree with the data it describes.</summary>
    [JsonIgnore]
    public bool IsUnusable =>
        string.IsNullOrWhiteSpace(EndpointId) &&
        (string.IsNullOrWhiteSpace(Desc) || string.IsNullOrWhiteSpace(InterfaceName));

    public string Describe() =>
        !string.IsNullOrWhiteSpace(DisplayHint) ? DisplayHint
        : !string.IsNullOrWhiteSpace(Desc) ? $"{Desc} ({InterfaceName})"
        : EndpointId;

    public DeviceRef Clone() => new()
    {
        EndpointId = EndpointId,
        Desc = Desc,
        InterfaceName = InterfaceName,
        Flow = Flow,
        Roles = new List<Role>(Roles),
        DisplayHint = DisplayHint
    };

    /// <summary>Builds a reference from a live device, capturing both identities.
    /// Anything that creates a DeviceRef should come through here — a ref saved
    /// with only one of the two identities defeats the resolver.</summary>
    public static DeviceRef From(AudioDevice device, params Role[] roles) => new()
    {
        EndpointId = device.EndpointId,
        Desc = device.Desc,
        InterfaceName = device.InterfaceName,
        Flow = device.Flow,
        Roles = roles.ToList(),
        DisplayHint = device.Display
    };
}

/// <summary>Per-profile volume. Declared now so adding it later is a MINOR format bump
/// with no migration; nothing reads it yet.</summary>
public sealed class VolumeSpec
{
    public int? OutputPercent { get; set; }
    public bool? Muted { get; set; }
}

/// <summary>
/// A named set of audio devices.
///
/// Every slot is nullable and null means "leave whatever Windows has" — the
/// direct parallel to DLS's ScalePercent of 0. A profile that only sets the
/// microphone is legitimate and useful, and forcing every profile to specify
/// everything would make the common case worse to serve the rare one.
/// </summary>
public sealed class AudioProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>e.g. "Ctrl+Alt+1". Empty means no hotkey.</summary>
    public string Hotkey { get; set; } = "";

    /// <summary>
    /// Whether APS keeps an eye on this profile: it notices when Windows moves a
    /// default away from what the profile says and tells you — or, with
    /// <see cref="AutoReapply"/>, puts it back.
    ///
    /// <para>Per-profile even though watching is the intended default: a profile
    /// that deliberately hands control back to Windows should be expressible, and
    /// the field costs nothing now against a format MAJOR bump later.</para>
    ///
    /// <para><b>Stored as <c>Pinned</c>.</b> This was called pinning until the word
    /// was found to mislead — it reads as "locked", when the default behaviour is a
    /// notification and nothing is held anywhere. The on-disk name is the format
    /// 1.x contract, and renaming it would be a MAJOR bump plus a migration to
    /// change one word, so the file keeps the old name and everything a person
    /// reads uses the new one.</para>
    /// </summary>
    [JsonPropertyName("Pinned")]
    public bool Watched { get; set; } = true;

    /// <summary>
    /// Whether APS puts the devices back by itself instead of asking.
    ///
    /// <para><b>Off by default, deliberately.</b> Noticing that the devices moved
    /// and offering to switch back covers the actual complaint — you always know
    /// which profile you are on, and getting back is one click — and it does it
    /// without a background loop that changes hardware on its own. Silently
    /// re-asserting is strictly more surprising, so it is the thing you opt into
    /// once you know you want it, not the thing that happens before you asked.</para>
    ///
    /// <para>Additive field: reading a file that predates it simply leaves this
    /// false, which is why the format bump is MINOR.</para>
    /// </summary>
    public bool AutoReapply { get; set; }

    /// <summary>The main output — Windows' "Default Device", covering Console and
    /// Multimedia. For a headset that exposes separate stereo and hands-free
    /// endpoints, this is the stereo one.</summary>
    public DeviceRef? Output { get; set; }

    /// <summary>
    /// A different output for calls — Windows' separate "Default Communication
    /// Device". For a headset exposing a hands-free endpoint alongside a stereo
    /// one, that hands-free endpoint goes here.
    ///
    /// <para>The editor offers three choices for this slot and null cannot encode
    /// three states, so <b><see cref="DeviceRef.Roles"/> is the authority</b>,
    /// not the presence of this field:</para>
    /// <list type="bullet">
    /// <item><i>Same as default</i> — this is null and <see cref="Output"/> carries
    /// Communications in its own Roles.</item>
    /// <item><i>Leave unchanged</i> — this is null and no reference claims
    /// Communications at all.</item>
    /// <item><i>A specific device</i> — this is set, with Communications in its
    /// Roles.</item>
    /// </list>
    /// <para>Encoding "same as default" by copying the device into both slots
    /// would let the two drift apart silently the moment one is edited, so the
    /// role list carries it instead. Apply simply walks each reference and sets
    /// the roles it claims — there is no special case anywhere.</para>
    /// </summary>
    public DeviceRef? OutputComms { get; set; }

    public DeviceRef? Input { get; set; }

    /// <summary>Reserved. Declared, never read.</summary>
    public VolumeSpec? Volume { get; set; }

    /// <summary>Every reference this profile actually carries, in apply order.</summary>
    [JsonIgnore]
    public IEnumerable<DeviceRef> Refs
    {
        get
        {
            if (Output != null) yield return Output;
            if (OutputComms != null) yield return OutputComms;
            if (Input != null) yield return Input;
        }
    }

    /// <summary>A profile that would change nothing. Shown in the tray as needing
    /// attention rather than silently doing nothing when clicked.</summary>
    [JsonIgnore]
    public bool IsEmpty => Output == null && OutputComms == null && Input == null;

    /// <summary>True when a reference exists but could never resolve. Derived from
    /// the data, mirroring DLS's NeedsRecapture, so it cannot disagree with the
    /// profile it describes and is never written to the settings file.</summary>
    [JsonIgnore]
    public bool NeedsRecapture => Refs.Any(r => r.IsUnusable);

    public AudioProfile Clone(bool newId = true) => new()
    {
        Id = newId ? Guid.NewGuid().ToString("N") : Id,
        Name = Name,
        Hotkey = Hotkey,
        Watched = Watched,
        AutoReapply = AutoReapply,
        Output = Output?.Clone(),
        OutputComms = OutputComms?.Clone(),
        Input = Input?.Clone(),
        Volume = Volume == null ? null : new VolumeSpec { OutputPercent = Volume.OutputPercent, Muted = Volume.Muted }
    };
}
