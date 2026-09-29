using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace APS;

/// <summary>
/// The tray menu, as a drawn popup rather than a ContextMenuStrip.
///
/// ToolStrip's layout engine sizes rows from the item's Text and its own font, so an
/// owner-drawn row that paints something larger simply gets clipped — the menu has no
/// idea what was drawn inside it. Rather than fight that, this measures its own
/// content and sizes the window to fit, which also means DPI scaling is one explicit
/// multiplier instead of an interaction between three layout systems.
///
/// It is the surface this app is actually used through, so it earns the same care as
/// DLS's tray menu did — but the content differs. A display layout is a shape, so DLS
/// draws it; an audio profile is a set of device names, so there is nothing to draw
/// but text. In its place, the StatusEntry "Now:" block takes over as the thing that
/// answers "which one is this?" — it shows what is actually live rather than a
/// picture of it.
/// </summary>
internal sealed class TrayPopup : Form
{
    internal abstract class Entry
    {
        public Action? Invoke;
        public bool Enabled = true;

        /// <summary>
        /// True when invoking this should leave the popup on screen and redraw it
        /// from freshly built entries, rather than closing first.
        ///
        /// <para>Closing on every click is right for a command that finishes
        /// something — applying a profile, opening the settings file — because the
        /// menu has done its job. It is wrong for a tick box or a reorder, where the
        /// whole point is to see the change and very often make another: ticking
        /// "keep an eye on this" and having the menu vanish means reopening it to
        /// find out what the tick did, and moving a profile up three places meant
        /// four round trips through the tray icon.</para>
        /// </summary>
        public bool KeepOpen;

        public virtual bool Selectable => Enabled && Invoke != null;
    }

    /// <summary>Which mark <see cref="RowAction"/> paints. Not an icon font — see
    /// <see cref="TrayPopup.DrawGlyph"/>.</summary>
    internal enum RowGlyph { Rename, Update, Delete }

    /// <summary>
    /// One of the small glyph buttons at the right of a profile row.
    ///
    /// <para>These three used to be commands on a page of the profile's own, reached
    /// by a "⋯". They are on the row itself now because reordering became a drag,
    /// and a drag is only possible on a list you can see — which meant the list had
    /// to become the one place a profile is maintained rather than one of two.</para>
    ///
    /// <para>A separate hit zone rather than a second click behaviour on the row: the
    /// row's one job is switching to the profile, and a menu where the same click
    /// sometimes applies your profile and sometimes deletes it is not one anybody
    /// should have to think before using.</para>
    /// </summary>
    internal sealed class RowAction
    {
        public required RowGlyph Glyph;

        /// <summary>Shown in place of the row's device subtitle while the pointer is
        /// on this glyph. Three unlabelled 13px marks would otherwise be a puzzle,
        /// and the row already has a line of small text to lend.</summary>
        public required string Label;

        public required Action Invoke;

        /// <summary>Draws the glyph and its hover label in the warning colour. The
        /// confirmation still asks, but the row should have said what kind of thing
        /// this is before it was clicked.</summary>
        public bool Danger;
    }

    internal sealed class ProfileEntry : Entry
    {
        public required AudioProfile Profile;

        /// <summary>The machine actually looks like this profile right now.</summary>
        public bool IsLive;

        /// <summary>
        /// The last profile switched to, when nothing matches any more.
        ///
        /// <para>Separate from <see cref="IsLive"/> because it used to be folded into
        /// it, and a filled dot on a profile the machine has drifted away from is the
        /// menu asserting something untrue about the hardware — the one thing this
        /// menu exists to be right about. Drawn as a ring instead: same slot, so it
        /// still says "this is the one you picked", without saying it is in
        /// effect.</para>
        /// </summary>
        public bool IsChosen;
        public string Subtitle = "";
        public string? UnavailableReason;

        /// <summary>
        /// Rename, replace, delete — drawn at the right of the row, left to right in
        /// that order.
        ///
        /// <para>They stay live even when the row itself is disabled: a profile whose
        /// devices are unplugged is exactly one you might want to rename or
        /// delete.</para>
        /// </summary>
        public List<RowAction> Actions = new();

        /// <summary>
        /// Selectable even when the profile cannot be switched to, unlike every
        /// other entry.
        ///
        /// <para>Selection is what the keyboard's F2, Delete and Ctrl+Up act on, and
        /// those have to reach an unavailable profile for the same reason its glyphs
        /// stay live for the mouse. Enter on one still does nothing — its
        /// <see cref="Entry.Invoke"/> is null and <see cref="TrayPopup.Fire"/> ignores
        /// that — so this widens what can be pointed at, not what can be
        /// applied.</para>
        /// </summary>
        public override bool Selectable => true;
    }

    internal sealed class StatusEntry : Entry
    {
        public required List<(string Label, string Value)> Rows;
    }

    internal sealed class CommandEntry : Entry
    {
        public required string Text;
        public string? Detail;
        public bool Emphasis;

        /// <summary>Null for a plain command; true/false draws a checkbox, so
        /// settings and actions can share one list without looking alike.</summary>
        public bool? Checked;
    }

    /// <summary>
    /// A profile row turned into a name field, in place.
    ///
    /// <para>This was a modal dialog. A dialog cannot be shown over the tray popup —
    /// the popup closes on <c>Deactivate</c> — so renaming meant the menu vanishing,
    /// a window opening somewhere else on the screen, and the menu being rebuilt
    /// afterwards to put you back where you were. Three things happening to answer
    /// one question about one row. Doing it on the row costs a text field and a
    /// keyboard mode, and nothing else moves.</para>
    /// </summary>
    internal sealed class EditEntry : Entry
    {
        /// <summary>Identifies the edit across rebuilds — the profile's id.
        /// <see cref="Entry.KeepOpen"/> clicks rebuild every entry, so without a key
        /// the field would be torn down and recreated on each redraw and lose what
        /// had been typed.</summary>
        public required string Key;

        public required string Title;
        public required string Initial;

        /// <summary>Returns the reason this name cannot be used, or null. Run on
        /// every keystroke and again before committing, so a rejected name is
        /// refused with the reason under the field rather than after you have
        /// committed to it.</summary>
        public required Func<string, string?> Validate;

        public required Action<string> Commit;
        public required Action Cancel;

        public override bool Selectable => false;
    }

    /// <summary>
    /// A profile row turned into a question with two buttons, in place. The other
    /// half of what used to be a dialog — see <see cref="EditEntry"/>.
    /// </summary>
    internal sealed class PromptEntry : Entry
    {
        public required string Title;

        /// <summary>One line, on the subtitle line of the row this replaces, and
        /// ellipsised if it does not fit. It used to wrap to as many lines as it
        /// needed and set the row's height from the result; it now has to say its
        /// piece in the space a device subtitle gets, which is a constraint on the
        /// sentence rather than on the row.</summary>
        public string Detail = "";

        /// <summary>The confirming button's verb — "Delete", not "OK" — so it says
        /// what it will do rather than making the reader hold the question in their
        /// head to know what they are agreeing to.</summary>
        public required string ConfirmText;

        public required Action Confirm;
        public required Action Cancel;

        /// <summary>Draws the question and its confirming button in the warning
        /// colour, for the one that destroys something.</summary>
        public bool Danger;

        public override bool Selectable => false;
    }

    internal sealed class SeparatorEntry : Entry
    {
        public override bool Selectable => false;
    }

    internal sealed class HeadingEntry : Entry
    {
        public required string Text;

        /// <summary>Right-aligned, dimmer, sentence case — a note about the list
        /// rather than part of its title. Drag-to-reorder is a gesture with no
        /// control to point at, so something has to say it is there.</summary>
        public string? Hint;

        public override bool Selectable => false;
    }

    private static TrayPopup? _open;

    /// <summary>Rebuilds the entries for a <see cref="Entry.KeepOpen"/> click. Null
    /// for the screenshot capture, which has no live menu behind it.</summary>
    private readonly Func<List<Entry>>? _build;

    /// <summary>
    /// Commits a drag: put this profile at this position among the profile rows.
    /// Null leaves the rows undraggable — the screenshot capture, which has no list
    /// to write back to.
    /// </summary>
    private readonly Action<AudioProfile, int>? _reorder;

    private List<Entry> _entries;
    private readonly float _scale;

    /// <summary>Where each entry was drawn, indexed by its position in
    /// <see cref="_entries"/> rather than by draw order — which a drag
    /// rearranges.</summary>
    private readonly List<Rectangle> _rows = new();

    /// <summary>The glyph hit zones, rebuilt on every paint alongside
    /// <see cref="_rows"/>.</summary>
    private readonly List<(int Row, int Slot, Rectangle Zone, RowAction Action)> _zones = new();

    /// <summary>The buttons of an open <see cref="PromptEntry"/>, index 0 cancel and
    /// 1 confirm. Kept apart from <see cref="_zones"/> because they are drawn and
    /// highlighted as buttons rather than as glyphs, and because only one prompt is
    /// ever open.</summary>
    private readonly List<(int Row, int Index, Rectangle Zone, Action Invoke)> _buttons = new();

    /// <summary>Doubles as pointer hover and keyboard focus, the same way
    /// <see cref="_hot"/> does for rows.</summary>
    private (int Row, int Index) _hotButton = (-1, -1);

    /// <summary>The live name field, or null when no row is being edited. A real
    /// TextBox rather than hand-drawn text: a caret, selection, Home/End, clipboard
    /// and IME are a great deal of behaviour to reimplement for a field people type
    /// six characters into.</summary>
    private TextBox? _editor;

    /// <summary>The entry <see cref="_editor"/> belongs to, replaced on every
    /// rebuild so its Commit and Cancel are never stale.</summary>
    private EditEntry? _editing;

    private string? _editKey;
    private string? _editError;

    /// <summary>True while <see cref="RefreshInPlace"/> holds the window's painting
    /// suspended, so anything that would draw or take focus can wait for it.</summary>
    private bool _refreshing;

    private bool _activatePending;

    /// <summary>
    /// The size the window will not go below for as long as it is open.
    ///
    /// <para>Resizing a visible top-level window reallocates its DWM redirection
    /// surface, and the compositor can put a frame on screen from it before the paint
    /// that fills it lands. That is the flicker WM_SETREDRAW, SWP_NOREDRAW and
    /// NOCOPYBITS do not reach: all three work inside the window's own painting, and
    /// none of them is a promise to DWM. The only reliable answer is not to resize.
    ///
    /// <para>So the popup keeps the largest size it has needed. A rebuild that needs
    /// less draws into the room it already has, and the click that changes no row
    /// count — which is most of them, now that the inline forms are the height of the
    /// row they replace — changes no geometry at all.</para>
    ///
    /// <para>Per popup rather than static: a floor that outlived the menu would hold
    /// space for profiles that have since been deleted. For the same reason it is
    /// dropped mid-menu whenever the profile list changes length — see
    /// <see cref="ListRowCount"/>.</para>
    /// </summary>
    private Size _floor;

    /// <summary>How many rows the profile list had when <see cref="_floor"/> was last
    /// set. Compared on every rebuild; see <see cref="ListRowCount"/>.</summary>
    private int _listRows;

    private Point _anchor;
    private int _hot = -1;
    private (int Row, int Slot) _hotZone = (-1, -1);

    // ------------------------------------------------------- drag-to-reorder

    /// <summary>The profile row the left button went down on, before it is known
    /// whether this is a click or a drag. -1 when the press was not on one.</summary>
    private int _pressEntry = -1;
    private Point _pressPoint;

    private bool _dragging;

    /// <summary>Index into <see cref="_entries"/> of the row being dragged.</summary>
    private int _dragEntry = -1;

    /// <summary>Where in the row the pointer grabbed it, so the row travels under the
    /// same part of itself that was picked up rather than snapping its top edge to
    /// the cursor.</summary>
    private int _dragGrabDy;

    private int _dragY;

    /// <summary>Top of the profile block when the drag began. Captured once: the
    /// block's geometry cannot change mid-drag — same count, same row height — and
    /// reading it back from <see cref="_rows"/> each frame would read the rearranged
    /// positions the drag itself produced.</summary>
    private int _blockTop;

    /// <summary>Position among the profile rows the dragged one would land in.</summary>
    private int _dropIndex = -1;

    private const int PadX = 14;
    private const int PadY = 8;

    // DLS's LayoutRowH (52) was sized to fit a 28px-tall layout diagram centered
    // in the row. There is no diagram here — a profile row is two lines of text —
    // so this is re-tuned directly from the fonts that actually fill it: NameFont
    // is declared at 15px, SubFont at 11.5px. A line box a few pixels taller than
    // the font's own size reads as normal leading rather than a cramped box; a
    // couple of pixels above and below the pair reads as a row rather than two
    // lines jammed against their neighbours. 6 (top) + 19 (name line) + 16 (sub
    // line) + 5 (bottom) = 46 — six pixels shorter than DLS's glyph-driven row,
    // with the width the glyph used to occupy reclaimed entirely (see DotDiameter
    // / DotGap below).
    private const int ProfilePadTop = 6;
    private const int ProfileNameH = 19;
    private const int ProfileSubH = 16;
    private const int ProfilePadBottom = 5;
    private const int ProfileRowH = ProfilePadTop + ProfileNameH + ProfileSubH + ProfilePadBottom;

    // The live dot replaces DLS's accent bar. It is small enough that its own
    // column barely register as a gutter, which is the point — the eye should
    // land on the name, not on a decoration next to it.
    private const int DotDiameter = 6;
    private const int DotGap = 8;

    // The glyph buttons at the right end of a profile row. 22px is the narrowest
    // square that stays a comfortable target at 16px-tray scale, and three of them at
    // that width cost the device subtitle 40px against the single 26px "⋯" they
    // replace — which Measure() gives straight back, because the popup sizes itself
    // to its widest row rather than clipping it.
    private const int ActionZoneW = 22;
    private const int ActionGlyph = 13;

    // How far the pointer has to travel with the button down before a press on a
    // profile row stops being a click and becomes a drag. Small enough that a
    // deliberate drag starts the moment you mean it; large enough that the shake in
    // an ordinary click never reorders anything.
    private const int DragThreshold = 4;

    // StatusEntry is new — no DLS equivalent. Each row is a single line at
    // SubFont's size (11.5px), so 18px gives it a comfortable line box without
    // the extra height a two-line ProfileRowH line would waste on a single line.
    // 7px above and below the whole block sets it apart visually from the
    // profile rows around it without ballooning the menu when all four rows
    // (out / calls / mic / — whatever the caller collapses to) are shown at once.
    private const int StatusPadY = 7;
    private const int StatusRowH = 18;

    // The two inline states a profile row can take. Both are exactly ProfileRowH,
    // and both put their text on the same two lines the profile row uses.
    //
    // They used to be half as tall again, the reasoning being that a question should
    // not look like the rows either side of it. What that actually bought was a
    // window resize on every rename and every confirmation — and a top-level window
    // that resizes while visible has its DWM redirection surface reallocated, so the
    // menu can be composed once with the new size and the old contents before the
    // paint lands. That is the hitch on the click that opens the question. Fitting
    // the row it replaces costs the explanation its second line and buys a
    // transition with no geometry change at all; what marks the row as different is
    // colour and a border, which are free.
    //
    // Neither height is spelled out here. Both rows return S(ProfileRowH) and lay
    // their parts out from the rectangle they were given, because S() rounds each
    // call independently — a set of parts that sums to 46 at scale 1 can sum to 47 at
    // 1.5, and a one-pixel disagreement is still a resize.
    private const int EditPadTop = 3;
    private const int EditFieldH = 26;
    private const int EditGap = 2;
    private const int EditFieldMinW = 200;

    // Text inset inside the field card, and the card's left edge placed so that the
    // text lands in the same column the profile name occupied. Pressing F2 should
    // put a caret in the name, not move the name first.
    private const int EditTextInset = 8;
    private const int EditCardLeft = PadX + DotDiameter + DotGap - EditTextInset;

    // The prompt mirrors the row it replaces: question on the name line, explanation
    // on the subtitle line, buttons where the glyphs were. It borrows ProfileNameH
    // and ProfileSubH rather than declaring its own, so the two rows' text sits on
    // the same baselines and the swap reads as the row changing rather than moving.
    private const int PromptButtonH = 28;
    private const int PromptButtonGap = 8;
    private const int PromptButtonMinW = 64;
    private const int PromptButtonPad = 20;
    private const int PromptTextGap = 12;

    // What a profile row reserves for the form it might have to take: a button pair
    // where its glyphs are, and a question and explanation that can run longer than
    // the name and subtitle on it now.
    //
    // Reserved rather than measured off the live prompt, because the popup has to be
    // this wide *before* the click that opens one. A confirmation that widens the
    // menu has moved every row in it, which is the same jump the equal heights were
    // for. The overhead covers the verb and the quotes around the name — "Update
    // “”?" set in CommandFont, which is smaller than the NameFont the name was
    // measured in, so the name's own width already over-estimates its half.
    private const int PromptTitleOverhead = 80;

    // Measured from the two sentences that actually use it, plus a little: at scale 1
    // the delete question needs 212 and the replace question 216. A sentence written
    // longer than this does not break anything, it just costs the resize back, so the
    // constraint belongs on the sentences and this is where it is written down.
    private const int PromptDetailW = 220;

    private const int CommandRowH = 34;
    private const int HeadingH = 26;
    private const int SeparatorH = 9;
    private const int MinWidth = 320;
    private const int MaxWidth = 520;

    /// <param name="build">Builds the menu, and is called again after every
    /// <see cref="Entry.KeepOpen"/> click. A delegate rather than a list because a
    /// menu that stays open has to be able to show what the click just did.</param>
    /// <param name="reorder">Commits a drag. Omitting it leaves the profile rows
    /// undraggable.</param>
    public static void Show(Func<List<Entry>> build, Point anchor, float scale,
                            Action<AudioProfile, int>? reorder = null)
    {
        Dismiss();
        _open = new TrayPopup(build(), scale, build, reorder);
        _open.PlaceNear(anchor);
        _open.Show();

        // Focus is what lets Deactivate close the popup and what makes the arrow
        // keys work. A tray menu that stays open after you click elsewhere is worse
        // than one that never opened.
        _open.Activate();
    }

    /// <summary>Puts the keyboard on Cancel when a prompt appears, and takes it off
    /// the buttons when one goes away.</summary>
    private void SyncPromptFocus()
    {
        int row = _entries.FindIndex(e => e is PromptEntry);
        if (row < 0) { _hotButton = (-1, -1); return; }
        if (_hotButton.Row != row) _hotButton = (row, 0);
    }

    public static void Dismiss()
    {
        if (_open is { IsDisposed: false }) _open.Close();
        _open = null;
    }

    /// <summary>Builds the popup without showing it, so its rendering can be captured
    /// by the --screenshot-menu diagnostic.</summary>
    internal static TrayPopup CreateForCapture(IEnumerable<Entry> entries, float scale) =>
        new(entries.ToList(), scale);

    /// <summary>
    /// Freezes the captured popup mid-drag, for <c>--screenshot-menu --drag</c>.
    ///
    /// <para>The floating row and the gap it came out of are the only part of this
    /// menu that a still of the resting state cannot show, and they are the part
    /// likeliest to go wrong: a row painted at an arbitrary Y, over rows it was not
    /// laid out with, on a window sized before it was lifted. Being able to reach
    /// that state from a script is worth the two lines that set it.</para>
    /// </summary>
    /// <param name="from">Position among the profile rows of the row being dragged.</param>
    /// <param name="to">Position it is currently hovering over.</param>
    internal void SimulateDragForCapture(int from, int to)
    {
        var block = ProfileBlock();
        if (from < 0 || from >= block.Count) return;

        // A throwaway paint first: the row rectangles this reads do not exist until
        // one has happened, and nothing has been shown.
        using (var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height)))
        using (var g = Graphics.FromImage(bmp))
        {
            Render(g);
        }

        int rowH = S(ProfileRowH);

        _dragEntry = block[from];
        _dropIndex = Math.Clamp(to, 0, block.Count - 1);
        _blockTop = _rows[block[0]].Y;
        _dragGrabDy = rowH / 2;

        // A third of a row below the slot it would drop into — far enough that the
        // gap and the floating row are both plainly visible, which is the whole
        // point of the capture.
        _dragY = _blockTop + _dropIndex * rowH + _dragGrabDy + rowH / 3;
        _dragging = true;
    }

    private TrayPopup(List<Entry> entries, float scale, Func<List<Entry>>? build = null,
                      Action<AudioProfile, int>? reorder = null)
    {
        _entries = entries;
        _build = build;
        _reorder = reorder;
        _scale = scale <= 0 ? 1f : scale;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(21, 19, 17);
        DoubleBuffered = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);

        Size = Measure();
        _floor = Size;
        _listRows = ListRowCount();
    }

    /// <summary>Cuts the corners as soon as there is a window to cut them from.
    /// <see cref="ApplyRegion"/> works on the handle rather than through
    /// <see cref="Control.Region"/>, so it cannot run from the constructor — and in
    /// exchange WinForms has no cached region of its own to re-apply behind it. Which
    /// is what <see cref="OnSizeChanged"/> is for: this first cut is nearly always
    /// the wrong size.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRegion();
    }

    /// <summary>
    /// Re-cuts the corners whenever the window changes size — the only rule that
    /// keeps the region and the window in agreement.
    ///
    /// <para>Without it the two depend on the order the constructor happens to run
    /// in, and that order is against us: <see cref="Measure"/> asks for a Graphics,
    /// which forces the handle, which raises <see cref="OnHandleCreated"/> — all
    /// before its result has been assigned to <see cref="Control.Size"/>. So the
    /// corners get cut to the Form's default 300x300 and the window is then sized to
    /// something much larger, leaving a full-size menu with only its top-left corner
    /// visible, in the place a full-size menu belongs.</para>
    ///
    /// <para>That was survivable only for as long as <see cref="RefreshInPlace"/>
    /// re-cut the region on every rebuild whether it had resized or not: the menu
    /// opened clipped and the first click repaired it. It is not a thing to rely on,
    /// so the size itself is what drives the region now.</para>
    /// </summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRegion();
    }

    /// <summary>
    /// Re-cuts the rounded corners to the current size.
    ///
    /// <para>Deliberately not <see cref="Control.Region"/>: WinForms' setter calls
    /// <c>SetWindowRgn</c> with <c>bRedraw</c> true, which repaints the window there
    /// and then whatever else has been suspended around it. That is the visible half
    /// of the flicker — the window is resized first and so spends a moment clipped to
    /// the region of its <em>old</em> size, which for a menu that just grew is a menu
    /// that disappears and comes back. Cutting the region without a redraw lets the
    /// resize and the new shape land in the same paint.</para>
    ///
    /// <para>The system takes ownership of the region handle, so it is not
    /// deleted here. The radius doubles because CreateRoundRectRgn is given the
    /// ellipse's size, where <see cref="RoundedPath"/> is given its radius.</para>
    /// </summary>
    private void ApplyRegion()
    {
        if (!IsHandleCreated) return;

        IntPtr region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, S(10) * 2, S(10) * 2);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(Handle, region, false) == 0) DeleteObject(region);
    }

    /// <summary>
    /// Rebuilds and redraws without closing — what a <see cref="Entry.KeepOpen"/>
    /// click does once its action has run.
    ///
    /// <para>Most of the time this changes no geometry at all — see
    /// <see cref="_floor"/> — and is a rebuild, a repaint and nothing else. When it
    /// does have to grow, it re-anchors rather than keeping the window where it was:
    /// the menu is placed with its bottom edge just above the pointer, so growing it
    /// moves only the top, and <see cref="ContentTop"/> holds every row below the
    /// change still. A row near the pointer stays under the pointer, which is what
    /// makes ticking two boxes in a row feel like one gesture.</para>
    /// </summary>
    private void RefreshInPlace()
    {
        if (_build == null) { Close(); return; }

        // The action that just ran can have closed this: a failure balloon takes
        // activation on some builds of Windows, and Deactivate closes the popup.
        // Redrawing a disposed window would turn that into a crash.
        if (IsDisposed || Disposing || !Visible) return;

        _entries = _build();

        // Never smaller than the popup has already been — see _floor. A rebuild that
        // measures shorter keeps the window and spends the difference as space above
        // the first row, which is what ContentTop is for.
        //
        // Except when the list itself got shorter. The floor is for rows that come
        // and go — ticking "keep an eye on this" reveals a second tick box and
        // unticking hides it again, and a window that resized both ways would flutter
        // under a pair of clicks. A deleted profile is not coming back, and the space
        // it left would sit above the first row for as long as the menu stayed open.
        // So the length of the profile list is what drops the floor, in either
        // direction, and everything else holds it.
        var natural = Measure();

        int listRows = ListRowCount();
        if (listRows != _listRows)
        {
            _listRows = listRows;
            _floor = natural;
        }

        var size = new Size(Math.Max(natural.Width, _floor.Width),
                            Math.Max(natural.Height, _floor.Height));
        _floor = size;

        bool resizing = size != Size;

        // The name field is a child window, so creating or destroying one paints
        // whatever the rebuild has not painted yet.
        bool editorChanging = _entries.OfType<EditEntry>().FirstOrDefault()?.Key != _editKey;

        // Every part of the rebuild that touches the window is its own trip through
        // the window manager, and each of them draws something: resizing to the new
        // measure, re-cutting the rounded region to that size, re-anchoring above the
        // pointer, and parenting in the name field. Opening a rename or a delete
        // confirmation therefore showed several intermediate states of the menu in as
        // many milliseconds. WM_SETREDRAW holds back the ones that go through the
        // window's own painting; the two that do not — SetWindowRgn's forced redraw
        // and SetWindowPos's blit of the old pixels — are turned off at their own
        // call sites below, because a suspended window does not suspend those. The
        // RedrawWindow at the end is then the only paint that reaches the screen.
        //
        // Only when there is something to hold back, though. Suspending the painting
        // leaves the window's contents undefined until RedrawWindow restores them,
        // which is its own one-frame risk, and a click that only changes a tick or
        // swaps a row for one the same size has nothing for it to hold back: an
        // ordinary Invalidate/Update is one buffered blit and cannot tear.
        bool frozen = IsHandleCreated && (resizing || editorChanging);
        if (frozen) SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        _refreshing = true;
        try
        {
            if (resizing)
            {
                // Size and position in one SetWindowPos rather than two, so there is
                // no frame where the menu has grown but not yet moved back up above
                // the pointer. NOCOPYBITS is the other half: a window that moves and
                // resizes at once has its old pixels blitted to the new offset by
                // default, which for a menu anchored by its bottom edge means every
                // row appearing at the wrong height for a frame before the paint
                // corrects it. Nothing on the new layout is in the same place as the
                // old, so the blit has nothing worth saving and the shear is all it
                // contributes.
                var at = PlacementFor(_anchor, size);
                SetWindowPos(Handle, IntPtr.Zero, at.X, at.Y, size.Width, size.Height,
                             SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOREDRAW | SWP_NOCOPYBITS);

                // Width/Height/Location are read back by everything below.
                // SetWindowPos sends WM_WINDOWPOSCHANGED synchronously and WinForms
                // updates its cached bounds from it, so they are already correct here
                // — and OnSizeChanged has already re-cut the region off the same
                // message, inside this suspended window and without a redraw.
            }

            SyncEditor();
            SyncPromptFocus();
        }
        finally
        {
            _refreshing = false;
            if (frozen)
            {
                SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);

                // UPDATENOW rather than letting the paint queue, because the hit test
                // below reads the row rectangles that painting produces. ALLCHILDREN
                // because the name field was frozen along with its parent, and FRAME
                // because the region just changed.
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero,
                             RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
            else
            {
                Invalidate();
                Update();
            }

            if (_activatePending)
            {
                _activatePending = false;
                if (!IsDisposed && !Disposing && Visible) Activate();
            }
        }

        // Only re-derived from the pointer when the pointer is actually over the
        // menu. Ctrl+Up walks a profile down the list with the mouse parked
        // somewhere else entirely, and re-reading the cursor there would drop the
        // keyboard's selection on every press — so the second press would move
        // nothing.
        var local = PointToClient(Cursor.Position);
        if (!ClientRectangle.Contains(local)) return;

        int row = HitTest(local);
        var zone = HitZone(local);
        if (row != _hot || zone != _hotZone)
        {
            _hot = row;
            _hotZone = zone;
            Invalidate();
        }
    }

    /// <summary>
    /// The entry indices of the profile rows, which are always one contiguous run —
    /// the menu builds them in a single loop under the "PROFILES" heading. A drag is
    /// confined to this run, so a profile can never be dropped in among the commands
    /// below it.
    /// </summary>
    private List<int> ProfileBlock()
    {
        var block = new List<int>();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i] is ProfileEntry) block.Add(i);
        }
        return block;
    }

    /// <summary>True while a row is asking something — a name, or a yes/no. The rest
    /// of the menu stops responding for as long as it is: switching profiles or
    /// starting a second rename underneath an open question is how you end up
    /// answering it about the wrong profile.</summary>
    private bool Blocking => _editing != null || _entries.Any(e => e is PromptEntry);

    /// <summary>
    /// Creates, moves or tears down the name field to match the entries.
    ///
    /// <para>Called after every rebuild rather than from the paint: positioning a
    /// child control inside OnPaint invalidates the thing that is painting. It can
    /// run outside a paint because <see cref="EntryHeight"/> needs no Graphics — the
    /// one row whose height depends on measured text caches it in Measure, which
    /// always runs first.</para>
    /// </summary>
    private void SyncEditor()
    {
        var edit = _entries.OfType<EditEntry>().FirstOrDefault();

        if (edit == null) { DisposeEditor(); return; }

        bool created = _editor == null || _editKey != edit.Key;

        if (created)
        {
            DisposeEditor();
            _editKey = edit.Key;

            _editor = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = UiTheme.Input,
                ForeColor = UiTheme.Text,
                Font = new Font("Segoe UI", 15f * _scale, GraphicsUnit.Pixel)
            };

            _editor.TextChanged += (_, _) => { Revalidate(); Invalidate(); };
            Controls.Add(_editor);
        }

        _editing = edit;

        // Non-null either way, which the flow analysis cannot see through a bool:
        // created means one was just made, and not created means the field already on
        // screen is the right one.
        var editor = _editor!;

        // A single-line TextBox sizes its own height from its font, so it is placed
        // by centring that height in the card rather than by being given one.
        var card = EditCardRect(RowTop(edit));
        editor.Width = Math.Max(S(40), card.Width - S(16));
        editor.Location = new Point(card.X + S(8), card.Y + (card.Height - editor.Height) / 2);

        if (created)
        {
            // Filled after the field has its width, not in the initialiser above.
            // A TextBox scrolls to keep its caret visible and a freshly constructed
            // one is 100px wide, so a name set there scrolls its own start out of
            // sight — and widening the field afterwards does not scroll back.
            // Renaming "Living Room Speakers" opened on "oom Speakers", with the
            // beginning of the name somewhere off to the left.
            editor.Text = edit.Initial;

            if (IsHandleCreated) { editor.Focus(); editor.SelectAll(); }
        }

        Revalidate();
    }

    private void DisposeEditor()
    {
        if (_editor != null)
        {
            // Removing the control that holds focus can leave the popup with focus
            // nowhere inside it, and a popup that loses activation closes. Taking it
            // back explicitly is what keeps the menu on screen after a rename.
            bool hadFocus = _editor.Focused;

            Controls.Remove(_editor);
            _editor.Dispose();
            _editor = null;

            // Deferred when a rebuild is in flight. Activate is a foreground-window
            // call — cross-process, and the one step here slow enough to let a
            // composition frame land between the resize and the paint that follows
            // it. RefreshInPlace runs it once the window is whole again.
            if (hadFocus && !IsDisposed && !Disposing && Visible)
            {
                if (_refreshing) _activatePending = true;
                else Activate();
            }
        }

        _editing = null;
        _editKey = null;
        _editError = null;
    }

    private void Revalidate() =>
        _editError = _editing == null ? null : _editing.Validate((_editor?.Text ?? "").Trim());

    /// <summary>Where an entry starts, without needing a paint to have happened.
    /// Draw order only differs from list order mid-drag, and nothing can be dragged
    /// while a row is asking a question.</summary>
    private Rectangle RowTop(Entry target)
    {
        int y = ContentTop();
        foreach (var entry in _entries)
        {
            int h = EntryHeight(entry);
            if (ReferenceEquals(entry, target)) return new Rectangle(0, y, Width, h);
            y += h;
        }
        return Rectangle.Empty;
    }

    private void CommitEdit()
    {
        if (_editing == null || _editor == null) return;

        Revalidate();
        if (_editError != null) { Invalidate(); return; }

        var commit = _editing.Commit;
        string value = _editor.Text.Trim();

        // Torn down before the callback runs: it rebuilds the menu, and a field still
        // holding the keyboard while its row is being replaced is a field that can
        // take a keystroke meant for the list.
        DisposeEditor();
        Fire(keepOpen: true, () => commit(value));
    }

    private void CancelEdit()
    {
        if (_editing == null) return;

        var cancel = _editing.Cancel;
        DisposeEditor();
        Fire(keepOpen: true, cancel);
    }

    private int S(int v) => (int)Math.Round(v * _scale);

    private Font NameFont() => new("Segoe UI", 15f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font SubFont() => new("Segoe UI", 11.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font HotkeyFont() => new("Consolas", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font CommandFont() => new("Segoe UI", 13.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
    private Font HeadingFont() => new("Segoe UI", 11f * _scale, FontStyle.Bold, GraphicsUnit.Pixel);

    /// <summary>
    /// Measures every row and sizes the window to the widest, so nothing can be
    /// clipped. This is the whole reason for not using a ContextMenuStrip.
    /// </summary>
    private Size Measure()
    {
        using var g = CreateGraphics();
        using var nameFont = NameFont();
        using var subFont = SubFont();
        using var hotkeyFont = HotkeyFont();
        using var commandFont = CommandFont();

        int widest = S(MinWidth);
        int height = S(PadY) * 2;

        foreach (var entry in _entries)
        {
            switch (entry)
            {
                case ProfileEntry profile:
                {
                    height += S(ProfileRowH);
                    string subtitle = profile.UnavailableReason ?? profile.Subtitle;
                    int name = (int)Math.Ceiling(g.MeasureString(profile.Profile.Name, nameFont).Width);
                    int sub = (int)Math.Ceiling(g.MeasureString(subtitle, subFont).Width);
                    int hotkey = string.IsNullOrWhiteSpace(profile.Profile.Hotkey)
                        ? 0
                        : (int)Math.Ceiling(g.MeasureString(profile.Profile.Hotkey, hotkeyFont).Width) + S(18);
                    int textLeft = S(DotDiameter) + S(DotGap);
                    int actions = profile.Actions.Count * S(ActionZoneW);
                    widest = Math.Max(widest, S(PadX) + textLeft + Math.Max(name, sub) + hotkey + actions + S(PadX));

                    // A row with glyphs can become a rename field or a confirmation
                    // without the popup changing size — that is what the equal
                    // heights are for — so it is measured for the widest of the three
                    // forms, not only the one on screen. The confirmation is the wide
                    // one, and it has to fit before it is asked for.
                    if (profile.Actions.Count > 0)
                    {
                        int question = Math.Max(name + S(PromptTitleOverhead), S(PromptDetailW));
                        widest = Math.Max(widest,
                            S(PadX) + textLeft + question + S(PromptTextGap) +
                            PromptButtonsWidth(g, commandFont) + S(PadX));
                    }
                    break;
                }
                case StatusEntry status:
                {
                    height += StatusEntryHeight(status);
                    int labelCol = LabelColumnWidth(g, subFont, status);
                    int widestValue = 0;
                    foreach (var (_, value) in status.Rows)
                        widestValue = Math.Max(widestValue, (int)Math.Ceiling(g.MeasureString(value, subFont).Width));
                    widest = Math.Max(widest, S(PadX) + labelCol + S(8) + widestValue + S(PadX));
                    break;
                }
                case CommandEntry command:
                {
                    height += S(CommandRowH);
                    int text = (int)Math.Ceiling(g.MeasureString(command.Text, commandFont).Width);
                    int detail = string.IsNullOrWhiteSpace(command.Detail)
                        ? 0
                        : (int)Math.Ceiling(g.MeasureString(command.Detail, subFont).Width) + S(18);
                    int checkbox = command.Checked.HasValue ? S(27) : 0;
                    widest = Math.Max(widest, S(PadX) + S(6) + checkbox + text + detail + S(PadX));
                    break;
                }
                case EditEntry edit:
                {
                    height += EditEntryHeight();

                    // Two claims on the width: the field itself, and the hint line
                    // under it, which carries the title on the left and the keys that
                    // work on the right. Both are comfortably inside what the profile
                    // row above already reserved for a confirmation, so a rename never
                    // moves anything — but measured rather than assumed, because a
                    // longer title should ellipsise by choice and not by accident.
                    int title = (int)Math.Ceiling(g.MeasureString(edit.Title, subFont).Width);
                    int keys = (int)Math.Ceiling(g.MeasureString(EditKeysHint, subFont).Width);
                    widest = Math.Max(widest,
                        S(EditCardLeft) + Math.Max(S(EditFieldMinW),
                                                   title + S(PromptTextGap) + keys) + S(PadX));
                    break;
                }
                case PromptEntry prompt:
                {
                    height += PromptEntryHeight();

                    // Measured the same way the profile row above reserved for it, so
                    // that the reservation is what this asks for and the swap is free.
                    // If a question ever outgrows the reserve this still sizes to fit
                    // rather than clipping — it just costs the resize back.
                    int question = Math.Max(
                        (int)Math.Ceiling(g.MeasureString(prompt.Title, commandFont).Width),
                        (int)Math.Ceiling(g.MeasureString(prompt.Detail, subFont).Width));

                    widest = Math.Max(widest,
                        S(PadX) + S(DotDiameter) + S(DotGap) + question + S(PromptTextGap) +
                        PromptButtonsWidth(g, commandFont, prompt.ConfirmText) + S(PadX));
                    break;
                }
                case HeadingEntry heading:
                {
                    height += S(HeadingH);
                    using var headingFont = HeadingFont();
                    int hint = string.IsNullOrWhiteSpace(heading.Hint)
                        ? 0
                        : (int)Math.Ceiling(g.MeasureString(heading.Hint, subFont).Width) + S(18);
                    widest = Math.Max(widest,
                        S(PadX) + S(6) + (int)Math.Ceiling(g.MeasureString(heading.Text, headingFont).Width) +
                        hint + S(PadX));
                    break;
                }
                default:
                    height += S(SeparatorH);
                    break;
            }
        }

        return new Size(Math.Min(widest, S(MaxWidth)), height);
    }

    private void PlaceNear(Point anchor)
    {
        _anchor = anchor;
        Location = PlacementFor(anchor, Size);
    }

    /// <summary>Where a popup of this size belongs. Separate from
    /// <see cref="PlaceNear"/> so a rebuild can work out its new position before
    /// resizing, and then apply both in a single move.</summary>
    private Point PlacementFor(Point anchor, Size size)
    {
        // Anchored to the pointer and clamped to the working area, which puts it in
        // the right place whichever edge the taskbar lives on without special-casing.
        var screen = Screen.FromPoint(anchor).WorkingArea;
        int x = Math.Min(Math.Max(anchor.X - size.Width / 2, screen.Left + S(8)), screen.Right - size.Width - S(8));
        int y = anchor.Y - size.Height - S(12);
        if (y < screen.Top + S(8)) y = Math.Min(anchor.Y + S(12), screen.Bottom - size.Height - S(8));
        return new Point(x, y);
    }

    /// <summary>The field cannot take focus before the window exists, and the very
    /// first menu drawn can already contain one — capture lands straight on the
    /// naming row.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SyncEditor();
        SyncPromptFocus();
    }

    protected override void OnPaint(PaintEventArgs e) => Render(e.Graphics);

    /// <summary>Paints the popup. Separated from OnPaint so the screenshot diagnostic
    /// can render it to a bitmap without showing a window.</summary>
    internal void Render(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Color.FromArgb(21, 19, 17)))
            g.FillRectangle(bg, new Rectangle(Point.Empty, Size));
        using (var edge = new Pen(Color.FromArgb(70, 62, 48)))
            g.DrawPath(edge, RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), S(10)));

        _rows.Clear();
        for (int i = 0; i < _entries.Count; i++) _rows.Add(Rectangle.Empty);
        _zones.Clear();
        _buttons.Clear();

        int y = ContentTop();

        foreach (int i in VisualOrder())
        {
            var entry = _entries[i];
            int h = EntryHeight(entry);

            var row = new Rectangle(0, y, Width, h);
            _rows[i] = row;

            if (_dragging && i == _dragEntry)
            {
                // The row being dragged is drawn last, floating, so what belongs in
                // its place in the list is the hole it will drop into.
                DrawDropSlot(g, row);
            }
            else
            {
                switch (entry)
                {
                    case ProfileEntry profile: DrawProfileRow(g, row, i, profile, i == _hot); break;
                    case StatusEntry status: DrawStatusRow(g, row, status); break;
                    case EditEntry edit: DrawEditRow(g, row, edit); break;
                    case PromptEntry prompt: DrawPromptRow(g, row, i, prompt); break;
                    case CommandEntry command: DrawCommandRow(g, row, command, i == _hot); break;
                    case HeadingEntry heading: DrawHeading(g, row, heading); break;
                    default: DrawSeparator(g, row); break;
                }
            }

            y += h;
        }

        if (_dragging && _dragEntry >= 0 && _dragEntry < _entries.Count &&
            _entries[_dragEntry] is ProfileEntry dragged)
        {
            int h = S(ProfileRowH);
            int top = Math.Clamp(_dragY - _dragGrabDy, ContentTop(),
                                 Math.Max(ContentTop(), Height - S(PadY) - h));
            DrawFloatingRow(g, new Rectangle(0, top, Width, h), dragged);
        }
    }

    /// <summary>
    /// How many rows the profile list is showing.
    ///
    /// <para>Counts the inline forms alongside the profile rows because each one
    /// stands in for exactly the row it replaced — which is what stops opening a
    /// rename or a confirmation, or backing out of one, from reading as the list
    /// having changed. What it does catch is the two things that really do change
    /// the list: a profile added, and a profile deleted.</para>
    /// </summary>
    private int ListRowCount() =>
        _entries.Count(e => e is ProfileEntry or EditEntry or PromptEntry);

    /// <summary>
    /// Where the first row starts.
    ///
    /// <para>Rows are laid out from the bottom of the window up, not from the top
    /// down, because the window itself no longer shrinks — see <see cref="_floor"/>.
    /// A menu that has lost a row keeps its footprint and spends the difference as
    /// space above the first row, which leaves every row below the change exactly
    /// where it was. The pointer is at the bottom edge, so that is the half worth
    /// holding still; it is the same reasoning that used to re-anchor the window on
    /// every rebuild, now that the window does not move.</para>
    /// </summary>
    private int ContentTop() => Math.Max(S(PadY), Height - S(PadY) - ContentHeight());

    private int ContentHeight()
    {
        int height = 0;
        foreach (var entry in _entries) height += EntryHeight(entry);
        return height;
    }

    private int EntryHeight(Entry entry) => entry switch
    {
        ProfileEntry => S(ProfileRowH),
        StatusEntry status => StatusEntryHeight(status),
        EditEntry => EditEntryHeight(),
        PromptEntry => PromptEntryHeight(),
        CommandEntry => S(CommandRowH),
        HeadingEntry => S(HeadingH),
        _ => S(SeparatorH)
    };

    // Both inline forms are the row they replace, exactly. Opening or closing one is
    // then a repaint and nothing else — no SetWindowPos, no region, no reallocated
    // surface. See the constants above.
    private int EditEntryHeight() => S(ProfileRowH);

    private int PromptEntryHeight() => S(ProfileRowH);

    private int ButtonWidth(Graphics g, Font font, string text) =>
        Math.Max(S(PromptButtonMinW),
                 (int)Math.Ceiling(g.MeasureString(text, font).Width) + S(PromptButtonPad));

    /// <summary>
    /// Width of a prompt's Cancel/confirm pair.
    ///
    /// <para>Called with no verb by <see cref="Measure"/> when a profile row reserves
    /// room for a question it has not been asked yet, and answers for the longest
    /// verb any row action can confirm — so the reservation covers whichever one is
    /// clicked.</para>
    /// </summary>
    private int PromptButtonsWidth(Graphics g, Font font, string? confirm = null) =>
        ButtonWidth(g, font, "Cancel") + S(PromptButtonGap) + ButtonWidth(g, font, confirm ?? "Update");

    /// <summary>
    /// The order entries are painted in: their own, except mid-drag, where the
    /// profile block is painted as it would be if the drag were dropped now.
    ///
    /// <para>Rearranging the drawing rather than <see cref="_entries"/> itself is
    /// what makes the drag free: nothing is written, no action has run, and letting
    /// go outside the list or pressing Escape leaves the saved order exactly as it
    /// was. It also keeps every index in this class meaning one thing — a position
    /// in <see cref="_entries"/> — instead of two that have to be translated between
    /// on every hit test.</para>
    /// </summary>
    private List<int> VisualOrder()
    {
        var order = new List<int>(_entries.Count);
        for (int i = 0; i < _entries.Count; i++) order.Add(i);

        if (!_dragging || _dragEntry < 0 || _dropIndex < 0) return order;

        var block = ProfileBlock();
        int at = block.IndexOf(_dragEntry);
        if (at < 0) return order;

        var shuffled = new List<int>(block);
        shuffled.RemoveAt(at);
        shuffled.Insert(Math.Clamp(_dropIndex, 0, shuffled.Count), _dragEntry);

        // The block occupies a contiguous run of positions, so rewriting those
        // positions in place is the whole reorder.
        for (int k = 0; k < block.Count; k++) order[block[k]] = shuffled[k];
        return order;
    }

    private int StatusEntryHeight(StatusEntry entry) =>
        S(StatusPadY) * 2 + Math.Max(1, entry.Rows.Count) * S(StatusRowH);

    /// <summary>Widest label in this status block, so labels form one straight
    /// column regardless of how many rows the caller collapsed this to (1-4).</summary>
    private static int LabelColumnWidth(Graphics g, Font labelFont, StatusEntry entry)
    {
        int widest = 0;
        foreach (var (label, _) in entry.Rows)
            widest = Math.Max(widest, (int)Math.Ceiling(g.MeasureString(label, labelFont).Width));
        return widest;
    }

    /// <param name="floating">True for the copy drawn under the pointer during a
    /// drag. It paints its own lifted background, has no hover state, and registers
    /// no hit zones — its glyphs would sit over the ones still in the list.</param>
    private void DrawProfileRow(Graphics g, Rectangle row, int index, ProfileEntry entry,
                                bool hot, bool floating = false)
    {
        if (!floating && (hot || _hotZone.Row == index)) FillHot(g, row);

        bool on = entry.Enabled || floating;

        // Fixed left inset for the dot regardless of IsLive, so the name column
        // never shifts when the live profile changes — jitter there would be more
        // distracting than the dot itself.
        int textLeft = S(PadX) + S(DotDiameter) + S(DotGap);

        if (entry.IsLive || entry.IsChosen)
        {
            int d = S(DotDiameter);
            var dotRect = new Rectangle(
                S(PadX), row.Y + S(ProfilePadTop) + (S(ProfileNameH) - d) / 2, d, d);

            if (entry.IsLive)
            {
                using var dot = new SolidBrush(UiTheme.Gold);
                g.FillEllipse(dot, dotRect);
            }
            else
            {
                // Hollow, and dimmer. Filled means "this is what you are on"; this
                // one only means "this is what you last asked for". The ring is
                // inset by half its stroke so it stays inside the same 6px the
                // filled dot occupies and the name column does not shift.
                float stroke = Math.Max(1f, d * 0.28f);
                using var ring = new Pen(UiTheme.GoldDim, stroke);
                g.DrawEllipse(ring, new RectangleF(
                    dotRect.X + stroke / 2, dotRect.Y + stroke / 2,
                    dotRect.Width - stroke, dotRect.Height - stroke));
            }
        }

        using var trim = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        string hotkey = entry.Profile.Hotkey;
        using var hotkeyFont = HotkeyFont();
        int hotkeyWidth = string.IsNullOrWhiteSpace(hotkey)
            ? 0
            : (int)Math.Ceiling(g.MeasureString(hotkey, hotkeyFont).Width) + S(18);

        // Reserved even while floating, so the name does not re-wrap or un-ellipsis
        // the instant a row is picked up.
        int actionsWidth = entry.Actions.Count * S(ActionZoneW);

        int textWidth = Math.Max(S(40), Width - textLeft - hotkeyWidth - actionsWidth - S(PadX));

        using (var nameFont = NameFont())
        using (var ink = new SolidBrush(on ? UiTheme.Text : UiTheme.Muted))
        {
            g.DrawString(entry.Profile.Name, nameFont, ink,
                new RectangleF(textLeft, row.Y + S(ProfilePadTop), textWidth, S(ProfileNameH)), trim);
        }

        // While the pointer is on one of the glyphs, the subtitle says what that
        // glyph does. The alternative to borrowing this line was three unlabelled
        // marks and a tooltip the popup has no way to show.
        var hovered = !floating && _hotZone.Row == index && _hotZone.Slot >= 0 &&
                      _hotZone.Slot < entry.Actions.Count
            ? entry.Actions[_hotZone.Slot]
            : null;

        string subtitle = hovered?.Label ?? entry.UnavailableReason ?? entry.Subtitle;
        using (var subFont = SubFont())
        using (var subInk = new SolidBrush(
                   hovered == null ? UiTheme.Muted : hovered.Danger ? UiTheme.Danger : UiTheme.Gold))
        {
            g.DrawString(subtitle, subFont, subInk,
                new RectangleF(textLeft, row.Y + S(ProfilePadTop) + S(ProfileNameH), textWidth, S(ProfileSubH)), trim);
        }

        if (hotkeyWidth > 0)
        {
            using var hotkeyInk = new SolidBrush(Color.FromArgb(on ? 160 : 90, UiTheme.Muted));
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(hotkey, hotkeyFont, hotkeyInk,
                new RectangleF(Width - hotkeyWidth - actionsWidth, row.Y, hotkeyWidth - S(PadX), row.Height), right);
        }

        if (!floating && entry.Actions.Count > 0) DrawRowActions(g, row, index, entry);
    }

    /// <summary>
    /// The rename / replace / delete glyphs at the right of a profile row, and their
    /// hit zones.
    ///
    /// <para>Drawn always rather than on hover: these are now the only way to rename
    /// or delete a profile, and an affordance that appears once you are already
    /// pointing at it is one nobody finds. What hover adds is the word — see the
    /// subtitle swap in <see cref="DrawProfileRow"/>.</para>
    /// </summary>
    private void DrawRowActions(Graphics g, Rectangle row, int index, ProfileEntry entry)
    {
        int w = S(ActionZoneW);
        int right = Width - S(PadX);
        int last = entry.Actions.Count - 1;

        for (int slot = 0; slot <= last; slot++)
        {
            var action = entry.Actions[slot];
            int left = right - (entry.Actions.Count - slot) * w;

            // The rightmost zone runs on to the edge of the popup so the corner is a
            // target too; the rest stop at their own width.
            var zone = new Rectangle(left, row.Y, slot == last ? w + S(PadX) : w, row.Height);
            _zones.Add((index, slot, zone, action));

            bool on = _hotZone == (index, slot);
            var colour = on
                ? action.Danger ? UiTheme.Danger : UiTheme.Gold
                : UiTheme.Muted;

            int size = S(ActionGlyph);
            DrawGlyph(g, action.Glyph,
                new Rectangle(left + (w - size) / 2, row.Y + (row.Height - size) / 2, size, size), colour);
        }
    }

    /// <summary>
    /// Draws one row-action mark from primitives rather than setting it in Segoe MDL2
    /// or Fluent Icons.
    ///
    /// <para>An icon font is hinted for WinUI's metrics and sized off a text
    /// baseline; this popup paints at a DPI multiplier of its own and positions the
    /// glyph in a box it computed, so the two disagree by a pixel or two at exactly
    /// the sizes used here — and disagree differently per machine, which is the kind
    /// of thing <c>--screenshot-menu</c> exists to catch and would then have to keep
    /// catching. Three shapes this simple are cheaper drawn.</para>
    /// </summary>
    private void DrawGlyph(Graphics g, RowGlyph glyph, Rectangle box, Color colour)
    {
        float x = box.X, y = box.Y, w = box.Width, h = box.Height;
        float stroke = Math.Max(1.2f, w * 0.11f);

        using var pen = new Pen(colour, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var fill = new SolidBrush(colour);

        switch (glyph)
        {
            // A pencil lying at 45 degrees over the line it is writing.
            //
            // The shaft is one stroke as wide as the pencil rather than an outlined
            // quadrilateral: at 13px the pencil is four pixels across, and two pen
            // lines that close together are a smudge, not a shape. The tip is the one
            // filled piece, which is what makes it read as a pencil rather than a
            // slash.
            case RowGlyph.Rename:
            {
                // Unit vectors along the pencil and across it. Everything below is
                // placed in those terms so the whole glyph rotates as one thing if
                // the angle is ever changed.
                const float ax = 0.7071f, ay = -0.7071f;   // along, pointing up-right
                const float nx = 0.7071f, ny = 0.7071f;    // across

                float tipX = x + w * 0.15f, tipY = y + h * 0.80f;
                float halfW = w * 0.13f;

                PointF At(float along, float across) => new(
                    tipX + (ax * along + nx * across) * w,
                    tipY + (ay * along + ny * across) * h);

                // Shaft, from the base of the tip to the blunt end.
                using (var shaft = new Pen(colour, halfW * 2f)
                       {
                           StartCap = LineCap.Flat,
                           EndCap = LineCap.Round
                       })
                {
                    g.DrawLine(shaft, At(0.21f, 0), At(0.80f, 0));
                }

                g.FillPolygon(fill, new[] { At(0, 0), At(0.21f, halfW / w), At(0.21f, -halfW / w) });

                // The line being written on. Short and low, clear of the tip.
                g.DrawLine(pen, x + w * 0.34f, y + h * 0.95f, x + w * 0.95f, y + h * 0.95f);
                break;
            }

            // A circling arrow: the profile keeps its name, its hotkey and its place,
            // and takes today's devices.
            case RowGlyph.Update:
            {
                float cx = x + w * 0.48f, cy = y + h * 0.54f;
                float r = w * 0.34f;

                const float start = -55f;
                const float sweep = 285f;
                g.DrawArc(pen, cx - r, cy - r, r * 2f, r * 2f, start, sweep);

                // The head sits on the end of the sweep pointing along the tangent
                // there, which is what separates a circling arrow from a broken ring.
                double end = (start + sweep) * Math.PI / 180.0;
                float px = cx + r * (float)Math.Cos(end);
                float py = cy + r * (float)Math.Sin(end);
                float tx = -(float)Math.Sin(end), ty = (float)Math.Cos(end);
                float hx = -ty, hy = tx;

                float len = w * 0.24f, half = w * 0.15f;
                g.FillPolygon(fill, new[]
                {
                    new PointF(px + tx * len, py + ty * len),
                    new PointF(px - tx * len * 0.35f + hx * half, py - ty * len * 0.35f + hy * half),
                    new PointF(px - tx * len * 0.35f - hx * half, py - ty * len * 0.35f - hy * half)
                });
                break;
            }

            // A bin: handle, lid, tapering body.
            case RowGlyph.Delete:
                g.DrawLine(pen, x + w * 0.38f, y + h * 0.16f, x + w * 0.62f, y + h * 0.16f);
                g.DrawLine(pen, x + w * 0.12f, y + h * 0.30f, x + w * 0.88f, y + h * 0.30f);
                g.DrawLines(pen, new[]
                {
                    new PointF(x + w * 0.22f, y + h * 0.30f),
                    new PointF(x + w * 0.29f, y + h * 0.88f),
                    new PointF(x + w * 0.71f, y + h * 0.88f),
                    new PointF(x + w * 0.78f, y + h * 0.30f)
                });
                break;
        }
    }

    /// <summary>
    /// The gap the dragged row will land in.
    ///
    /// <para>Outlined and recessed rather than filled: it has to read as an absence,
    /// or the eye counts it as a second copy of the row floating just above it.</para>
    /// </summary>
    private void DrawDropSlot(Graphics g, Rectangle row)
    {
        using var path = RoundedPath(
            new Rectangle(S(4), row.Y + S(2), Width - S(8), row.Height - S(4)), S(6));

        using (var fill = new SolidBrush(Color.FromArgb(28, 25, 20)))
            g.FillPath(fill, path);

        using var edge = new Pen(Color.FromArgb(135, UiTheme.Gold)) { DashStyle = DashStyle.Dash };
        g.DrawPath(edge, path);
    }

    /// <summary>
    /// The row under the pointer while it is being dragged. Painted after everything
    /// else so it sits over its neighbours, on its own filled and bordered card so it
    /// reads as lifted off the list rather than as a row that has lost its place in
    /// one.
    /// </summary>
    private void DrawFloatingRow(Graphics g, Rectangle row, ProfileEntry entry)
    {
        // A shadow, so the card sits over the rows it is crossing rather than in
        // among them. Two offset passes rather than a blur: GDI+ has no cheap one,
        // and at this size two translucent edges read as softness anyway.
        for (int d = 3; d >= 1; d--)
        {
            using var shadow = new Pen(Color.FromArgb(38 - d * 8, 0, 0, 0), 2f);
            using var below = RoundedPath(
                new Rectangle(S(4) + d, row.Y + d * 2, Width - S(8) - d * 2, row.Height), S(6));
            g.DrawPath(shadow, below);
        }

        using (var path = RoundedPath(new Rectangle(S(4), row.Y, Width - S(8), row.Height), S(6)))
        {
            using var fill = new SolidBrush(UiTheme.CardActive);
            g.FillPath(fill, path);
            using var edge = new Pen(UiTheme.Gold);
            g.DrawPath(edge, path);
        }

        DrawProfileRow(g, row, -1, entry, hot: false, floating: true);
    }

    private void DrawStatusRow(Graphics g, Rectangle row, StatusEntry entry)
    {
        // Never highlighted — this block is not clickable, it is a readout.
        using var font = SubFont();
        using var labelInk = new SolidBrush(UiTheme.Muted);
        using var valueInk = new SolidBrush(UiTheme.Text);
        using var trim = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        int labelCol = LabelColumnWidth(g, font, entry);
        int valueLeft = S(PadX) + labelCol + S(8);

        int y = row.Y + S(StatusPadY);
        foreach (var (label, value) in entry.Rows)
        {
            g.DrawString(label, font, labelInk,
                new RectangleF(S(PadX), y, labelCol, S(StatusRowH)), trim);
            g.DrawString(value, font, valueInk,
                new RectangleF(valueLeft, y, Width - valueLeft - S(PadX), S(StatusRowH)), trim);
            y += S(StatusRowH);
        }
    }

    private void DrawCommandRow(Graphics g, Rectangle row, CommandEntry entry, bool hot)
    {
        if (hot && entry.Enabled) FillHot(g, row);

        int textLeft = S(PadX) + S(6);

        if (entry.Checked is { } isChecked)
        {
            int box = S(13);
            var b = new Rectangle(S(PadX) + S(4), row.Y + (row.Height - box) / 2, box, box);

            using (var pen = new Pen(isChecked ? UiTheme.Gold : UiTheme.Line))
                g.DrawRectangle(pen, b);

            if (isChecked)
            {
                using var tick = new Pen(UiTheme.Gold, Math.Max(1.4f, box * 0.16f))
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                g.DrawLines(tick, new[]
                {
                    new PointF(b.Left + box * 0.22f, b.Top + box * 0.52f),
                    new PointF(b.Left + box * 0.42f, b.Top + box * 0.74f),
                    new PointF(b.Left + box * 0.80f, b.Top + box * 0.26f)
                });
            }

            textLeft = b.Right + S(10);
        }

        using var font = CommandFont();
        using var ink = new SolidBrush(entry.Enabled
            ? entry.Emphasis ? UiTheme.Gold : UiTheme.Text
            : UiTheme.Muted);
        using var left = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        g.DrawString(entry.Text, font, ink,
            new RectangleF(textLeft, row.Y, Width - textLeft - S(PadX), row.Height), left);

        if (!string.IsNullOrWhiteSpace(entry.Detail))
        {
            using var subFont = SubFont();
            using var subInk = new SolidBrush(UiTheme.Muted);
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(entry.Detail, subFont, subInk,
                new RectangleF(0, row.Y, Width - S(PadX), row.Height), right);
        }
    }

    /// <summary>
    /// The name field, its label, and the line under it that is either the reason
    /// the name cannot be used or a reminder of the two keys that end the edit.
    ///
    /// <para>The text is painted here as well as being in the TextBox on top of it.
    /// The control only exists on a popup that has been shown, and
    /// <c>--screenshot-menu --state rename</c> renders one that has not — without
    /// this the diagnostic would photograph an empty box.</para>
    /// </summary>
    /// <summary>The keys that work while the field has focus. Right-aligned on the
    /// hint line and dropped whenever the title or an error needs the room — a
    /// reminder is worth less than the thing it is sharing the line with.</summary>
    private const string EditKeysHint = "Enter saves · Esc cancels";

    /// <summary>
    /// A profile row turned into a name field, in the same box the row occupied.
    ///
    /// <para>The field sits over the name line and the title moves to the subtitle
    /// line, where the reason a name cannot be used replaces it as soon as there is
    /// one. That is the whole row: no title above the field, no separate hint below
    /// it, and so no height the row did not already have.</para>
    /// </summary>
    private void DrawEditRow(Graphics g, Rectangle row, EditEntry entry)
    {
        var card = EditCardRect(row);

        using (var path = RoundedPath(card, S(5)))
        {
            using var fill = new SolidBrush(UiTheme.Input);
            g.FillPath(fill, path);

            // Gold, because this field has the keyboard the moment it appears and the
            // border is the only thing on screen that says so.
            using var edge = new Pen(_editError == null ? UiTheme.Gold : UiTheme.Danger);
            g.DrawPath(edge, path);
        }

        using var centreLeft = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        using (var font = NameFont())
        using (var ink = new SolidBrush(UiTheme.Text))
        {
            g.DrawString(_editor?.Text ?? entry.Initial, font, ink,
                new RectangleF(card.X + S(EditTextInset), card.Y,
                               card.Width - S(EditTextInset) * 2, card.Height), centreLeft);
        }

        // Runs from under the field to the bottom of the row rather than having a
        // height of its own, so it absorbs whatever S() rounding left over and the
        // parts cannot add up to more than the row at some scale.
        int hintTop = card.Bottom + S(EditGap);
        var hint = new Rectangle(card.X + S(EditTextInset), hintTop,
                                 Math.Max(S(40), Width - card.X - S(EditTextInset) - S(PadX)),
                                 Math.Max(S(10), row.Bottom - hintTop));

        using var hintFont = SubFont();

        string message = _editError ?? entry.Title;
        int keysW = (int)Math.Ceiling(g.MeasureString(EditKeysHint, hintFont).Width);
        int messageW = (int)Math.Ceiling(g.MeasureString(message, hintFont).Width);

        // Dropped rather than ellipsised when it does not fit: half of "Enter saves"
        // says nothing, where half a title still reads.
        bool showKeys = _editError == null && messageW + S(PromptTextGap) + keysW <= hint.Width;

        if (showKeys)
        {
            using var keysInk = new SolidBrush(Color.FromArgb(150, UiTheme.Muted));
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(EditKeysHint, hintFont, keysInk, hint, right);
        }

        using (var messageInk = new SolidBrush(_editError == null ? UiTheme.Muted : UiTheme.Danger))
        {
            int room = showKeys ? hint.Width - keysW - S(PromptTextGap) : hint.Width;
            g.DrawString(message, hintFont, messageInk,
                new RectangleF(hint.X, hint.Y, room, hint.Height), centreLeft);
        }
    }

    /// <summary>The bordered box the name sits in, which the TextBox is centred
    /// inside. Derived from the row rather than stored, so the two can never disagree
    /// after a resize. Its left edge is set by <see cref="EditCardLeft"/>, which puts
    /// the text inside it in the profile row's name column.</summary>
    private Rectangle EditCardRect(Rectangle row) =>
        new(S(EditCardLeft),
            row.Y + S(EditPadTop),
            Math.Max(S(60), Width - S(EditCardLeft) - S(PadX)),
            S(EditFieldH));

    /// <summary>
    /// A question and two buttons, in place of the row it is about and in the same
    /// box.
    ///
    /// <para>Laid out the way the profile row is: question on the name line,
    /// explanation on the subtitle line, buttons where the three glyphs were. Cancel
    /// sits left of the confirming button and holds the keyboard when the prompt
    /// opens. Enter on a question you have not read must not delete anything — the
    /// property the old dialog had, kept.</para>
    /// </summary>
    private void DrawPromptRow(Graphics g, Rectangle row, int index, PromptEntry entry)
    {
        var card = new Rectangle(S(4), row.Y + S(2), Width - S(8), row.Height - S(4));
        using (var path = RoundedPath(card, S(6)))
        {
            using var fill = new SolidBrush(UiTheme.Card);
            g.FillPath(fill, path);
            using var edge = new Pen(entry.Danger ? Color.FromArgb(120, UiTheme.Danger) : UiTheme.Line);
            g.DrawPath(edge, path);
        }

        using var commandFont = CommandFont();
        int confirmW = ButtonWidth(g, commandFont, entry.ConfirmText);
        int cancelW = ButtonWidth(g, commandFont, "Cancel");

        // Centred on the row rather than given a line of their own: the row is two
        // lines of text and one decision, and the decision belongs to both lines.
        int buttonY = row.Y + (row.Height - S(PromptButtonH)) / 2;
        int right = Width - S(PadX);

        var confirmRect = new Rectangle(right - confirmW, buttonY, confirmW, S(PromptButtonH));
        var cancelRect = new Rectangle(confirmRect.X - S(PromptButtonGap) - cancelW, buttonY,
                                       cancelW, S(PromptButtonH));

        _buttons.Add((index, 0, cancelRect, entry.Cancel));
        _buttons.Add((index, 1, confirmRect, entry.Confirm));

        int textLeft = S(PadX) + S(DotDiameter) + S(DotGap);
        int textWidth = Math.Max(S(40), cancelRect.X - S(PromptTextGap) - textLeft);

        using var trim = new StringFormat
        {
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        using (var titleInk = new SolidBrush(entry.Danger ? UiTheme.Danger : UiTheme.Text))
        {
            g.DrawString(entry.Title, commandFont, titleInk,
                new RectangleF(textLeft, row.Y + S(ProfilePadTop), textWidth, S(ProfileNameH)), trim);
        }

        using (var detailFont = SubFont())
        using (var detailInk = new SolidBrush(UiTheme.Muted))
        {
            g.DrawString(entry.Detail, detailFont, detailInk,
                new RectangleF(textLeft, row.Y + S(ProfilePadTop) + S(ProfileNameH),
                               textWidth, S(ProfileSubH)), trim);
        }

        DrawButton(g, commandFont, cancelRect, "Cancel", _hotButton == (index, 0),
                   primary: false, danger: false);
        DrawButton(g, commandFont, confirmRect, entry.ConfirmText, _hotButton == (index, 1),
                   primary: true, danger: entry.Danger);
    }

    private void DrawButton(Graphics g, Font font, Rectangle r, string text, bool hot, bool primary, bool danger)
    {
        var accent = danger ? UiTheme.Danger : UiTheme.Gold;

        using (var path = RoundedPath(r, S(5)))
        {
            // Filled only while it has the pointer or the keyboard, so the two
            // buttons read as equals until one is chosen — which is what stops the
            // eye landing on "Delete" as the thing to press.
            using var fill = new SolidBrush(hot ? (primary ? accent : UiTheme.CardHover) : UiTheme.Panel);
            g.FillPath(fill, path);

            using var edge = new Pen(primary ? accent : UiTheme.Line);
            g.DrawPath(edge, path);
        }

        using var ink = new SolidBrush(hot && primary ? UiTheme.Ink : primary ? accent : UiTheme.Text);
        using var centre = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, ink, r, centre);
    }

    private void DrawHeading(Graphics g, Rectangle row, HeadingEntry entry)
    {
        using var font = HeadingFont();
        using var ink = new SolidBrush(UiTheme.Muted);
        using var left = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(entry.Text, font, ink,
            new RectangleF(S(PadX) + S(6), row.Y, Width - S(PadX) * 2, row.Height), left);

        if (string.IsNullOrWhiteSpace(entry.Hint)) return;

        using var hintFont = SubFont();
        using var hintInk = new SolidBrush(Color.FromArgb(150, UiTheme.Muted));
        using var right = new StringFormat
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(entry.Hint, hintFont, hintInk,
            new RectangleF(0, row.Y, Width - S(PadX), row.Height), right);
    }

    private void DrawSeparator(Graphics g, Rectangle row)
    {
        using var pen = new Pen(Color.FromArgb(45, 40, 32));
        int y = row.Y + row.Height / 2;
        g.DrawLine(pen, S(PadX), y, Width - S(PadX), y);
    }

    private void FillHot(Graphics g, Rectangle row)
    {
        using var brush = new SolidBrush(UiTheme.CardHover);
        g.FillRectangle(brush, new Rectangle(S(4), row.Y + S(2), Width - S(8), row.Height - S(4)));
    }

    // ------------------------------------------------------------- interaction

    /// <summary>
    /// Records a press on a profile row without acting on it. Whether this was a
    /// click or the start of a drag is not known until the pointer either moves
    /// <see cref="DragThreshold"/> or comes back up.
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        // Cleared unconditionally. A press that ends outside the window never reaches
        // OnMouseUp, so without this a stale row could be left armed and the next
        // press-and-move anywhere in the menu would pick that one up instead.
        _pressEntry = -1;

        if (e.Button == MouseButtons.Left && _reorder != null && !Blocking &&
            HitZone(e.Location).Row < 0)
        {
            int row = RowAt(e.Location);

            // RowAt rather than HitTest: a profile whose devices are unplugged cannot
            // be switched to, but there is no reason it cannot be moved.
            if (row >= 0 && _entries[row] is ProfileEntry && ProfileBlock().Count > 1)
            {
                _pressEntry = row;
                _pressPoint = e.Location;
            }
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_pressEntry >= 0 && (e.Button & MouseButtons.Left) != 0)
        {
            if (!_dragging &&
                (Math.Abs(e.Y - _pressPoint.Y) >= S(DragThreshold) ||
                 Math.Abs(e.X - _pressPoint.X) >= S(DragThreshold)))
            {
                BeginDrag();
            }

            if (_dragging)
            {
                _dragY = e.Y;
                UpdateDropIndex();
                Invalidate();
                return;
            }
        }

        var button = HitButton(e.Location);
        if (button.Row >= 0)
        {
            if (button != _hotButton) { _hotButton = button; Cursor = Cursors.Hand; Invalidate(); }
            base.OnMouseMove(e);
            return;
        }

        int hit = Blocking ? -1 : HitTest(e.Location);
        (int Row, int Slot) zone = Blocking ? (-1, -1) : HitZone(e.Location);
        if (hit != _hot || zone != _hotZone)
        {
            _hot = hit;
            _hotZone = zone;
            Cursor = hit >= 0 || zone.Row >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        // A drag holds the mouse captured, so it keeps running past the edge of the
        // popup — which is exactly where you end up when dragging a row to the top of
        // a list that is anchored above the pointer.
        if (_dragging) return;

        if (_hot != -1 || _hotZone.Row != -1) { _hot = -1; _hotZone = (-1, -1); Invalidate(); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragging)
        {
            CommitDrag();
            return;
        }

        _pressEntry = -1;

        // Left button only. Rows and glyphs both do something, and a stray right
        // click landing on a profile row and switching the machine's audio would be
        // an expensive way to find that out.
        if (e.Button != MouseButtons.Left) { base.OnMouseUp(e); return; }

        var button = HitButton(e.Location);
        if (button.Row >= 0)
        {
            Fire(keepOpen: true, ButtonAction(button));
            return;
        }

        if (Blocking)
        {
            // Clicking away answers the row rather than doing nothing. A name is
            // taken — that is what clicking out of a field means everywhere else —
            // unless it is one that cannot be used, and a question is dropped,
            // because clicking past a question is not an answer to it.
            if (_editing != null) { if (_editError == null) CommitEdit(); else CancelEdit(); }
            else CancelPrompt();
            return;
        }

        var zone = HitZone(e.Location);
        if (zone.Row >= 0)
        {
            // Kept open: what all three glyphs do is put a question on their own
            // row, and a question on a menu that has just closed is one nobody can
            // answer. They were dialogs, and closing first was what a dialog needed.
            Fire(keepOpen: true, ZoneAction(zone)?.Invoke);
            return;
        }

        int hit = HitTest(e.Location);
        if (hit >= 0)
        {
            Fire(_entries[hit].KeepOpen, _entries[hit].Invoke);
            return;
        }

        base.OnMouseUp(e);
    }

    private void BeginDrag()
    {
        var block = ProfileBlock();
        int at = block.IndexOf(_pressEntry);
        if (at < 0) { _pressEntry = -1; return; }

        _dragging = true;
        _dragEntry = _pressEntry;
        _blockTop = _rows[block[0]].Y;
        _dragGrabDy = _pressPoint.Y - _rows[_dragEntry].Y;
        _dropIndex = at;

        // Hover state belongs to a pointer that is choosing something. This one has
        // already chosen and is carrying it.
        _hot = -1;
        _hotZone = (-1, -1);
        Cursor = Cursors.SizeNS;

        // Without capture the drag dies the moment the pointer leaves the popup,
        // which for a row near the top edge is immediately.
        Capture = true;
    }

    private void UpdateDropIndex()
    {
        int count = ProfileBlock().Count;
        if (count == 0) return;

        int h = S(ProfileRowH);

        // Measured from the floating row's own top edge, not the pointer: the row is
        // what is being placed, so it should land where it looks like it will land
        // whichever part of it was grabbed. The half-row bias makes the swap happen
        // when it has travelled past the midpoint rather than fully clear.
        _dropIndex = Math.Clamp((_dragY - _dragGrabDy - _blockTop + h / 2) / h, 0, count - 1);
    }

    private void CommitDrag()
    {
        var block = ProfileBlock();
        int from = block.IndexOf(_dragEntry);
        int to = Math.Clamp(_dropIndex, 0, Math.Max(0, block.Count - 1));
        var profile = (_entries[_dragEntry] as ProfileEntry)?.Profile;
        var reorder = _reorder;

        EndDrag();

        if (profile != null && reorder != null && from >= 0 && to != from)
        {
            // Saved on drop, with no separate confirmation: the drop is the
            // confirmation, and a reorder you have to remember to save afterwards is
            // one you will lose.
            reorder(profile, to);
            RefreshInPlace();
        }
        else
        {
            Invalidate();
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragEntry = -1;
        _dropIndex = -1;
        _pressEntry = -1;
        Capture = false;
        Cursor = Cursors.Default;
    }

    /// <summary>
    /// Runs an entry's action, closing first or staying open depending on what the
    /// entry is. Every path that invokes an entry goes through here, so the two
    /// behaviours cannot drift apart between mouse and keyboard.
    /// </summary>
    private void Fire(bool keepOpen, Action? action)
    {
        if (action == null) return;

        if (!keepOpen)
        {
            // Closed before the action runs, not after: an action that opens a dialog
            // would otherwise be behind a TopMost popup that is about to deactivate
            // and close itself anyway.
            Close();
            action();
            return;
        }

        action();
        RefreshInPlace();
    }

    private int HitTest(Point p)
    {
        for (int i = 0; i < _rows.Count && i < _entries.Count; i++)
        {
            if (_rows[i].Contains(p) && _entries[i].Selectable) return i;
        }
        return -1;
    }

    /// <summary>The row under the point whatever its state, for the right-click path
    /// — a profile whose devices are missing is not selectable but still has a
    /// page.</summary>
    private int RowAt(Point p)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Contains(p)) return i;
        }
        return -1;
    }

    /// <summary>The row and slot of the glyph under the point, or (-1, -1).</summary>
    private (int Row, int Slot) HitZone(Point p)
    {
        foreach (var (row, slot, zone, _) in _zones)
        {
            if (zone.Contains(p)) return (row, slot);
        }
        return (-1, -1);
    }

    private (int Row, int Index) HitButton(Point p)
    {
        foreach (var (row, index, zone, _) in _buttons)
        {
            if (zone.Contains(p)) return (row, index);
        }
        return (-1, -1);
    }

    private Action? ButtonAction((int Row, int Index) at)
    {
        if (at.Row < 0) return null;
        foreach (var b in _buttons)
        {
            if (b.Row == at.Row && b.Index == at.Index) return b.Invoke;
        }
        return null;
    }

    private void CancelPrompt()
    {
        int row = _entries.FindIndex(e => e is PromptEntry);
        if (row >= 0) Fire(keepOpen: true, ((PromptEntry)_entries[row]).Cancel);
    }

    private RowAction? ZoneAction((int Row, int Slot) at)
    {
        if (at.Row < 0) return null;
        foreach (var zone in _zones)
        {
            if (zone.Row == at.Row && zone.Slot == at.Slot) return zone.Action;
        }
        return null;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // A name field owns the keyboard while it is open. Only the two keys that
        // end the edit are taken; everything else — arrows, Home, Delete, Ctrl+A —
        // belongs to the text being typed.
        if (_editing != null)
        {
            switch (keyData)
            {
                case Keys.Escape: CancelEdit(); return true;
                case Keys.Enter: CommitEdit(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        int promptRow = _entries.FindIndex(e => e is PromptEntry);
        if (promptRow >= 0)
        {
            var prompt = (PromptEntry)_entries[promptRow];
            switch (keyData)
            {
                case Keys.Escape:
                    Fire(keepOpen: true, prompt.Cancel);
                    return true;

                case Keys.Left:
                case Keys.Right:
                case Keys.Tab:
                    _hotButton = (promptRow, _hotButton.Index == 1 ? 0 : 1);
                    Invalidate();
                    return true;

                case Keys.Enter:
                case Keys.Space:
                    Fire(keepOpen: true, _hotButton.Index == 1 ? prompt.Confirm : prompt.Cancel);
                    return true;

                // Swallowed: the list behind an open question is not navigable, so
                // Up and Down must not move a selection that cannot be acted on.
                case Keys.Up:
                case Keys.Down:
                    return true;
            }
        }

        switch (keyData)
        {
            case Keys.Escape:
                // During a drag this abandons the move rather than closing the menu,
                // the same as every other list you can drag inside. Nothing has been
                // written yet — see VisualOrder — so there is nothing to undo.
                if (_dragging) { EndDrag(); Invalidate(); return true; }
                Close();
                return true;

            case Keys.Down:
                MoveSelection(1);
                return true;

            case Keys.Up:
                MoveSelection(-1);
                return true;

            // The keyboard's drag. Reordering is a pointer gesture now, so without
            // these there would be no way to reorder from the keyboard at all.
            case Keys.Control | Keys.Up:
                return NudgeSelectedProfile(-1);

            case Keys.Control | Keys.Down:
                return NudgeSelectedProfile(1);

            // The keyboard's glyphs, on the two that have a conventional key. Update
            // is left to the pointer: it is the rarest of the three and there is no
            // key anybody would guess.
            case Keys.F2:
                return InvokeRowAction(RowGlyph.Rename);

            case Keys.Delete:
                return InvokeRowAction(RowGlyph.Delete);

            case Keys.Enter:
            case Keys.Space:
                if (_hot >= 0 && _hot < _entries.Count && _entries[_hot].Selectable)
                {
                    Fire(_entries[_hot].KeepOpen, _entries[_hot].Invoke);
                }
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Moves the selected profile one place, and keeps the selection on it
    /// so a second press moves the same profile again rather than whichever one has
    /// taken its place.</summary>
    private bool NudgeSelectedProfile(int delta)
    {
        if (_reorder == null || _hot < 0 || _hot >= _entries.Count) return true;
        if (_entries[_hot] is not ProfileEntry entry) return true;

        var block = ProfileBlock();
        int at = block.IndexOf(_hot);
        int to = at + delta;
        if (at < 0 || to < 0 || to >= block.Count) return true;

        _reorder(entry.Profile, to);
        RefreshInPlace();

        // Entry indices, not profile positions: the block occupies the same run of
        // rows however its contents were rearranged.
        if (to < _rows.Count) { _hot = block[to]; Invalidate(); }
        return true;
    }

    private bool InvokeRowAction(RowGlyph glyph)
    {
        if (_hot < 0 || _hot >= _entries.Count) return true;
        if (_entries[_hot] is not ProfileEntry entry) return true;

        var action = entry.Actions.FirstOrDefault(a => a.Glyph == glyph);
        if (action != null) Fire(keepOpen: true, action.Invoke);
        return true;
    }

    private void MoveSelection(int direction)
    {
        if (_entries.Count == 0 || _dragging) return;
        int i = _hot;
        for (int step = 0; step < _entries.Count; step++)
        {
            i = (i + direction + _entries.Count) % _entries.Count;
            if (_entries[i].Selectable) { _hot = i; Invalidate(); return; }
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeEditor();
        if (ReferenceEquals(_open, this)) _open = null;
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------- painting once

    /// <summary>Suspends and resumes a window's own painting. Everything done to the
    /// window in between is applied without reaching the screen, so a rebuild that
    /// resizes, re-regions, moves and re-parents shows up as one change rather than
    /// four.</summary>
    private const int WM_SETREDRAW = 0x000B;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rect, IntPtr region, uint flags);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOREDRAW = 0x0008;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOCOPYBITS = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y,
                                            int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region,
                                           [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
                                                    int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
