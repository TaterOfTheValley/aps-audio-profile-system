using System.Reflection;

namespace APS;

/// <summary>
/// The app's identity, in one place. Every user-visible name comes from here so
/// renaming is a single edit rather than a hunt through string literals.
/// </summary>
internal static class AppInfo
{
    /// <summary>Short name — title bars, tray, balloon tips.</summary>
    public const string Name = "APS";

    /// <summary>Expanded name, for the README, the about line and error dialogs where
    /// three initials on their own would tell a stranger nothing.</summary>
    public const string FullName = "Audio Profile System";

    /// <summary>Both together, for first-contact surfaces.</summary>
    public const string Branded = Name + " — " + FullName;

    /// <summary>Identifies the single running instance. Includes the name so an older
    /// build under the previous name does not block a new one.</summary>
    public const string SingleInstanceMutex = "APS_AudioProfileSystem_SingleInstance";

    /// <summary>Value name under the HKCU Run key.</summary>
    public const string StartupValueName = "APS";

    /// <summary>
    /// The app's semantic version, e.g. "0.1.0-alpha", from the assembly. Set it in
    /// APS.csproj — this only reads it, so there is one place to bump.
    /// </summary>
    public static string Version =>
        System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "0.0.0";
}
