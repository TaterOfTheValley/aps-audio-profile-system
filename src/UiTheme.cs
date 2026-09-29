namespace APS;

/// <summary>Windows' own "Text size" setting, for the windows that lay themselves out.</summary>
internal static class UiScaling
{
    private static float _textScale;

    /// <summary>
    /// Settings › Accessibility › Text size, as a multiplier from 1 to 2.25. It is
    /// separate from the display scale and applies on top of it. Nothing in WinForms
    /// reads it, so a window that wants to honour it has to apply it to its fonts.
    /// </summary>
    public static float TextScale
    {
        get
        {
            if (_textScale <= 0) _textScale = ReadTextScale();
            return _textScale;
        }
    }

    private static float ReadTextScale()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            if (key?.GetValue("TextScaleFactor") is int percent) return Math.Clamp(percent, 100, 225) / 100f;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // Unreadable is the same as unset: text at its normal size.
        }
        return 1f;
    }
}

/// <summary>
/// The type ramp, in design pixels at 100% scale. Anchored to Windows' own sizes —
/// body text is 9pt, 12px at 100% — so a window reads as part of the system.
/// </summary>
internal static class UiType
{
    public const float Caption = 11f;
    public const float Body = 12f;
    public const float Subtitle = 16f;

    /// <summary>
    /// Segoe UI Variable where Windows 11 has it, since that is what its own UI is set
    /// in; plain Segoe UI everywhere else. Probed rather than assumed: GDI+ silently
    /// substitutes Microsoft Sans Serif for a family it cannot find.
    /// </summary>
    public static readonly string Family = Resolve("Segoe UI Variable Text", "Segoe UI");

    /// <summary>The optical-size cut for headings, where the Text cut looks loose.</summary>
    public static readonly string DisplayFamily = Resolve("Segoe UI Variable Display", Family);

    private static string Resolve(string preferred, string fallback)
    {
        try
        {
            using var probe = new Font(preferred, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
            return probe.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? preferred : fallback;
        }
        catch (ArgumentException)
        {
            return fallback;
        }
    }
}

internal static class UiTheme
{
    public static readonly Color Bg = Color.FromArgb(14, 13, 11);
    public static readonly Color Panel = Color.FromArgb(22, 20, 17);
    public static readonly Color Card = Color.FromArgb(28, 25, 21);
    public static readonly Color CardActive = Color.FromArgb(46, 38, 25);
    public static readonly Color CardHover = Color.FromArgb(38, 33, 27);
    public static readonly Color Input = Color.FromArgb(18, 16, 14);
    public static readonly Color Gold = Color.FromArgb(232, 189, 99);
    public static readonly Color GoldHover = Color.FromArgb(248, 215, 135);
    public static readonly Color GoldDim = Color.FromArgb(141, 109, 50);
    public static readonly Color Muted = Color.FromArgb(170, 160, 140);
    public static readonly Color Line = Color.FromArgb(58, 50, 40);
    public static readonly Color Text = Color.FromArgb(245, 240, 230);
    public static readonly Color Ink = Color.FromArgb(14, 13, 11);
    public static readonly Color Danger = Color.FromArgb(232, 131, 117);

    /// <summary>Applies the app's button colors and borders, preserving font and size.</summary>
    public static void StyleButton(Button btn, bool primary)
    {
        btn.UseMnemonic = false;
        btn.BackColor = primary ? Gold : Card;
        btn.ForeColor = primary ? Ink : Text;
        btn.FlatStyle = FlatStyle.Flat;
        btn.Cursor = Cursors.Hand;
        btn.FlatAppearance.BorderSize = primary ? 0 : 1;
        btn.FlatAppearance.BorderColor = Line;
        btn.FlatAppearance.MouseOverBackColor = primary ? GoldHover : CardHover;
        btn.EnabledChanged += (_, _) =>
        {
            btn.BackColor = btn.Enabled ? (primary ? Gold : Card) : Card;
            btn.ForeColor = btn.Enabled ? (primary ? Ink : Text) : Muted;
        };
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    private const int DwmUseImmersiveDarkMode = 20;

    /// <summary>
    /// Gives a window a dark title bar, so the frame matches the dark window inside it.
    /// Harmless where unsupported: before Windows 10 20H1 the call just fails.
    /// </summary>
    public static void UseDarkTitleBar(Form form)
    {
        void Apply()
        {
            int on = 1;
            DwmSetWindowAttribute(form.Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int));
        }

        if (form.IsHandleCreated) Apply();
        form.HandleCreated += (_, _) => Apply();
    }

    /// <summary>Dark scroll bars for a control that has its own, such as a multi-line
    /// TextBox, which would otherwise draw light ones inside a dark window.</summary>
    public static void UseDarkScrollBars(Control control)
    {
        void Apply() => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);

        if (control.IsHandleCreated) Apply();
        control.HandleCreated += (_, _) => Apply();
    }
}
