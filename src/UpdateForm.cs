using System.Text;
using System.Text.RegularExpressions;
using Velopack;

namespace APS;

/// <summary>Lets the user review, download, and install an available update.</summary>
internal sealed class UpdateForm : Form
{
    private readonly UpdateManager _manager;
    private readonly UpdateInfo _update;
    private readonly Func<VelopackAsset, bool> _apply;
    private readonly Button _installButton;
    private readonly ProgressStrip _progress;
    private readonly Label _status;
    private readonly CancellationTokenSource _closing = new();
    private bool _downloaded;

    /// <summary>Converts a size on the app's pixel ramp to the point size this dialog
    /// uses, with Windows' Text size setting applied.</summary>
    private static float Pt(float designPx) => designPx * 0.75f * UiScaling.TextScale;

    public UpdateForm(UpdateManager manager, UpdateInfo update, Func<VelopackAsset, bool> apply)
    {
        _manager = manager;
        _update = update;
        _apply = apply;

        // Unlike the tray menu, this one is laid out by WinForms rather than
        // by hand: it is a plain dialog, and letting AutoScaleMode.Dpi plus auto-sizing
        // panels do the work keeps it right at any display scale. The fonts are in
        // points for the same reason — a point size is already DPI-relative, so the text
        // follows the monitor, and the panels follow the text. Windows' Text size
        // setting is the one thing WinForms does not know about, so that is applied
        // here, to the fonts, and the layout grows around it. It still looks like the
        // rest of the app: same colours, same buttons, same type ramp.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font(UiType.Family, Pt(UiType.Body), FontStyle.Regular, GraphicsUnit.Point);
        BackColor = UiTheme.Bg;
        ForeColor = UiTheme.Text;
        UiTheme.UseDarkTitleBar(this);

        Text = $"Update {AppInfo.Name}";
        Icon = AppIcon.Shared;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        float grow = 1f + (UiScaling.TextScale - 1f) / 2f;
        ClientSize = new Size((int)Math.Round(520 * grow), (int)Math.Round(380 * grow));

        var title = new Label
        {
            Text = $"{AppInfo.Name} {update.TargetFullRelease.Version} is available",
            Font = new Font(UiType.DisplayFamily, Pt(UiType.Subtitle), FontStyle.Bold, GraphicsUnit.Point),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        };

        var current = new Label
        {
            Text = $"You have {AppInfo.Version}.",
            ForeColor = UiTheme.Muted,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12)
        };

        var notesHeading = new Label
        {
            Text = "WHAT'S NEW",
            ForeColor = UiTheme.Muted,
            Font = new Font(UiType.Family, Pt(UiType.Caption), FontStyle.Bold, GraphicsUnit.Point),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        var notes = new TextBox
        {
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = UiTheme.Card,
            ForeColor = UiTheme.Text,
            Text = FormatNotes(update.TargetFullRelease.NotesMarkdown),
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        UiTheme.UseDarkScrollBars(notes);

        // The notes sit in a card with the app's hairline border. A TextBox cannot pad
        // its own text or colour its own border, so the card does both.
        var notesCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiTheme.Card,
            Padding = new Padding(12, 10, 4, 10),
            Margin = new Padding(0, 0, 0, 12)
        };
        notesCard.Paint += (_, e) =>
        {
            using var pen = new Pen(UiTheme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, notesCard.Width - 1, notesCard.Height - 1);
        };
        notesCard.Controls.Add(notes);

        _status = new Label
        {
            Text = "Ready to download",
            ForeColor = UiTheme.Muted,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6)
        };

        _progress = new ProgressStrip
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Height = 6,
            Margin = new Padding(0, 0, 16, 0)
        };

        _installButton = new Button
        {
            Text = "Install and restart",
            Font = new Font(UiType.Family, Pt(UiType.Body), FontStyle.Bold, GraphicsUnit.Point),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(0, 0, 8, 0)
        };
        UiTheme.StyleButton(_installButton, primary: true);
        _installButton.Click += InstallClicked;

        var laterButton = new Button
        {
            Text = "Later",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(0),
            MinimumSize = new Size(88, 0)
        };
        UiTheme.StyleButton(laterButton, primary: false);
        laterButton.Click += (_, _) => Close();

        // Progress takes whatever the buttons leave; the buttons take what their
        // labels need.
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actions.Controls.Add(_progress, 0, 0);
        actions.Controls.Add(_installButton, 1, 0);
        actions.Controls.Add(laterButton, 2, 0);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(20, 18, 20, 18)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(current, 0, 1);
        root.Controls.Add(notesHeading, 0, 2);
        root.Controls.Add(notesCard, 0, 3);
        root.Controls.Add(_status, 0, 4);
        root.Controls.Add(actions, 0, 5);
        Controls.Add(root);
        ResumeLayout(performLayout: true);

        // Otherwise the notes box takes focus and opens with all of its text selected.
        ActiveControl = _installButton;
        AcceptButton = _installButton;
        CancelButton = laterButton;
        FormClosing += (_, _) => _closing.Cancel();
    }

    /// <summary>
    /// Turns the release notes' markdown into plain text worth reading in a text box:
    /// no <c>#</c> or <c>**</c> markers, bullets as bullets, and paragraphs re-flowed.
    /// The notes are written hard-wrapped at 90 columns; shown verbatim, those line
    /// breaks land mid-sentence at any other width.
    /// </summary>
    internal static string FormatNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "A new version is ready to install.";

        var output = new StringBuilder();
        var paragraph = new StringBuilder();

        void Flush()
        {
            if (paragraph.Length == 0) return;
            if (output.Length > 0) output.Append("\r\n\r\n");
            output.Append(paragraph);
            paragraph.Clear();
        }

        foreach (string raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            line = Regex.Replace(line, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2");
            line = Regex.Replace(line, "`([^`]*)`", "$1");

            if (line.StartsWith('#'))
            {
                Flush();
                paragraph.Append(line.TrimStart('#').Trim());
                Flush();
            }
            else if (Regex.IsMatch(line, @"^([-*+]|\d+\.)\s"))
            {
                // Each bullet starts its own line; a wrapped bullet's continuation
                // lines join onto it like any other paragraph text.
                if (paragraph.Length > 0) paragraph.Append("\r\n");
                paragraph.Append("•  ").Append(Regex.Replace(line, @"^([-*+]|\d+\.)\s+", ""));
            }
            else
            {
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(line);
            }
        }
        Flush();
        return output.ToString();
    }

    private async void InstallClicked(object? sender, EventArgs e)
    {
        _installButton.Enabled = false;
        try
        {
            if (!_downloaded)
            {
                _status.Text = "Downloading update...";
                await _manager.DownloadUpdatesAsync(_update, percent =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        try
                        {
                            BeginInvoke(() =>
                            {
                                if (!IsDisposed) _progress.Value = Math.Clamp(percent, 0, 100);
                            });
                        }
                        catch (InvalidOperationException) { }
                    }
                }, _closing.Token);
                _downloaded = true;
            }

            if (_closing.IsCancellationRequested || IsDisposed) return;
            _status.Text = "Installing update...";
            if (!_apply(_update.TargetFullRelease))
            {
                _status.Text = "Update is ready. Try again in a moment.";
                _installButton.Enabled = true;
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            // Closing the dialog while downloading means "Later".
        }
        catch (Exception ex)
        {
            if (_closing.IsCancellationRequested || IsDisposed) return;
            _status.Text = "The update could not be installed.";
            _installButton.Enabled = true;
            MessageBox.Show(this, ex.Message, "APS update failed", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// A slim gold progress bar. The stock ProgressBar draws the system's green bar in
    /// a light well, and ignores its colours whenever visual styles are on.
    /// </summary>
    private sealed class ProgressStrip : Control
    {
        private int _value;

        public ProgressStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
        }

        /// <summary>Percent complete, 0–100.</summary>
        public int Value
        {
            get => _value;
            set
            {
                int clamped = Math.Clamp(value, 0, 100);
                if (clamped == _value) return;
                _value = clamped;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var track = new SolidBrush(UiTheme.Card);
            e.Graphics.FillRectangle(track, ClientRectangle);
            int filled = (int)Math.Round(Width * _value / 100f);
            if (filled <= 0) return;
            using var fill = new SolidBrush(UiTheme.Gold);
            e.Graphics.FillRectangle(fill, 0, 0, filled, Height);
        }
    }
}
