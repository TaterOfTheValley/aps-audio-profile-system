namespace APS;

/// <summary>
/// Undo for profile switches.
///
/// <para>Adapted from DLS's LayoutSafety, with its central mechanism deliberately
/// removed. DLS reverts a display change unless you confirm it, because a display
/// switch can leave you staring at a monitor it just turned off — recovery has to
/// be the thing that needs no input, since you may not be able to see the button.</para>
///
/// <para>Audio has no equivalent trap. A wrong output device is obvious within a
/// second, the tray icon is still right there, and nothing about the mistake
/// prevents you from fixing it. A twenty-second countdown after every switch
/// would be pure friction protecting against a failure that cannot happen.</para>
///
/// <para>So the snapshot and the undo window stay; the confirmation dialog and
/// the auto-revert do not.</para>
/// </summary>
internal static class AudioSafety
{
    public const int UndoSeconds = 20;

    private static AudioProfile? _snapshot;
    private static DateTime _expiresUtc;

    /// <summary>Raised when undo becomes available or stops being available, so the
    /// tray can show or hide its undo entry without polling.</summary>
    public static event EventHandler? UndoStateChanged;

    public static bool CanUndo => _snapshot != null && DateTime.UtcNow < _expiresUtc;

    public static int RemainingSeconds =>
        CanUndo ? Math.Max(0, (int)Math.Ceiling((_expiresUtc - DateTime.UtcNow).TotalSeconds)) : 0;

    public static bool Apply(AudioProfile profile, out string message)
    {
        // Captured BEFORE applying: this is the exact configuration to come back
        // to, including any roles the profile itself does not touch.
        var snapshot = ProfileEngine.Capture("Previous devices");

        if (!ProfileEngine.Apply(profile, out message)) return false;

        _snapshot = snapshot;
        _expiresUtc = DateTime.UtcNow.AddSeconds(UndoSeconds);
        UndoStateChanged?.Invoke(null, EventArgs.Empty);

        return true;
    }

    public static bool Undo(out string message)
    {
        if (!CanUndo || _snapshot == null)
        {
            message = "Nothing to undo.";
            return false;
        }

        var snapshot = _snapshot;

        // Cleared before the restore is attempted. If the restore itself fails
        // there is nothing useful to retry — re-applying a configuration that was
        // just rejected only produces the same error, and leaving undo enabled
        // invites a loop. DLS makes the same call for the same reason.
        _snapshot = null;
        _expiresUtc = DateTime.MinValue;

        bool ok = ProfileEngine.Apply(snapshot, out message);
        if (ok) message = "Restored the previous devices.";

        UndoStateChanged?.Invoke(null, EventArgs.Empty);
        return ok;
    }
}
