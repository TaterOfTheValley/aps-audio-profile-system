using Microsoft.Win32;
using Velopack;
using Velopack.Sources;

namespace APS;

internal enum UpdateChannel
{
    /// <summary>Only full releases.</summary>
    Stable,

    /// <summary>Full releases and the automatic alpha builds that lead up to them.</summary>
    Alpha
}

/// <summary>Finds updates in the public source repository's releases.</summary>
internal static class UpdateService
{
    public const string RepositoryUrl =
        "https://github.com/TaterOfTheValley/aps-audio-profile-system";

    private const string SettingsKey = @"Software\APS";
    private const string ChannelValue = "UpdateChannel";

    /// <summary>True for a build whose version has a pre-release tail, such as
    /// <c>0.1.2-alpha.7</c>.</summary>
    public static bool RunningPrerelease =>
        AppInfo.Version.Contains('-', StringComparison.Ordinal);

    /// <summary>
    /// Which releases this install follows. What the user last chose, and until they
    /// choose, whatever they are already running: someone who installed an alpha
    /// build wants the next one, and someone on a full release does not want an alpha
    /// they never asked for.
    ///
    /// <para>Kept in the per-user registry beside the other install-scoped choices,
    /// not in the settings file — that file holds profiles, and is versioned and
    /// migrated for that.</para>
    /// </summary>
    public static UpdateChannel Channel
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKey);
                if (key?.GetValue(ChannelValue) is string chosen)
                {
                    if (chosen.Equals("alpha", StringComparison.OrdinalIgnoreCase)) return UpdateChannel.Alpha;
                    if (chosen.Equals("stable", StringComparison.OrdinalIgnoreCase)) return UpdateChannel.Stable;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
            {
                // Unreadable is the same as unchosen.
            }

            return RunningPrerelease ? UpdateChannel.Alpha : UpdateChannel.Stable;
        }
    }

    public static bool Set(UpdateChannel channel, out string error)
    {
        error = string.Empty;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKey, writable: true);
            key?.SetValue(ChannelValue, channel == UpdateChannel.Alpha ? "alpha" : "stable", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// A manager for the current channel. Built fresh after a change: the source is
    /// fixed when a manager is created.
    ///
    /// <para>Switching from alpha to stable never downgrades. A newer alpha of the
    /// same number sorts below the release it leads up to, so an install on
    /// <c>0.1.2-alpha.7</c> simply waits for a stable release above it.</para>
    /// </summary>
    public static UpdateManager CreateManager() => new(
        new GithubSource(RepositoryUrl, accessToken: null,
            prerelease: Channel == UpdateChannel.Alpha));
}
