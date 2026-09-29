namespace APS;

/// <summary>The same speaker icon used by Windows, the installer and the app UI.</summary>
internal static class AppIcon
{
    private static Icon? _shared;
    private static Icon? _sharedBadged;

    // NotifyIcon and the forms borrow these handles, so keep them alive for the UI's
    // lifetime. The lock badge still indicates that a profile is being watched.
    public static Icon Shared => _shared ??= Create(32);
    public static Icon SharedBadged => _sharedBadged ??= Create(32, badged: true);

    public static Icon Create(int size) => Create(size, badged: false);

    /// <summary>Loads the nearest embedded frame. The caller owns the returned icon.</summary>
    public static Icon Create(int size, bool badged)
    {
        string name = badged ? "APS.AppIcon.Badged.ico" : "APS.AppIcon.ico";
        using var resource = typeof(AppIcon).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The APS icon is missing from the assembly.");
        return new Icon(resource, size, size);
    }
}
