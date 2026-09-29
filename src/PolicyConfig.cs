using System.Runtime.InteropServices;

namespace APS;

// ---------------------------------------------------------------------------
// Setting a default audio endpoint.
//
// There is no public API for this. Windows exposes it only through an
// undocumented COM interface, which is what every tool in this space uses.
//
// The two interfaces below differ in VTABLE ORDER, and that matters more than it
// looks: IPolicyConfigVista has no ResetDeviceFormat, so SetDefaultEndpoint sits
// at slot 12 there and at slot 13 in IPolicyConfig. Every slot is declared, in
// order, including the ones this app never calls, so the index is correct by
// construction rather than by counting. Get it wrong on the Vista interface and
// the call lands on SetPropertyValue, which takes the int role as a PROPERTYKEY&
// and dereferences it — a crash at best, silent corruption at worst.
//
// Unused slots take IntPtr for every parameter deliberately: the declaration
// only needs to reserve the slot, and an opaque pointer cannot be marshalled
// wrongly if something later calls it by accident.
//
// Measured on current Windows 11 builds:
//     CPolicyConfigClient      + IPolicyConfig       -> E_NOINTERFACE
//     CPolicyConfigClient      + IPolicyConfigVista  -> E_NOINTERFACE
//     CPolicyConfigVistaClient + IPolicyConfigVista  -> works
//
// The first of those is the pairing nearly every published sample uses. Both are
// tried at runtime rather than hardcoding the winner, because Windows has
// already moved this once and the failure needs to be diagnosable rather than
// mysterious.
// ---------------------------------------------------------------------------

[Guid("568b9108-44bf-40b4-9006-86afe5b5a620"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);            // 3
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, out IntPtr fmt);   // 4
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);        // 5
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr a, IntPtr b); // 6
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);         // 7
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);                  // 8
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);                  // 9
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr pv);    // 10
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr pv);    // 11
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);               // 12
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);         // 13
}

[Guid("f8679f50-850a-4b0c-9c46-0daa1f8bcb4b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);            // 3
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int def, out IntPtr fmt);   // 4
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);                          // 5  <- the extra slot
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);        // 6
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int def, IntPtr a, IntPtr b); // 7
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);         // 8
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);                  // 9
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);                  // 10
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr pv);    // 11
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr pv);    // 12
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);               // 13
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);         // 14
}

/// <summary>
/// The one thing APS exists to do: move a Windows default audio device.
/// </summary>
internal static class PolicyConfig
{
    private const string ClsidVistaClient = "294935ce-f637-4e7c-a41b-ab255460b862";
    private const string ClsidPolicyClient = "870af99c-171d-4f9e-af0d-e63df40c2bc9";

    private sealed class Route
    {
        public required string Description;
        public required Func<string, int, int> SetDefaultEndpoint;
    }

    private static Route? _route;
    private static bool _discovered;
    private static string _failureDetail = "";

    /// <summary>Whether a working route was found. Preflight treats a false here as
    /// fatal — without it the app cannot do the only thing it is for.</summary>
    public static bool IsAvailable
    {
        get
        {
            Discover();
            return _route != null;
        }
    }

    /// <summary>
    /// Which COM pairing is in use, or why none is. Printed by --preflight so that
    /// if a future Windows update breaks this, one command says so instead of the
    /// app failing in a way nobody can explain.
    /// </summary>
    public static string RouteDescription
    {
        get
        {
            Discover();
            return _route?.Description ?? _failureDetail;
        }
    }

    private static void Discover()
    {
        if (_discovered) return;
        _discovered = true;

        var attempts = new List<string>();

        // Vista first: measured as the working pairing on current Windows 11.
        var vista = TryActivate<IPolicyConfigVista>(ClsidVistaClient, "CPolicyConfigVistaClient/IPolicyConfigVista", attempts);
        if (vista != null)
        {
            _route = new Route
            {
                Description = "CPolicyConfigVistaClient / IPolicyConfigVista",
                SetDefaultEndpoint = vista.SetDefaultEndpoint
            };
            return;
        }

        var legacy = TryActivate<IPolicyConfig>(ClsidPolicyClient, "CPolicyConfigClient/IPolicyConfig", attempts);
        if (legacy != null)
        {
            _route = new Route
            {
                Description = "CPolicyConfigClient / IPolicyConfig",
                SetDefaultEndpoint = legacy.SetDefaultEndpoint
            };
            return;
        }

        _failureDetail =
            "No supported way to set the default audio device on this system. Tried: " +
            string.Join("; ", attempts) + ".";
    }

    private static T? TryActivate<T>(string clsid, string label, List<string> attempts) where T : class
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(clsid));
            if (type == null)
            {
                attempts.Add($"{label} (CLSID not registered)");
                return null;
            }

            object raw = Activator.CreateInstance(type)!;
            if (raw is T typed) return typed;

            attempts.Add($"{label} (interface refused)");
            return null;
        }
        catch (InvalidCastException)
        {
            attempts.Add($"{label} (E_NOINTERFACE)");
            return null;
        }
        catch (Exception ex)
        {
            attempts.Add($"{label} (0x{ex.HResult:X8})");
            return null;
        }
    }

    /// <summary>
    /// Sets one role. Windows links Console and Multimedia — setting either moves
    /// both — but they are still set explicitly, because relying on that coupling
    /// would be relying on undocumented behaviour inside an undocumented API.
    /// </summary>
    public static bool SetDefault(string endpointId, Role role, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(endpointId))
        {
            error = "No device was given to set as the default.";
            return false;
        }

        Discover();
        if (_route == null)
        {
            error = _failureDetail;
            return false;
        }

        int hr;
        try
        {
            hr = _route.SetDefaultEndpoint(endpointId, (int)role);
        }
        catch (Exception ex)
        {
            error = $"Windows rejected the device change ({ex.GetType().Name}, 0x{ex.HResult:X8}).";
            return false;
        }

        if (hr == 0) return true;

        // Keep the raw HRESULT alongside the readable text. Without it there is no
        // way to tell which failure this was, and these are undocumented calls.
        error = $"{Explain(hr)} [SetDefaultEndpoint({role}) -> 0x{hr:X8}]";
        return false;
    }

    /// <summary>
    /// Sets every role a device claims, stopping at the first failure and naming
    /// the role that failed. SetDefaultEndpoint does not cascade across roles.
    /// </summary>
    public static bool SetDefaultForRoles(string endpointId, IEnumerable<Role> roles, out string error)
    {
        foreach (var role in roles)
        {
            if (!SetDefault(endpointId, role, out error)) return false;
        }

        error = "";
        return true;
    }

    private static string Explain(int hr) => (uint)hr switch
    {
        0x80070490 => "Windows could not find that audio device — it may have been unplugged.",
        0x8889000A => "That audio device is currently in use and cannot be changed.",
        0x80004002 => "This build of Windows does not support changing the default device this way.",
        0x80070005 => "Access denied while changing the default audio device.",
        _ => "Windows would not change the default audio device."
    };
}
