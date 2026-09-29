namespace APS;

/// <summary>
/// Where APS keeps its data.
///
/// Settings live in <c>%LOCALAPPDATA%\APS\.aps</c>, not beside the executable. That
/// makes them belong to the user account rather than to a folder, so the app can be
/// moved, re-downloaded, or launched from anywhere and still find the same profiles.
///
/// Local rather than Roaming deliberately: a profile identifies audio devices by
/// endpoint IDs that are specific to one machine's driver stack, so roaming it to
/// another machine would sync data that cannot match anything there.
/// </summary>
internal static class AppPaths
{
    private const string SettingsFileName = ".aps";

    /// <summary>An empty <c>.aps</c> beside the exe switches to portable mode: settings
    /// stay in that folder and travel with it. Useful on a USB stick, and it means the
    /// per-user default is a default rather than a rule.</summary>
    public static bool IsPortable { get; private set; }

    public static string SettingsDirectory { get; private set; } = ResolveDirectory();

    public static string SettingsFile => Path.Combine(SettingsDirectory, SettingsFileName);

    private static string ResolveDirectory()
    {
        try
        {
            string beside = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
                SettingsFileName);

            if (File.Exists(beside))
            {
                IsPortable = true;
                return Path.GetDirectoryName(beside)!;
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "APS");
        }
        catch
        {
            // Fall through to the executable's own folder.
        }

        return Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// Resolves and creates the settings folder. Called once at startup, before any
    /// settings are read.
    ///
    /// Nothing is imported from earlier builds: the app is pre-release, so a
    /// profiles.json beside the executable is simply ignored rather than carried
    /// forward. Migration support starts from the first shipped format.
    /// </summary>
    public static void Initialise()
    {
        SettingsDirectory = ResolveDirectory();

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
        }
        catch
        {
            // A failure here surfaces properly through the preflight writability
            // check, which names the folder and says what to do about it.
        }
    }
}
