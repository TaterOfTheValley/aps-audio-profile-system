using System.Reflection;
using System.Text.Json;

namespace APS;

/// <summary>
/// The on-disk shape of the settings file.
///
/// Wrapping the profiles in an envelope rather than writing a bare array is what makes
/// the format upgradable: a version that lives beside the data can be read before the
/// data is interpreted, so a future build knows what it is looking at instead of
/// guessing from the contents.
/// </summary>
internal sealed class SettingsEnvelope
{
    /// <summary>
    /// MAJOR.MINOR, following semver's compatibility contract rather than a bare
    /// counter: MAJOR changes when existing fields change meaning or disappear, MINOR
    /// when fields are only added. That distinction is the whole point — it lets a
    /// build tell "newer but still readable" from "newer and not safe to touch",
    /// which a single incrementing number cannot express.
    ///
    /// PATCH is omitted deliberately: a file format has no bug-fix axis. A change
    /// either affects how the data reads or it does not.
    /// </summary>
    public string FormatVersion { get; set; } = ProfileManager.CurrentFormat.ToString(2);

    public string App { get; set; } = AppInfo.Name;

    /// <summary>Which build wrote this, for diagnostics. Compatibility decisions key
    /// off FormatVersion only — the app version and the format version move
    /// independently, which is exactly why they are separate fields.</summary>
    public string AppVersion { get; set; } = string.Empty;

    public DateTime SavedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// How often the guard looks for drift, in seconds.
    ///
    /// <para>Here rather than in a settings UI there is no room for: "Edit profiles…"
    /// already opens this file, so a field beside the profiles is a setting somebody
    /// can actually reach. Out-of-range values are bounded when they are read — see
    /// <see cref="ProfileGuard.MinPollSeconds"/> — and left alone here, so the file
    /// still says what was typed into it.</para>
    /// </summary>
    public int PollSeconds { get; set; } = ProfileGuard.DefaultPollSeconds;

    public List<AudioProfile> Profiles { get; set; } = new();
}

public static class ProfileManager
{
    /// <summary>
    /// The settings format this build writes.
    ///
    /// Bump MINOR when adding optional fields — older builds keep working. Bump MAJOR
    /// when an existing field changes meaning or goes away, and add a migration step.
    /// </summary>
    public static readonly Version CurrentFormat = new(1, 2);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Set when loading did something the user should know about — a
    /// migration, or a file from a newer build. Read once at startup.</summary>
    public static string? LastNotice { get; private set; }

    /// <summary>
    /// The poll interval last read from the settings file, in seconds, and what the
    /// next save will write back.
    ///
    /// <para>Carried here rather than returned from <see cref="LoadProfiles"/>
    /// because every caller of that wants profiles and none of them wants a tuple.
    /// Held raw: <see cref="ProfileGuard.PollSeconds"/> is what bounds it.</para>
    /// </summary>
    public static int PollSeconds { get; set; } = ProfileGuard.DefaultPollSeconds;

    public static string ConfigPath => AppPaths.SettingsFile;

    public static List<AudioProfile> LoadProfiles()
    {
        LastNotice = null;

        // Reset before the reads that can fail, so a file that cannot be parsed
        // leaves the default rather than whatever the last good file said.
        PollSeconds = ProfileGuard.DefaultPollSeconds;

        try
        {
            string path = AppPaths.SettingsFile;
            if (!File.Exists(path)) return new List<AudioProfile>();

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new List<AudioProfile>();

            var envelope = Parse(json, out Version found);
            if (envelope == null) return new List<AudioProfile>();

            if (found.Major > CurrentFormat.Major)
            {
                // Breaking change: fields this build reads may mean something else
                // entirely. Refuse the data rather than misinterpret it — a profile
                // applied from a misread file drives real hardware.
                string backup = Backup(path, found);
                LastNotice =
                    $"Your settings use format {found.ToString(2)}, which this build of " +
                    $"{AppInfo.Name} ({AppInfo.Version}) cannot read. They were saved as " +
                    $"{Path.GetFileName(backup)} and this session starts empty.";
                return new List<AudioProfile>();
            }

            // Before the migrate branch below, which saves: a file that already names
            // an interval should be re-written with the one it named, not with the
            // default it has not been read into yet.
            PollSeconds = envelope.PollSeconds;

            if (found.Minor > CurrentFormat.Minor)
            {
                // Additive-only change: everything this build understands is still
                // correct, so load normally. But saving would drop the fields it does
                // not know about, so keep the original first.
                string backup = Backup(path, found);
                LastNotice =
                    $"Your settings were written by a newer {AppInfo.Name}. They work here, " +
                    $"but saving drops the newer settings — a copy is kept as {Path.GetFileName(backup)}.";
            }
            else if (found < CurrentFormat)
            {
                envelope = Migrate(envelope, found);
                LastNotice = $"Settings upgraded from format {found.ToString(2)} to {CurrentFormat.ToString(2)}.";

                // Persist immediately so the upgrade is not redone on every launch.
                TrySaveProfiles(envelope.Profiles, out _);
            }

            Reconcile(envelope.Profiles);
            return envelope.Profiles;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
            LastNotice = $"Your settings could not be read ({ex.GetType().Name}). Starting with no profiles.";
            return new List<AudioProfile>();
        }
    }

    private static SettingsEnvelope? Parse(string json, out Version version)
    {
        version = CurrentFormat;

        var envelope = JsonSerializer.Deserialize<SettingsEnvelope>(json, JsonOptions);
        if (envelope == null) return null;

        // An unparseable or absent version is treated as current rather than as a
        // failure: the alternative is discarding a file that is probably fine.
        if (Version.TryParse(envelope.FormatVersion, out var parsed)) version = parsed;
        return envelope;
    }

    /// <summary>
    /// Walks the file forward one version at a time. Sequential steps rather than a
    /// jump straight to current, so a file three versions behind is handled by the
    /// same code that handled it one version behind.
    ///
    /// Empty today — v1 is the first shipped format, so nothing older exists. Each
    /// future format bump adds a `case n:` here that upgrades n to n+1.
    /// </summary>
    private static SettingsEnvelope Migrate(SettingsEnvelope envelope, Version from)
    {
        // Each future format bump adds its step here, in order:
        //   if (from < new Version(2, 0)) UpgradeTo2_0(envelope);
        //   if (from < new Version(2, 1)) UpgradeTo2_1(envelope);
        _ = from;

        envelope.FormatVersion = CurrentFormat.ToString(2);
        return envelope;
    }

    private static string Backup(string path, Version version)
    {
        string backup = $"{path}.v{version.ToString(2)}.bak";
        try
        {
            File.Copy(path, backup, overwrite: true);
        }
        catch
        {
            // Best effort; the notice still tells the user what happened.
        }
        return backup;
    }

    /// <summary>
    /// Unlike DLS, there is no live hardware to refresh a saved reference against
    /// here: this only drops structurally invalid data. A <see cref="DeviceRef"/>
    /// with neither an endpoint id nor a usable fingerprint (<see
    /// cref="DeviceRef.IsUnusable"/>) can never resolve to anything, so it is
    /// cleared to null rather than kept as dead weight a menu build would have to
    /// special-case forever. A profile with no refs at all is left alone — it is
    /// simply empty, not invalid, and <see cref="AudioProfile.IsEmpty"/> already
    /// says so to anything that displays it.
    ///
    /// Deliberately does <b>not</b> enumerate audio devices. Reconcile runs at
    /// load, on every launch, before the tray or the CLI has done anything — if it
    /// resolved refs against live hardware here, starting the app would depend on
    /// every referenced device being plugged in, and a load must never fail (or
    /// hang, or COM-initialize) just because a USB headset is at home. Resolving
    /// a DeviceRef against what is actually connected is <see
    /// cref="DeviceResolver"/>'s job, and it runs per-apply and per-menu-open,
    /// where up-to-date hardware state actually matters.
    /// </summary>
    private static void Reconcile(List<AudioProfile> profiles)
    {
        foreach (var p in profiles)
        {
            if (p.Output?.IsUnusable == true) p.Output = null;
            if (p.OutputComms?.IsUnusable == true) p.OutputComms = null;
            if (p.Input?.IsUnusable == true) p.Input = null;
        }
    }

    public static void SaveProfiles(List<AudioProfile> profiles) => TrySaveProfiles(profiles, out _);

    public static bool TrySaveProfiles(List<AudioProfile> profiles, out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            Directory.CreateDirectory(AppPaths.SettingsDirectory);

            var envelope = new SettingsEnvelope
            {
                FormatVersion = CurrentFormat.ToString(2),
                App = AppInfo.Name,
                AppVersion = AppInfo.Version,
                SavedUtc = DateTime.UtcNow,
                PollSeconds = PollSeconds,
                Profiles = profiles
            };

            // Write to a temporary file and swap. A crash or a full disk mid-write
            // would otherwise leave a truncated settings file, which reads as "no
            // profiles" — the one outcome worse than failing to save at all.
            string target = AppPaths.SettingsFile;
            string temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(envelope, JsonOptions));

            if (File.Exists(target)) File.Replace(temp, target, destinationBackupFileName: null);
            else File.Move(temp, target);

            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            errorMessage = $"Could not save settings to {AppPaths.SettingsFile}: {ex.Message}";
            return false;
        }
    }
}
