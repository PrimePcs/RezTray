using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RezTray
{
    internal sealed class Palette
    {
        public Color Back, Surface, Hover, Text, Dim, Line, Accent, AccentText, Warning;

        private static bool ReadLight(string value)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return key?.GetValue(value) is int light && light != 0;
        }

        public static bool TaskbarIsLight => ReadLight("SystemUsesLightTheme");
        public static bool AppsAreLight => ReadLight("AppsUseLightTheme");

        public static Palette Current() => AppsAreLight
            ? new Palette
            {
                Back = Color.FromArgb(249, 249, 249), Surface = Color.White, Hover = Color.FromArgb(232, 232, 232),
                Text = Color.FromArgb(26, 26, 26), Dim = Color.FromArgb(104, 104, 104), Line = Color.FromArgb(220, 220, 220),
                Accent = Color.FromArgb(82, 130, 0), AccentText = Color.White, Warning = Color.FromArgb(157, 93, 0),
            }
            : new Palette
            {
                Back = Color.FromArgb(32, 32, 32), Surface = Color.FromArgb(45, 45, 45), Hover = Color.FromArgb(58, 58, 58),
                Text = Color.FromArgb(245, 245, 245), Dim = Color.FromArgb(160, 160, 160), Line = Color.FromArgb(68, 68, 68),
                Accent = Color.FromArgb(118, 185, 0), AccentText = Color.FromArgb(16, 20, 8), Warning = Color.FromArgb(252, 200, 100),
            };
    }

    internal static class Dwm
    {
        private const int UseDarkMode = 20, CornerPreference = 33, RoundCorners = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void RoundWindow(IntPtr handle)
        {
            int round = RoundCorners;
            DwmSetWindowAttribute(handle, CornerPreference, ref round, sizeof(int));
        }

        public static void DarkTitleBar(IntPtr handle, bool dark)
        {
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, UseDarkMode, ref value, sizeof(int));
        }
    }

    internal static class TrayGlyph
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>A small monitor outline, drawn in the taskbar's text color.</summary>
        public static Icon Create()
        {
            int size = SystemInformation.SmallIconSize.Width;
            var color = Palette.TaskbarIsLight ? Color.FromArgb(28, 28, 28) : Color.White;

            using (var bitmap = new Bitmap(size, size))
            using (var g = Graphics.FromImage(bitmap))
            using (var pen = new Pen(color, size / 12f))
            {
                float s = size / 16f;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawRectangle(pen, 1.5f * s, 2.5f * s, 13 * s, 8.5f * s);
                g.DrawLine(pen, 8 * s, 11 * s, 8 * s, 13.5f * s);
                g.DrawLine(pen, 4.5f * s, 13.5f * s, 11.5f * s, 13.5f * s);
                g.DrawLine(pen, 5 * s, 8.5f * s, 11 * s, 5 * s);
                g.DrawLine(pen, 8.2f * s, 5 * s, 11 * s, 5 * s);
                g.DrawLine(pen, 11 * s, 5 * s, 11 * s, 7.6f * s);

                var handle = bitmap.GetHicon();
                try { using (var icon = Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }

    /// <summary>Paints the tray menu like a Windows 11 menu.</summary>
    internal sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly Palette _p;

        public MenuRenderer(Palette palette) : base(new FlatColors(palette))
        {
            _p = palette;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(_p.Back);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(_p.Line))
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;

            var bounds = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(_p.Hover))
            using (var path = Rounded(bounds, 4))
                e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var item = e.Item as ToolStripMenuItem;
            bool isHeader = item != null && !item.Enabled;
            bool isSecondary = item != null && e.Text == item.ShortcutKeyDisplayString;

            e.TextColor = isHeader || isSecondary ? _p.Dim : _p.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var box = e.ImageRectangle;
            float s = box.Height / 16f, x = box.X + box.Width / 2f, y = box.Y + box.Height / 2f;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(_p.Accent, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                e.Graphics.DrawLines(pen, new[]
                {
                    new PointF(x - 4 * s, y), new PointF(x - 1 * s, y + 3 * s), new PointF(x + 4.5f * s, y - 3.5f * s),
                });
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(_p.Line))
                e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            float x = e.ArrowRectangle.X + e.ArrowRectangle.Width / 2f;
            float y = e.ArrowRectangle.Y + e.ArrowRectangle.Height / 2f;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(_p.Dim, 1.3f))
                e.Graphics.DrawLines(pen, new[]
                {
                    new PointF(x - 2, y - 4), new PointF(x + 2, y), new PointF(x - 2, y + 4),
                });
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class FlatColors : ProfessionalColorTable
        {
            private readonly Palette _p;
            public FlatColors(Palette palette) { _p = palette; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground => _p.Back;
            public override Color MenuBorder => _p.Line;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => _p.Hover;
            public override Color ImageMarginGradientBegin => _p.Back;
            public override Color ImageMarginGradientMiddle => _p.Back;
            public override Color ImageMarginGradientEnd => _p.Back;
        }
    }

    /// <summary>A dark-aware window with small helpers for building a form in code.</summary>
    internal abstract class ThemedForm : Form
    {
        protected readonly Palette P = Palette.Current();

        protected ThemedForm(string title, int width, int height)
        {
            Text = title;
            Font = new Font("Segoe UI", 9.5f);
            BackColor = P.Back;
            ForeColor = P.Text;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(width), S(height));
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }

        protected int S(int value) => (int)Math.Round(value * DeviceDpi / 96f);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Dwm.DarkTitleBar(Handle, !Palette.AppsAreLight);
        }

        protected Label AddLabel(string text, int x, int y, int width, int height = 20, bool dim = false)
        {
            var label = new Label
            {
                Text = text, Bounds = new Rectangle(S(x), S(y), S(width), S(height)),
                ForeColor = dim ? P.Dim : P.Text, BackColor = Color.Transparent,
            };
            Controls.Add(label);
            return label;
        }

        protected TextBox AddTextBox(int x, int y, int width)
        {
            var box = new TextBox
            {
                Bounds = new Rectangle(S(x), S(y), S(width), S(26)),
                BackColor = P.Surface, ForeColor = P.Text, BorderStyle = BorderStyle.FixedSingle,
            };
            Controls.Add(box);
            return box;
        }

        protected Button AddButton(string text, int x, int y, int width, bool primary = false)
        {
            var button = new Button
            {
                Text = text, Bounds = new Rectangle(S(x), S(y), S(width), S(32)),
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                BackColor = primary ? P.Accent : P.Surface,
                ForeColor = primary ? P.AccentText : P.Text,
            };
            button.FlatAppearance.BorderColor = primary ? P.Accent : P.Line;
            button.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(P.Accent, 0.2f) : P.Hover;
            button.EnabledChanged += (s, e) =>
            {
                bool lit = primary && button.Enabled;
                button.BackColor = lit ? P.Accent : P.Surface;
                button.FlatAppearance.BorderColor = lit ? P.Accent : P.Line;
            };
            Controls.Add(button);
            return button;
        }

        protected ComboBox AddCombo(int x, int y, int width)
        {
            var combo = new ComboBox
            {
                Bounds = new Rectangle(S(x), S(y), S(width), S(28)),
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = P.Surface, ForeColor = P.Text,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(22),
            };
            combo.DrawItem += (s, e) => DrawRow(e, combo.GetItemText(e.Index >= 0 ? combo.Items[e.Index] : ""));
            Controls.Add(combo);
            return combo;
        }

        protected ListBox AddList(int x, int y, int width, int height)
        {
            var list = new ListBox
            {
                Bounds = new Rectangle(S(x), S(y), S(width), S(height)),
                BackColor = P.Surface, ForeColor = P.Text, BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(28), IntegralHeight = false,
            };
            list.DrawItem += (s, e) => { if (e.Index >= 0) DrawRow(e, list.GetItemText(list.Items[e.Index])); };
            Controls.Add(list);
            return list;
        }

        private void DrawRow(DrawItemEventArgs e, string text)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(selected ? P.Hover : P.Surface))
                e.Graphics.FillRectangle(back, e.Bounds);

            var bounds = Rectangle.Inflate(e.Bounds, -S(8), 0);
            TextRenderer.DrawText(e.Graphics, text, Font, bounds, P.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>A labelled slider. Double-click puts it back to its default.</summary>
    internal sealed class FlatSlider : Control
    {
        private readonly Palette _p;
        private readonly string _label;
        private readonly int _min, _max, _default;
        private readonly Func<int, string> _format;
        private int _value;

        public event EventHandler ValueChanged;

        public FlatSlider(Palette palette, string label, int min, int max, int defaultValue, Func<int, string> format)
        {
            _p = palette; _label = label; _min = min; _max = max; _default = _value = defaultValue; _format = format;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                     ControlStyles.StandardDoubleClick, true);
            TabStop = true;
        }

        public int Value
        {
            get => _value;
            set => SetValue(value, false);
        }

        private void SetValue(int value, bool raise)
        {
            value = Math.Max(_min, Math.Min(_max, value));
            if (value == _value) return;
            _value = value;
            Invalidate();
            if (raise) ValueChanged?.Invoke(this, EventArgs.Empty);
        }

        private int Pad => Height / 8;
        private int TrackY => Height - Pad - 4;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? _p.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var text = Enabled ? _p.Text : _p.Dim;
            var top = new Rectangle(0, 0, Width, TrackY - Pad);
            TextRenderer.DrawText(g, _label, Font, top, text, TextFormatFlags.Left | TextFormatFlags.Top);
            TextRenderer.DrawText(g, _format(_value), Font, top, _p.Dim, TextFormatFlags.Right | TextFormatFlags.Top);

            float radius = 7 * DeviceDpi / 96f;
            float left = radius, right = Width - radius;
            float x = left + (right - left) * (_value - _min) / (_max - _min);

            using (var line = new Pen(_p.Line, 3))
                g.DrawLine(line, left, TrackY, right, TrackY);
            using (var fill = new Pen(Enabled ? _p.Accent : _p.Dim, 3))
                g.DrawLine(fill, left, TrackY, x, TrackY);
            using (var thumb = new SolidBrush(Enabled ? _p.Accent : _p.Dim))
                g.FillEllipse(thumb, x - radius, TrackY - radius, radius * 2, radius * 2);
            if (Focused)
                using (var ring = new Pen(_p.Text))
                    g.DrawEllipse(ring, x - radius, TrackY - radius, radius * 2, radius * 2);
        }

        private void SetFromMouse(int x)
        {
            float radius = 7 * DeviceDpi / 96f;
            float t = (x - radius) / (Width - radius * 2);
            SetValue(_min + (int)Math.Round(t * (_max - _min)), true);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); if (e.Button == MouseButtons.Left) SetFromMouse(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { if (e.Button == MouseButtons.Left) SetFromMouse(e.X); }
        protected override void OnMouseWheel(MouseEventArgs e) => SetValue(_value + Math.Sign(e.Delta), true);
        protected override void OnDoubleClick(EventArgs e) => SetValue(_default, true);
        protected override void OnGotFocus(EventArgs e) => Invalidate();
        protected override void OnLostFocus(EventArgs e) => Invalidate();
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        protected override bool IsInputKey(Keys key) =>
            key == Keys.Left || key == Keys.Right || base.IsInputKey(key);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) SetValue(_value - 1, true);
            else if (e.KeyCode == Keys.Right) SetValue(_value + 1, true);
            else base.OnKeyDown(e);
        }
    }
}
