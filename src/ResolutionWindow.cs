using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace RezTray
{
    /// <summary>Creates and deletes custom resolutions through the NVIDIA driver.</summary>
    internal sealed class ResolutionWindow : ThemedForm
    {
        private const int MinWidth = 640, MinHeight = 480, MinHz = 24, MaxHz = 540;

        private readonly ComboBox _monitor;
        private readonly TextBox _width, _height, _hz;
        private readonly Label _hint;
        private readonly Button _create, _delete;
        private readonly ListBox _added;

        public ResolutionWindow(Monitor preferred) : base("Create resolution", 420, 470)
        {
            AddLabel("Monitor", 20, 18, 380, dim: true);
            _monitor = AddCombo(20, 40, 380);

            AddLabel("Width", 20, 84, 110, dim: true);
            AddLabel("Height", 140, 84, 110, dim: true);
            AddLabel("Refresh (Hz)", 260, 84, 140, dim: true);
            _width = AddTextBox(20, 106, 110);
            _height = AddTextBox(140, 106, 110);
            _hz = AddTextBox(260, 106, 140);

            _hint = AddLabel("", 20, 142, 380, 58, dim: true);
            _create = AddButton("Test resolution", 20, 206, 380, primary: true);

            AddLabel("Saved in the NVIDIA driver", 20, 258, 380, dim: true);
            _added = AddList(20, 280, 380, 130);
            _delete = AddButton("Delete", 300, 420, 100);

            foreach (var box in new[] { _width, _height, _hz })
            {
                box.TextChanged += (s, e) => UpdateHint();
                box.KeyPress += (s, e) => { if (char.IsLetter(e.KeyChar) || char.IsPunctuation(e.KeyChar)) e.Handled = true; };
            }
            _monitor.SelectedIndexChanged += (s, e) => LoadMonitor();
            _added.SelectedIndexChanged += (s, e) => _delete.Enabled = _added.SelectedItem != null;
            _create.Click += (s, e) => CreateResolution();
            _delete.Click += (s, e) => DeleteSelected();
            AcceptButton = _create;

            ReloadMonitors(preferred?.Key);
        }

        /// <summary>Raised after the driver's list or the active resolution changes.</summary>
        public event EventHandler ResolutionsChanged;

        private Monitor Selected => (Monitor)_monitor.SelectedItem;

        private void ReloadMonitors(string key)
        {
            var monitors = Monitors.Read();
            _monitor.Items.Clear();
            _monitor.Items.AddRange(monitors.Cast<object>().ToArray());
            if (monitors.Count > 0)
                _monitor.SelectedItem = monitors.FirstOrDefault(m => m.Key == key) ?? monitors[0];
        }

        private void LoadMonitor()
        {
            var monitor = Selected;
            _added.Items.Clear();
            _added.Items.AddRange(monitor.Added.Cast<object>().ToArray());
            _delete.Enabled = false;

            if (_hz.Text.Length == 0) _hz.Text = monitor.Native.Hz.ToString();
            UpdateHint();
        }

        private bool TryRead(out DisplayMode mode)
        {
            mode = default;
            if (!int.TryParse(_width.Text, out mode.Width)) return false;
            if (!int.TryParse(_height.Text, out mode.Height)) return false;
            return int.TryParse(_hz.Text, out mode.Hz);
        }

        /// <summary>Returns why this cannot be created, or null if it can.</summary>
        private static string Problem(Monitor monitor, DisplayMode mode)
        {
            if (mode.Width < MinWidth || mode.Height < MinHeight)
                return $"The smallest size is {MinWidth}×{MinHeight}.";
            if (mode.Width > monitor.Native.Width || mode.Height > monitor.Native.Height)
                return $"This monitor tops out at {monitor.Native.Size}.";
            if (mode.Hz < MinHz || mode.Hz > MaxHz)
                return $"Refresh has to be between {MinHz} and {MaxHz} Hz.";
            if (monitor.Added.Any(a => a.Mode.Width == mode.Width && a.Mode.Height == mode.Height && a.Mode.Hz == mode.Hz))
                return "That resolution already exists.";
            return null;
        }

        private void UpdateHint()
        {
            var monitor = Selected;
            if (monitor == null) return;

            if (!TryRead(out var mode))
            {
                _hint.ForeColor = P.Dim;
                _hint.Text = "Enter a size smaller than the monitor, like 1920 × 1080. Your screen tests it, then you choose to keep it.";
                _create.Enabled = false;
                return;
            }

            string problem = Problem(monitor, mode);
            var timing = problem == null
                ? NvApi.CalculateTiming(monitor.DisplayId, monitor.PanelWidth, monitor.PanelHeight, mode.Hz)
                : null;
            bool beyond = timing != null && !timing.ReportedByMonitor;

            _create.Enabled = problem == null && timing != null;
            _hint.ForeColor = problem != null || beyond ? P.Warning : P.Dim;
            _hint.Text = problem
                ?? (timing == null ? "The driver could not work out a signal for this refresh rate."
                : beyond ? $"{mode.Hz} Hz is more than this monitor says it can do. It may go black or show no signal. If it does, wait: it comes back by itself."
                : $"The monitor stays at {monitor.PanelWidth}×{monitor.PanelHeight} · {mode.Hz} Hz and the GPU scales the picture.");
        }

        private void CreateResolution()
        {
            var monitor = Selected;
            if (!TryRead(out var mode) || Problem(monitor, mode) != null) return;

            int driverWidth = monitor.Sideways ? mode.Height : mode.Width;
            int driverHeight = monitor.Sideways ? mode.Width : mode.Height;
            var timing = NvApi.CalculateTiming(monitor.DisplayId, monitor.PanelWidth, monitor.PanelHeight, mode.Hz);
            if (timing == null) return;

            int tried = NvApi.TryCustomDisplay(monitor.DisplayId, driverWidth, driverHeight, timing);
            if (tried != 0)
            {
                Alert($"The driver would not test {mode}: {NvApi.Describe(tried)}.");
                return;
            }

            bool keep;
            using (var dialog = new KeepDialog(mode, monitor.Name))
                keep = dialog.ShowDialog(this) == DialogResult.Yes;

            if (!keep)
            {
                NvApi.RevertCustomDisplay(monitor.DisplayId);
                return;
            }

            int saved = NvApi.SaveCustomDisplay(monitor.DisplayId);
            if (saved != 0)
            {
                NvApi.RevertCustomDisplay(monitor.DisplayId);
                Alert($"The driver would not save {mode}: {NvApi.Describe(saved)}.");
                return;
            }

            // The trial was outside Windows' records. Switching properly makes it stick after a reboot.
            var updated = Monitors.Read().FirstOrDefault(m => m.Key == monitor.Key);
            var added = updated?.Added.FirstOrDefault(a => a.Usable && a.Mode.Width == mode.Width && a.Mode.Height == mode.Height);
            string failed = added == null
                ? "Saved, but Windows has not listed it yet. Restart the PC if it is missing from the menu."
                : Monitors.Apply(updated, added.Mode);

            ReloadMonitors(monitor.Key);
            ResolutionsChanged?.Invoke(this, EventArgs.Empty);
            if (failed != null) Alert(failed);
        }

        private void DeleteSelected()
        {
            var monitor = Selected;
            if (!(_added.SelectedItem is AddedResolution item)) return;

            var answer = MessageBox.Show(this, $"Delete {item} from {monitor.Name}?", "Delete resolution",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            if (monitor.IsRunning(item.Mode))
            {
                string failed = Monitors.Apply(monitor, monitor.Native);
                if (failed != null) { Alert(failed); return; }
            }

            int status = NvApi.DeleteCustomDisplay(monitor.DisplayId, item.Driver.Raw);
            if (status != 0) Alert($"The driver would not delete it: {NvApi.Describe(status)}.");

            ReloadMonitors(monitor.Key);
            ResolutionsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Alert(string message) =>
            MessageBox.Show(this, message, "RezTray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>Stays on top during a test, and goes back unless the user says to keep it.</summary>
    internal sealed class KeepDialog : ThemedForm
    {
        private const int Seconds = 15;
        private readonly Timer _timer = new Timer { Interval = 1000 };
        private int _left = Seconds;

        public KeepDialog(DisplayMode mode, string monitor) : base("Keep this resolution?", 360, 134)
        {
            TopMost = true;
            ShowInTaskbar = false;
            ControlBox = false;
            StartPosition = FormStartPosition.Manual;

            AddLabel($"{mode} on {monitor}", 20, 16, 320);
            var countdown = AddLabel($"Going back in {_left} seconds", 20, 40, 320, dim: true);
            var keep = AddButton("Keep", 20, 80, 155, primary: true);
            var back = AddButton("Go back", 185, 80, 155);

            keep.Click += (s, e) => { DialogResult = DialogResult.Yes; };
            back.Click += (s, e) => { DialogResult = DialogResult.No; };
            CancelButton = back;

            _timer.Tick += (s, e) =>
            {
                countdown.Text = $"Going back in {--_left} seconds";
                if (_left <= 0) DialogResult = DialogResult.No;
            };
            Shown += (s, e) =>
            {
                var area = Screen.PrimaryScreen.WorkingArea;
                Location = new System.Drawing.Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
                _timer.Start();
            };
            FormClosed += (s, e) => _timer.Dispose();
        }
    }
}
