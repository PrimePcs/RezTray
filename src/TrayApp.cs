using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RezTray
{
    /// <summary>The tray icon, its menu, and keeping color profiles applied.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly Control _ui;
        private readonly Timer _reapply = new Timer { Interval = 800 };
        private readonly AppSettings _settings = AppSettings.Load();
        private List<Monitor> _monitors = new List<Monitor>();
        private ResolutionWindow _resolutionWindow;
        private ProfileEditor _profileEditor;

        public TrayApp()
        {
            _ui = new Control();
            _ = _ui.Handle;   // SystemEvents arrive on another thread; this marshals them back

            _menu = new ContextMenuStrip
            {
                Font = new Font("Segoe UI", 9.5f),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Padding = new Padding(0, 4, 0, 4),
            };
            _menu.Opening += (s, e) => Rebuild();

            _icon = new NotifyIcon { Icon = TrayGlyph.Create(), Text = "RezTray", ContextMenuStrip = _menu, Visible = true };
            _icon.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMenuAtCursor();
            };

            if (!NvApi.Initialize())
                Warn("The NVIDIA driver could not be reached, so there is nothing to switch.");

            _monitors = Monitors.Read();
            UpdateTooltip();
            ApplySavedProfiles();

            _reapply.Tick += (s, e) => { _reapply.Stop(); _monitors = Monitors.Read(); UpdateTooltip(); ApplySavedProfiles(); };
            SystemEvents.DisplaySettingsChanged += OnEnvironmentChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            SystemEvents.UserPreferenceChanged += OnThemeChanged;
        }

        // Windows drops gamma ramps on mode changes, unlock and resume, so they are put back.
        private void OnEnvironmentChanged(object sender, EventArgs e) => OnUi(() => { _reapply.Stop(); _reapply.Start(); });

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock) OnEnvironmentChanged(sender, e);
        }

        private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) OnEnvironmentChanged(sender, e);
        }

        private void OnThemeChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General) return;
            OnUi(() =>
            {
                var old = _icon.Icon;
                _icon.Icon = TrayGlyph.Create();
                old.Dispose();
            });
        }

        private void OnUi(Action action)
        {
            if (_ui.IsHandleCreated) _ui.BeginInvoke(action);
        }

        private void ApplySavedProfiles()
        {
            foreach (var monitor in _monitors.Where(m => _settings.Active.ContainsKey(m.Key)))
            {
                string problem = ColorEngine.Apply(monitor, _settings.ActiveProfile(monitor));
                if (problem != null) Warn(problem);
            }
        }

        private void UpdateTooltip()
        {
            var main = _monitors.FirstOrDefault();
            string text = main == null ? "RezTray" : $"RezTray · {main.Current}";
            _icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        private void Warn(string message) =>
            _icon.ShowBalloonTip(5000, "RezTray", message, ToolTipIcon.Warning);

        private void ShowMenuAtCursor()
        {
            // NotifyIcon only opens its menu on right-click; this is the same call it makes.
            typeof(NotifyIcon)
                .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(_icon, null);
        }

        internal ContextMenuStrip Menu => _menu;
        internal AppSettings Settings => _settings;
        internal List<Monitor> CurrentMonitors => _monitors;

        internal void Rebuild()
        {
            _monitors = Monitors.Read();
            UpdateTooltip();

            var palette = Palette.Current();
            var renderer = new MenuRenderer(palette);
            _menu.Renderer = renderer;
            Dwm.RoundWindow(_menu.Handle);

            foreach (var item in _menu.Items.Cast<ToolStripItem>().ToList()) item.Dispose();
            _menu.Items.Clear();

            if (_monitors.Count == 0)
                _menu.Items.Add(Header("No NVIDIA display found", null));

            foreach (var monitor in _monitors)
            {
                _menu.Items.Add(Header(monitor.Name, monitor.Current.ToString()));
                _menu.Items.Add(ModeItem(monitor, monitor.Native, "Native"));
                foreach (var added in monitor.Added.Where(a => a.Usable))
                    _menu.Items.Add(ModeItem(monitor, added.Mode, $"{added.Mode.Hz} Hz"));
                _menu.Items.Add(new ToolStripSeparator());
            }

            var create = Item("Create resolution…");
            create.Click += (s, e) => OpenResolutionWindow();
            create.Enabled = _monitors.Count > 0;
            _menu.Items.Add(create);
            _menu.Items.Add(new ToolStripSeparator());

            foreach (var monitor in _monitors)
                _menu.Items.Add(ColorMenu(monitor, renderer));

            var editProfiles = Item("Edit color profiles…");
            editProfiles.Click += (s, e) => OpenProfileEditor();
            editProfiles.Enabled = _monitors.Count > 0;
            _menu.Items.Add(editProfiles);
            _menu.Items.Add(new ToolStripSeparator());

            var startup = Item("Start with Windows");
            startup.Checked = AutoStart.Enabled;
            startup.Click += (s, e) => AutoStart.Enabled = !AutoStart.Enabled;
            _menu.Items.Add(startup);

            var exit = Item("Exit");
            exit.Click += (s, e) => ExitThread();
            _menu.Items.Add(exit);
        }

        private static ToolStripMenuItem Item(string text) => new ToolStripMenuItem(text)
        {
            Padding = new Padding(0, 3, 0, 3),
        };

        private static ToolStripMenuItem Header(string text, string detail) => new ToolStripMenuItem(text)
        {
            Enabled = false,
            ShortcutKeyDisplayString = detail,
            ShowShortcutKeys = detail != null,
        };

        private ToolStripMenuItem ModeItem(Monitor monitor, DisplayMode mode, string detail)
        {
            var item = Item(mode.Size);
            item.ShortcutKeyDisplayString = detail;
            item.ShowShortcutKeys = true;
            item.Checked = monitor.IsRunning(mode);
            item.Click += (s, e) =>
            {
                if (monitor.IsRunning(mode)) return;
                string problem = Monitors.Apply(monitor, mode);
                if (problem != null) Warn(problem);
            };
            return item;
        }

        private ToolStripMenuItem ColorMenu(Monitor monitor, MenuRenderer renderer)
        {
            var active = _settings.ActiveProfile(monitor);
            var parent = Item($"Color: {monitor.Name} · {active.Name}");

            foreach (var profile in _settings.Profiles)
            {
                var item = Item(profile.Name);
                item.Checked = profile.Id == active.Id;
                item.Click += (s, e) => UseProfile(monitor, profile);
                parent.DropDownItems.Add(item);
            }

            var drop = (ToolStripDropDownMenu)parent.DropDown;
            drop.ShowImageMargin = false;
            drop.ShowCheckMargin = true;
            drop.Renderer = renderer;
            drop.Padding = new Padding(0, 4, 0, 4);
            drop.Font = _menu.Font;
            Dwm.RoundWindow(drop.Handle);
            return parent;
        }

        private void UseProfile(Monitor monitor, ColorProfile profile)
        {
            string problem = ColorEngine.Apply(monitor, profile);
            if (problem != null) Warn(problem);

            // Default leaves no entry, so RezTray goes back to not touching that monitor.
            if (profile.IsDefault) _settings.Active.Remove(monitor.Key);
            else _settings.Active[monitor.Key] = profile.Id;
            _settings.Save();
        }

        private void OpenResolutionWindow()
        {
            if (_resolutionWindow != null) { _resolutionWindow.Activate(); return; }

            _resolutionWindow = new ResolutionWindow(_monitors.FirstOrDefault());
            _resolutionWindow.ResolutionsChanged += (s, e) => OnEnvironmentChanged(s, e);
            _resolutionWindow.FormClosed += (s, e) => _resolutionWindow = null;
            _resolutionWindow.Show();
            _resolutionWindow.Activate();
        }

        private void OpenProfileEditor()
        {
            if (_profileEditor != null) { _profileEditor.Activate(); return; }

            _profileEditor = new ProfileEditor(_settings, _monitors);
            _profileEditor.FormClosed += (s, e) => _profileEditor = null;
            _profileEditor.Show();
            _profileEditor.Activate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnEnvironmentChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerChanged;
                SystemEvents.UserPreferenceChanged -= OnThemeChanged;
                _reapply.Dispose();
                _icon.Visible = false;
                _icon.Dispose();
                _menu.Dispose();
                _ui.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
