using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace APS;

/// <summary>
/// The application icon, drawn rather than shipped as a resource so it stays a
/// single-file build with no assets beside the exe.
///
/// Shared by the tray and by every window. A window left with the default WinForms
/// icon shows it in the title bar, the taskbar and Alt-Tab, which is the kind of
/// detail that makes an otherwise finished app look unfinished.
///
/// Two variants: the plain speaker, and a lock-badged one the tray shows while a
/// watched profile is being enforced. Both are cached, because the tray swaps
/// between them on every apply and building an icon means an unmanaged handle.
/// </summary>
internal static class AppIcon
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon? _cached;
    private static Icon? _cachedBadged;

    /// <summary>The app icon at 32px. Cached — see <see cref="Create"/> for why
    /// building one is not free.</summary>
    public static Icon Shared => _cached ??= Create(32);

    /// <summary>
    /// The same icon with a lock badge, shown while <see cref="PinEnforcer"/> is
    /// guarding a profile. Cached alongside the plain one so a swap is a field
    /// assignment: the tray swaps on every apply, and an icon built per swap
    /// leaks one unmanaged handle per profile switch.
    /// </summary>
    public static Icon SharedBadged => _cachedBadged ??= Create(32, badged: true);

    public static Icon Create(int size) => Create(size, badged: false);

    /// <summary>
    /// Draws the icon at one size.
    ///
    /// <para>The returned Icon owns its handle. <c>Icon.FromHandle</c> does not —
    /// disposing one built that way leaves the HICON allocated — so the handle
    /// from GetHicon is cloned into a managed icon and destroyed immediately.
    /// Without that every call here leaks a handle for the life of the process.</para>
    /// </summary>
    public static Icon Create(int size, bool badged)
    {
        using var bmp = new Bitmap(size, size);
        float k = size / 32f;

        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float Sc(float v) => v * k;

            using var body = new SolidBrush(Color.FromArgb(25, 22, 18));
            using var gold = new SolidBrush(UiTheme.Gold);

            // A dark badge behind the glyph, matching the app's near-black body colour
            // — it reads as a filled shape at 16px rather than a wireframe. Drawn at
            // full size in both variants: it is the icon's ground, not part of the
            // glyph.
            var panel = new RectangleF(Sc(2), Sc(2), Sc(28), Sc(28));
            using (var panelPath = Rounded(panel, Sc(6)))
            {
                g.FillPath(body, panelPath);
            }

            // The speaker shrinks toward the top-left when badged, which is how
            // every badged icon in Windows works and why. The first attempt kept it
            // full size and put the lock on top, and at 16px the badge's dark halo
            // bit a chunk out of the cone: the icon read as damaged rather than as
            // locked. Making room is the difference between a badge and a blemish.
            float glyph = badged ? 0.78f : 1f;

            // Divided by the glyph scale so the arcs keep their pixel weight after
            // the transform — a 16px arc thinned to 0.78px is the first thing to
            // disappear.
            using var pen = new Pen(UiTheme.Gold, Math.Max(1f, Sc(2f)) / glyph)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            var glyphState = g.Save();
            if (badged) g.ScaleTransform(glyph, glyph);

            // Driver: the block the cone sits in front of.
            var driver = new RectangleF(Sc(7), Sc(12), Sc(7), Sc(8));
            using (var driverPath = Rounded(driver, Sc(1.5f)))
            {
                g.FillPath(gold, driverPath);
            }

            // Cone: a trapezoid flaring from the driver out to the right, the
            // silhouette that says "speaker" at 16px even before the arcs resolve.
            using (var cone = new GraphicsPath())
            {
                cone.AddPolygon(new[]
                {
                    new PointF(Sc(14), Sc(14)),
                    new PointF(Sc(21), Sc(7)),
                    new PointF(Sc(21), Sc(25)),
                    new PointF(Sc(14), Sc(18)),
                });
                g.FillPath(gold, cone);
            }

            // Sound arcs, radiating from the cone's mouth. Two rather than three — a
            // third stops being distinguishable from the others once this is scaled
            // down to a 16px tray icon.
            var arcCenter = new PointF(Sc(19), Sc(16));
            DrawArc(g, pen, arcCenter, Sc(6));
            DrawArc(g, pen, arcCenter, Sc(10));

            g.Restore(glyphState);

            if (badged) DrawLockBadge(g, Sc, body, gold);
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            // Clone, because an Icon built from a handle does not own it. The
            // clone carries its own copy and frees it on Dispose like any other
            // managed icon.
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// A padlock in the lower-right corner, saying the profile is being held.
    ///
    /// <para>Sized for 16px first, where this is a badge roughly seven pixels
    /// across — the state it is meant to communicate is visible at exactly one
    /// size in the tray, and a badge that only resolves at 32px communicates
    /// nothing. So: a dark disc behind it, because the outer sound arc runs
    /// straight through this corner and gold-on-gold would read as a smudge; a
    /// solid body wider than it is tall, which survives being three pixels; and
    /// one thick shackle arc rather than a drawn keyhole, which does not.</para>
    /// </summary>
    private static void DrawLockBadge(Graphics g, Func<float, float> Sc, Brush body, Brush gold)
    {
        // The dark disc is bounded by the panel rather than hanging off it: a badge
        // spilling past the icon's own edge is clipped by the tray at some sizes
        // and not others, which looks like a rendering fault.
        g.FillEllipse(body, Sc(16.5f), Sc(16.5f), Sc(14), Sc(14));

        using var shackle = new Pen(gold, Math.Max(1f, Sc(2.2f)));

        // Shackle: the top half of a circle, drawn before the body so the body
        // covers its ends and the join never shows as two stubs.
        g.DrawArc(shackle, Sc(21f), Sc(20f), Sc(5), Sc(5), 180, 180);

        // Wider than it is tall, because height is what runs out first: at 16px
        // this is about three pixels by two, and a square would read as a dot.
        using var lockBody = Rounded(new RectangleF(Sc(19.5f), Sc(23f), Sc(8), Sc(6)), Sc(1.4f));
        g.FillPath(gold, lockBody);
    }

    private static void DrawArc(Graphics g, Pen pen, PointF center, float radius)
    {
        var box = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        g.DrawArc(pen, box, -40, 80);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
