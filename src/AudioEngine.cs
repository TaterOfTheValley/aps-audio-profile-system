using System.Text;

namespace APS;

/// <summary>
/// One audio endpoint, as APS cares about it.
///
/// Two identities live here and they are not interchangeable. <see cref="EndpointId"/>
/// is exact but not durable — the same physical device in a different USB port
/// gets a different one. The (<see cref="Desc"/>, <see cref="InterfaceName"/>)
/// pair is the fingerprint that survives that, and it is what DeviceResolver
/// falls back to. <see cref="FriendlyName"/> is for showing a human and nothing
/// else: it carries a numeric prefix Windows renumbers, and it can be empty.
/// </summary>
public sealed class AudioDevice
{
    public string EndpointId { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string Desc { get; init; } = "";
    public string InterfaceName { get; init; } = "";
    public Flow Flow { get; init; }
    public DeviceState State { get; init; }

    /// <summary>
    /// A display heuristic only — never identity. Used by the editor's
    /// "show virtual devices" filter and by diagnostics that prefer real
    /// hardware. Plenty of setups are mostly virtual outputs, so the
    /// distinction is worth drawing, but a virtual device is a real endpoint and
    /// a perfectly valid thing for a profile to select.
    /// </summary>
    public bool IsVirtual =>
        Contains(InterfaceName, "Voicemeeter") ||
        Contains(InterfaceName, "VB-Audio") ||
        Contains(InterfaceName, "Virtual") ||
        Contains(InterfaceName, "CABLE");

    public bool IsActive => State == DeviceState.Active;

    /// <summary>What to show a user. Falls back to Desc: a disconnected endpoint
    /// can have an empty FriendlyName while Desc and InterfaceName survive, and a
    /// blank row in the tray helps nobody.</summary>
    public string Display => !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName : Desc;

    /// <summary>
    /// A compact label, for places listing several devices on one line.
    ///
    /// <para>Windows' friendly name is "Desc (InterfaceName)" — "Speakers (3- USB
    /// Advanced Audio Device)" — which is far too long to put three of on a tray
    /// row. Whichever half is distinctive varies: for real hardware the Desc is a
    /// generic role word and the interface name carries the identity ("Example
    /// USB DAC"), while for virtual devices it is the other way round (a friendly
    /// name such as "Virtual Mixer Input" against a generic driver name).</para>
    ///
    /// <para>So: use the interface name when the Desc is one of Windows' generic
    /// endpoint words, and the Desc otherwise. This is a display heuristic and is
    /// English-only — on a localised Windows the list simply will not match and
    /// every device falls back to its Desc, which is still reasonable. It is never
    /// used for matching; identity is Desc and InterfaceName together.</para>
    /// </summary>
    public string ShortName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Desc)) return Display;
            if (string.IsNullOrWhiteSpace(InterfaceName)) return Desc;
            return IsGenericDesc(Desc) ? InterfaceName : Desc;
        }
    }

    private static readonly string[] GenericDescriptions =
    {
        "speakers", "headphones", "headset", "headset earphone", "headset microphone",
        "microphone", "line in", "digital output", "digital audio (hdmi)",
        "internal aux jack", "stereo mix", "speaker/hp"
    };

    private static bool IsGenericDesc(string desc) =>
        GenericDescriptions.Contains(desc.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Display} [{State}] {EndpointId}";
}

/// <summary>
/// Reading the audio configuration. Everything here is non-mutating; changing a
/// default lives in <see cref="PolicyConfig"/>.
/// </summary>
internal static class AudioEngine
{
    /// <summary>
    /// Reads one flow using the caller's enumerator, shared across device queries.
    /// </summary>
    public static List<AudioDevice> GetDevices(IMMDeviceEnumerator enumerator, Flow flow, bool includeInactive)
    {
        var mask = includeInactive ? DeviceState.All : DeviceState.Active;
        var result = new List<AudioDevice>();

        foreach (var device in AudioInterop.Enumerate(enumerator, flow, mask))
        {
            var built = Describe(device, flow);
            if (built != null) result.Add(built);

            // Describe copies everything APS wants into a managed AudioDevice, so
            // the wrapper has no further use. Releasing here is what keeps the
            // handle count of a long-running tray flat.
            AudioInterop.Release(device);
        }

        return result;
    }

    /// <summary>Every device on both flows, for diagnostics and the editor.</summary>
    public static List<AudioDevice> GetAllDevices(bool includeInactive)
    {
        var enumerator = AudioInterop.Enumerator;
        var result = GetDevices(enumerator, Flow.Render, includeInactive);
        result.AddRange(GetDevices(enumerator, Flow.Capture, includeInactive));
        return result;
    }

    public static AudioDevice? GetDefault(Flow flow, Role role)
    {
        try
        {
            var enumerator = AudioInterop.Enumerator;
            return GetDefault(enumerator, flow, role);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The current default for one role, or null when there is none. Null rather
    /// than an exception: a machine with no capture device at all is unusual but
    /// not an error, and every caller has something sensible to do with "none".
    /// </summary>
    public static AudioDevice? GetDefault(IMMDeviceEnumerator enumerator, Flow flow, Role role)
    {
        try
        {
            if (enumerator.GetDefaultAudioEndpoint((int)flow, (int)role, out var device) != 0 || device == null)
                return null;

            try { return Describe(device, flow); }
            finally { AudioInterop.Release(device); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The endpoint id currently holding one role, or "" when nothing does.
    ///
    /// <para>Deliberately not <see cref="GetDefault"/>: that builds a whole
    /// AudioDevice, which means opening a property store and reading three
    /// strings. ProfileGuard asks this question for up to six roles every couple
    /// of seconds forever, and it only ever compares the answer to an id, so all
    /// of that work would be thrown away. This is two COM calls and no
    /// allocations worth speaking of.</para>
    /// </summary>
    public static string GetDefaultId(IMMDeviceEnumerator enumerator, Flow flow, Role role)
    {
        try
        {
            if (enumerator.GetDefaultAudioEndpoint((int)flow, (int)role, out var device) != 0 || device == null)
                return "";

            try { return AudioInterop.IdOf(device); }
            finally { AudioInterop.Release(device); }
        }
        catch
        {
            return "";
        }
    }

    private static AudioDevice? Describe(IMMDevice device, Flow flow)
    {
        try
        {
            string id = AudioInterop.IdOf(device);
            if (string.IsNullOrEmpty(id)) return null;

            var strings = AudioInterop.ReadStrings(device);

            return new AudioDevice
            {
                EndpointId = id,
                FriendlyName = strings.FriendlyName,
                Desc = strings.Desc,
                InterfaceName = strings.InterfaceName,
                Flow = flow,
                State = AudioInterop.StateOf(device)
            };
        }
        catch
        {
            // One unreadable endpoint is skipped; it must not end the enumeration.
            return null;
        }
    }

    /// <summary>
    /// The --dump-devices report. Returns a string rather than printing so the CLI
    /// and any future diagnostic share one implementation, matching DLS's
    /// DisplayEngine.DumpConfiguration.
    /// </summary>
    public static string DumpDevices()
    {
        var sb = new StringBuilder();
        var enumerator = AudioInterop.Enumerator;

        foreach (var flow in new[] { Flow.Render, Flow.Capture })
        {
            var devices = GetDevices(enumerator, flow, includeInactive: true);
            int active = devices.Count(d => d.IsActive);

            sb.AppendLine($"=== {(flow == Flow.Render ? "OUTPUTS (render)" : "INPUTS (capture)")} ===");

            foreach (var d in devices)
            {
                sb.AppendLine($"[{d.State,-10}] {d.Display}{(d.IsVirtual ? "   (virtual)" : "")}");
                sb.AppendLine($"             fingerprint: desc=\"{d.Desc}\"  iface=\"{d.InterfaceName}\"");
                sb.AppendLine($"             {d.EndpointId}");
            }

            sb.AppendLine($"  {active} active / {devices.Count} total");
            sb.AppendLine();
        }

        sb.AppendLine("=== CURRENT DEFAULTS ===");
        sb.Append(DescribeDefaults(enumerator));
        return sb.ToString();
    }

    /// <summary>
    /// The six role defaults, as the tray's "Now" block shows them: one line per
    /// flow when all three roles agree, expanded only when they differ.
    /// </summary>
    public static string DescribeDefaults(IMMDeviceEnumerator? enumerator = null)
    {
        enumerator ??= AudioInterop.Enumerator;
        var sb = new StringBuilder();

        foreach (var flow in new[] { Flow.Render, Flow.Capture })
        {
            string label = flow == Flow.Render ? "output" : "input";

            var console = GetDefault(enumerator, flow, Role.Console);
            var multimedia = GetDefault(enumerator, flow, Role.Multimedia);
            var comms = GetDefault(enumerator, flow, Role.Communications);

            if (SameDevice(console, multimedia) && SameDevice(console, comms))
            {
                sb.AppendLine($"  {label,-7} {Name(console)}");
                continue;
            }

            // Console and Multimedia are what Windows calls "Default Device";
            // Communications is the separate "Default Communication Device".
            if (SameDevice(console, multimedia))
            {
                sb.AppendLine($"  {label,-7} {Name(console)}");
            }
            else
            {
                sb.AppendLine($"  {label,-7} {Name(console)}   (console)");
                sb.AppendLine($"  {"",-7} {Name(multimedia)}   (multimedia)");
            }

            sb.AppendLine($"  {"",-7} {Name(comms)}   (for calls)");
        }

        return sb.ToString();
    }

    private static bool SameDevice(AudioDevice? a, AudioDevice? b) =>
        string.Equals(a?.EndpointId ?? "", b?.EndpointId ?? "", StringComparison.OrdinalIgnoreCase);

    private static string Name(AudioDevice? d) => d?.Display ?? "(none)";
}
