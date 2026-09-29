using System.Runtime.InteropServices;

namespace APS;

/// <summary>
/// COM interop for the Windows audio endpoint APIs.
///
/// Every GUID here was measured against a real system rather than
/// taken from documentation or samples. The setting side (PolicyConfig.cs) is
/// undocumented and the commonly published route does not work on current
/// Windows 11.
/// </summary>
internal static class Iid
{
    public const string MMDeviceEnumerator = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    public const string IMMDeviceEnumerator = "A95664D2-9614-4F35-A746-DE8DB63617E6";
    public const string IMMDeviceCollection = "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E";
    public const string IMMDevice = "D666063F-1587-4E43-81F1-B948E807363F";
    public const string IMMNotificationClient = "7991EEC9-7E89-4D85-8390-6C703CEC60C0";
    public const string IPropertyStore = "886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99";
}

/// <summary>Which direction audio flows. Windows calls these render and capture.</summary>
public enum Flow
{
    Render = 0,
    Capture = 1
}

/// <summary>
/// The three default-device slots Windows keeps.
///
/// Settings shows only two of them — "Default Device" (Console and Multimedia
/// together) and "Default Communication Device" (Communications). They really
/// are separate, and on many systems they point at different hardware,
/// which is exactly why a profile records roles explicitly.
/// </summary>
public enum Role
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

[Flags]
public enum DeviceState
{
    Active = 0x1,
    Disabled = 0x2,
    NotPresent = 0x4,
    Unplugged = 0x8,
    All = 0xF
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
    public Guid fmtid;
    public int pid;

    public PropertyKey(string formatId, int propertyId)
    {
        fmtid = new Guid(formatId);
        pid = propertyId;
    }
}

/// <summary>
/// PROPVARIANT.
///
/// <para><b>Size = 24 is load-bearing.</b> A PROPVARIANT is 24 bytes on x64: an
/// 8-byte header (vt plus three reserved shorts) then a 16-byte union.
/// Declaring only the two fields this code reads — vt at 0, pointer at 8 —
/// yields a 16-byte struct, and GetValue then writes 24 bytes into it,
/// corrupting 8 bytes of whatever follows.</para>
///
/// <para>This was hit for real during development, and the symptom looks nothing like
/// memory corruption: every call returns S_OK, the first property read on a
/// device is correct, and every read after it comes back empty — the overrun
/// lands on dead stack when the read is inline and on a live local when it goes
/// through a helper. If Desc or InterfaceName ever reads empty while
/// FriendlyName reads fine, this is the cause; the property keys are correct.</para>
///
/// <para>The reserved fields are spelled out rather than left implicit so the
/// declaration documents the real structure. APS publishes win-x64 only; a
/// PROPVARIANT is 16 bytes on x86 and this would need revisiting there.</para>
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public short vt;
    [FieldOffset(2)] public short wReserved1;
    [FieldOffset(4)] public short wReserved2;
    [FieldOffset(6)] public short wReserved3;
    [FieldOffset(8)] public IntPtr pointerValue;

    public const short VT_LPWSTR = 31;

    public string? AsString() =>
        vt == VT_LPWSTR && pointerValue != IntPtr.Zero
            ? Marshal.PtrToStringUni(pointerValue)
            : null;
}

internal static class Pkey
{
    /// <summary>
    /// "Speakers (3- Example USB DAC)". Display only.
    ///
    /// The "3-" is a disambiguator Windows renumbers as devices come and go, so
    /// this string is not stable enough to match on. It can also be empty on a
    /// disconnected device whose Desc and InterfaceName are both intact, so
    /// anything showing a device name must fall back to Desc.
    /// </summary>
    public static readonly PropertyKey FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);

    /// <summary>"Speakers". Clean of the prefix; fingerprint part 1.</summary>
    public static readonly PropertyKey DeviceDesc = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);

    /// <summary>"Example USB DAC". Clean of the prefix; fingerprint part 2.</summary>
    public static readonly PropertyKey InterfaceFriendlyName = new("b3f8fa53-0004-438e-9003-51a46e139bfc", 6);
}

[Guid(Iid.IPropertyStore), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    int GetCount(out int count);
    int GetAt(int index, out PropertyKey key);
    int GetValue(ref PropertyKey key, out PropVariant value);
    int SetValue(ref PropertyKey key, ref PropVariant value);
    int Commit();
}

[Guid(Iid.IMMDevice), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                 [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    int GetState(out int state);
}

[Guid(Iid.IMMDeviceCollection), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    int GetCount(out int count);
    int Item(int index, out IMMDevice device);
}

[Guid(Iid.IMMDeviceEnumerator), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

/// <summary>
/// Device-change notifications.
///
/// <para><b>APS does not implement this, and should not.</b> It is declared only
/// because IMMDeviceEnumerator's last two vtable slots take it as a parameter —
/// deleting it would mean changing that interface, and the slot order there is
/// load-bearing.</para>
///
/// <para>An earlier build did implement it, and registering a managed implementation
/// destabilises the process: under a burst of default changes it dies with an
/// access violation on the notification thread, with no managed frames, in
/// coreclr or in combase depending on timing — and once with a heap-corruption
/// code instead. About one run in four under repeated triggering. It happens
/// in the real tray, not only in a diagnostic, and
/// it happens with a callback object whose five methods do nothing but return
/// S_OK, which rules out every response APS could make. Moving registration to a
/// dedicated MTA thread, declaring the PROPERTYKEY parameter as the pointer the
/// x64 ABI actually passes, and removing Marshal.ReleaseComObject all failed to
/// stop it.</para>
///
/// <para><see cref="ProfileGuard"/> polls instead. If you are about to reach for
/// this interface again: read that investigation first, and reproduce the crash
/// before assuming it has gone away.</para>
///
/// <para>For reference, as measured: one logical default change delivers
/// four OnDefaultDeviceChanged callbacks within about 15 ms, plus roughly one
/// OnPropertyValueChanged per active endpoint. Console and Multimedia are linked,
/// so setting either fires both.</para>
/// </summary>
[Guid(Iid.IMMNotificationClient), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);

    /// <summary>
    /// <para><b>The key is IntPtr, not PropertyKey, and that is load-bearing.</b>
    /// The native signature passes a PROPERTYKEY <i>by value</i>. On x64 a
    /// 20-byte struct cannot go in a register, so the ABI passes a pointer to a
    /// caller-allocated copy — which is exactly what an IntPtr parameter
    /// receives. Declaring it as the struct instead makes the runtime marshal a
    /// by-value struct across the callback boundary, and under real notification
    /// volume that faults inside the stub before any managed code runs: an
    /// ExecutionEngineException on the notification thread with no managed frames
    /// to look at, landing in coreclr or combase depending on timing.</para>
    ///
    /// <para>It took a hammer test to surface — a few callbacks are fine, a few
    /// hundred are not — which is the same shape as the PROPVARIANT bug above and
    /// the reason both are written down rather than left to be rediscovered.
    /// Nothing in APS reads this key, so nothing is lost by leaving it opaque, and
    /// an opaque pointer cannot be marshalled wrongly.</para>
    /// </summary>
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key);
}

internal static class AudioInterop
{
    private const int StgmRead = 0;

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <summary>The three string properties APS reads, from one store open.</summary>
    internal sealed class DeviceStrings
    {
        public string FriendlyName = "";
        public string Desc = "";
        public string InterfaceName = "";

        /// <summary>What to show a user. Falls back to Desc because FriendlyName
        /// can be empty on a disconnected device, which would otherwise render as
        /// a blank row in the tray.</summary>
        public string Display => !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName : Desc;
    }

    /// <summary>
    /// Verifies the marshalled struct sizes before anything relies on them.
    ///
    /// This is the audio counterpart to DLS's CcdNative.AssertLayout: a size
    /// mismatch here corrupts memory rather than returning an error, so it is
    /// worth failing loudly at preflight instead of misbehaving quietly for the
    /// life of the process.
    /// </summary>
    public static void AssertLayout()
    {
        int propVariant = Marshal.SizeOf<PropVariant>();
        if (propVariant != 24)
        {
            throw new InvalidOperationException(
                $"PROPVARIANT marshals to {propVariant} bytes; 24 is required on x64. " +
                "Reading a device property would write past the end of the struct and corrupt memory.");
        }

        int propertyKey = Marshal.SizeOf<PropertyKey>();
        if (propertyKey != 20)
        {
            throw new InvalidOperationException(
                $"PROPERTYKEY marshals to {propertyKey} bytes; 20 is required (GUID plus DWORD).");
        }
    }

    [ThreadStatic] private static IMMDeviceEnumerator? _threadEnumerator;

    /// <summary>
    /// The enumerator to use for ordinary work: one per thread, kept for the life
    /// of that thread.
    ///
    /// <para>Every call site used to create its own, which meant a tray refresh or
    /// a correction round activated several COM objects and abandoned them to the
    /// finalizer. That is a lot of churn for an object that is stateless as far as
    /// APS uses it, and it is churn on exactly the objects the audio service is
    /// concurrently delivering notifications through.</para>
    ///
    /// <para>Per <i>thread</i> rather than per process, because a COM object
    /// belongs to the apartment it was created in. APS does all its audio work on
    /// the one STA thread, so in practice this is one object; DeviceWatcher's MTA
    /// thread deliberately keeps its own and does not touch this.</para>
    /// </summary>
    public static IMMDeviceEnumerator Enumerator => _threadEnumerator ??= CreateEnumerator();

    /// <summary>A brand new enumerator. Prefer <see cref="Enumerator"/>; this
    /// exists for it, and for anything that genuinely needs an instance of its
    /// own rather than the thread's.</summary>
    public static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(new Guid(Iid.MMDeviceEnumerator))
                   ?? throw new InvalidOperationException("The audio device enumerator is not registered on this system.");
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Reads every string property in ONE property-store open, then releases the
    /// store. Not one open per property: this runs for every endpoint on every
    /// tray open, and there is nothing to gain from three times the COM traffic.
    ///
    /// Degrades to empty strings on any failure. One malformed endpoint must never
    /// take down an enumeration — during development a capture-side device threw here
    /// and killed the entire listing.
    /// </summary>
    public static DeviceStrings ReadStrings(IMMDevice device)
    {
        var result = new DeviceStrings();
        IPropertyStore? store = null;

        try
        {
            if (device.OpenPropertyStore(StgmRead, out store) != 0 || store == null) return result;

            result.FriendlyName = Read(store, Pkey.FriendlyName);
            result.Desc = Read(store, Pkey.DeviceDesc);
            result.InterfaceName = Read(store, Pkey.InterfaceFriendlyName);
        }
        catch
        {
            // Whatever was read before the failure is still worth returning.
        }
        finally
        {
            // Released here rather than left to the finalizer. This is called once
            // per endpoint per enumeration and an enumeration can be dozens of endpoints, so
            // waiting for a GC that an idle tray may not run for a long time left
            // several hundred handles outstanding — measured, not assumed.
            //
            // Briefly removed during a crash investigation on the theory
            // that severing a shared wrapper was the cause. It was not: the numbers
            // that suggested it were inside the noise of a one-in-four failure, and
            // the actual cause was the notification client, which is gone.
            if (store != null && Marshal.IsComObject(store)) Marshal.ReleaseComObject(store);
        }

        return result;
    }

    private static string Read(IPropertyStore store, PropertyKey key)
    {
        var variant = default(PropVariant);
        try
        {
            var k = key;
            if (store.GetValue(ref k, out variant) != 0) return "";
            return variant.AsString() ?? "";
        }
        catch
        {
            return "";
        }
        finally
        {
            // GetValue allocates the string; without this the app leaks one
            // allocation per property per endpoint on every single tray open.
            try { PropVariantClear(ref variant); } catch { /* nothing useful to do */ }
        }
    }

    /// <summary>
    /// Releases a COM object APS created and has finished with.
    ///
    /// <para>Every endpoint read makes wrappers — one per device, plus a property
    /// store — and an enumeration can be dozens of endpoints. Left to the finalizer they sit
    /// there: an idle tray does not allocate, so it does not collect, and the
    /// handle count measured after fifty profile switches stayed several hundred
    /// above where it started for as long as it was watched. Not a leak, but not
    /// something to leave to chance either.</para>
    ///
    /// <para>Only ever called on an object this file has just created and no longer
    /// refers to. ReleaseComObject severs a wrapper rather than decrementing a
    /// reference, so calling it on something another part of APS still holds would
    /// be a use-after-free — which is why it is a small private helper with that
    /// rule attached rather than something to reach for freely.</para>
    /// </summary>
    public static void Release(object? comObject)
    {
        if (comObject == null || !Marshal.IsComObject(comObject)) return;

        try { Marshal.ReleaseComObject(comObject); }
        catch { /* Already gone; nothing to do and nothing to report. */ }
    }

    public static string IdOf(IMMDevice device)
    {
        try
        {
            return device.GetId(out string id) == 0 ? id : "";
        }
        catch
        {
            return "";
        }
    }

    public static DeviceState StateOf(IMMDevice device)
    {
        try
        {
            return device.GetState(out int state) == 0 ? (DeviceState)state : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Enumerates endpoints, skipping any single one that cannot be read.</summary>
    public static List<IMMDevice> Enumerate(IMMDeviceEnumerator enumerator, Flow flow, DeviceState stateMask)
    {
        var result = new List<IMMDevice>();

        if (enumerator.EnumAudioEndpoints((int)flow, (int)stateMask, out var collection) != 0 || collection == null)
            return result;
        if (collection.GetCount(out int count) != 0) return result;

        for (int i = 0; i < count; i++)
        {
            try
            {
                if (collection.Item(i, out var device) == 0 && device != null) result.Add(device);
            }
            catch
            {
                // Skip the one bad endpoint and keep the rest of the enumeration.
            }
        }

        return result;
    }
}
