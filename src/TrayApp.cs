using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RezTray
{
    /// <summary>The tray icon, its menu, hotkeys, and keeping color profiles applied.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        private const int FirstUpdateCheckMs = 30 * 1000;
        private const int UpdateCheckEveryMs = 24 * 60 * 60 * 1000;

        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly Control _ui;
        private readonly Timer _reapply = new Timer { Interval = 800 };
        private readonly Timer _updateTimer = new Timer { Interval = FirstUpdateCheckMs };
        private readonly HotkeyManager _hotkeys = new HotkeyManager();
        private readonly InstanceSignal _instanceSignal;
        private readonly AppSettings _settings = AppSettings.Load();
        private List<Monitor> _monitors = new List<Monitor>();
        private ResolutionWindow _resolutionWindow;
        private ProfileEditor _profileEditor;
        private HotkeyWindow _hotkeyWindow;
        private SetupWindow _setupWindow;
        private string _availableUpdate;
        private bool _balloonOpensUpdate;

        /// <param name="announce">Says where the app went, for a launch from the Start menu,
        /// because the icon usually sits behind the up arrow.</param>
        public TrayApp(bool announce = false)
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
            _icon.BalloonTipClicked += (s, e) =>
            {
                if (_balloonOpensUpdate) OpenReleasePage();
            };

            _instanceSignal = new InstanceSignal(() => OnUi(ShowMenuAtCursor));

            if (!NvApi.Initialize())
                Warn("The NVIDIA driver could not be reached, so there is nothing to switch.");
            else if (announce)
                Tell("RezTray is running. Find it in the up arrow next to the clock.");

            _monitors = Monitors.Read();
            UpdateTooltip();
            ApplySavedProfiles();

            _hotkeys.Pressed += RunHotkey;
            WarnAboutHotkeys(_hotkeys.Register(_settings.Hotkeys));

            _reapply.Tick += (s, e) => { _reapply.Stop(); _monitors = Monitors.Read(); UpdateTooltip(); ApplySavedProfiles(); };
            SystemEvents.DisplaySettingsChanged += OnEnvironmentChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            SystemEvents.UserPreferenceChanged += OnThemeChanged;

            _updateTimer.Tick += (s, e) =>
            {
                _updateTimer.Interval = UpdateCheckEveryMs;
                if (!_settings.UpdateAlertsOff) CheckForUpdates(false);
            };
            _updateTimer.Start();
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

        private void Warn(string message)
        {
            _balloonOpensUpdate = false;
            _icon.ShowBalloonTip(5000, "RezTray", message, ToolTipIcon.Warning);
        }

        private void Tell(string message)
        {
            _balloonOpensUpdate = false;
            _icon.ShowBalloonTip(4000, "RezTray", message, ToolTipIcon.Info);
        }

        private void WarnAboutHotkeys(List<HotkeyBinding> failed)
        {
            if (failed.Count > 0)
                Warn($"Another app already owns {string.Join(", ", failed.Select(b => b.Keys))}.");
        }

        private void ShowMenuAtCursor()
        {
            // NotifyIcon only opens its menu on right-click; this is the same call it makes.
            typeof(NotifyIcon)
                .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(_icon, null);
        }

        internal ContextMenuStrip Menu => _menu;

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

            if (_availableUpdate != null)
            {
                var update = Item($"Update available: {_availableUpdate}");
                update.Click += (s, e) => OpenReleasePage();
                _menu.Items.Add(update);
                _menu.Items.Add(new ToolStripSeparator());
            }

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

            if (_monitors.Count > 0)
            {
                _menu.Items.Add(SetupsMenu(renderer));
                foreach (var monitor in _monitors.Where(m => m.Rates.Count > 1))
                    _menu.Items.Add(RefreshMenu(monitor, renderer));
                foreach (var monitor in _monitors)
                    _menu.Items.Add(ColorMenu(monitor, renderer));
                _menu.Items.Add(new ToolStripSeparator());

                _menu.Items.Add(Action("Create resolution…", () => OpenResolutionWindow(null)));
                _menu.Items.Add(Action("Edit color profiles…", OpenProfileEditor));
                _menu.Items.Add(Action("Hotkeys…", OpenHotkeyWindow));
            }

            _menu.Items.Add(MoreMenu(renderer));
            _menu.Items.Add(Action("Exit", ExitThread));
        }

        private static ToolStripMenuItem Item(string text) => new ToolStripMenuItem(text)
        {
            Padding = new Padding(0, 3, 0, 3),
        };

        private static ToolStripMenuItem Action(string text, Action action)
        {
            var item = Item(text);
            item.Click += (s, e) => action();
            return item;
        }

        private static ToolStripMenuItem Header(string text, string detail) => new ToolStripMenuItem(text)
        {
            Enabled = false,
            ShortcutKeyDisplayString = detail,
            ShowShortcutKeys = detail != null,
        };

        private void StyleSubmenu(ToolStripMenuItem parent, MenuRenderer renderer)
        {
            var drop = (ToolStripDropDownMenu)parent.DropDown;
            drop.ShowImageMargin = false;
            drop.ShowCheckMargin = true;
            drop.Renderer = renderer;
            drop.Padding = new Padding(0, 4, 0, 4);
            drop.Font = _menu.Font;
            Dwm.RoundWindow(drop.Handle);
        }

        private ToolStripMenuItem ModeItem(Monitor monitor, DisplayMode mode, string detail)
        {
            var item = Item(mode.Size);
            item.ShortcutKeyDisplayString = detail;
            item.ShowShortcutKeys = true;
            item.Checked = monitor.IsRunning(mode);
            item.Click += (s, e) =>
            {
                if (!monitor.IsRunning(mode)) SwitchTo(monitor, mode);
            };
            return item;
        }

        private ToolStripMenuItem RefreshMenu(Monitor monitor, MenuRenderer renderer)
        {
            var parent = Item($"Refresh: {monitor.Name} · {monitor.Current.Hz} Hz");
            foreach (int hz in monitor.Rates)
            {
                var mode = new DisplayMode { Width = monitor.Current.Width, Height = monitor.Current.Height, Hz = hz };
                var item = Item($"{hz} Hz");
                item.Checked = hz == monitor.Current.Hz;
                item.Click += (s, e) =>
                {
                    if (hz != monitor.Current.Hz) SwitchTo(monitor, mode);
                };
                parent.DropDownItems.Add(item);
            }
            StyleSubmenu(parent, renderer);
            return parent;
        }

        private ToolStripMenuItem ColorMenu(Monitor monitor, MenuRenderer renderer)
        {
            var active = _settings.ActiveProfile(monitor);
            var parent = Item($"Color: {monitor.Name} · {active.Name}");

            foreach (var profile in _settings.Profiles)
            {
                var item = Item(profile.Name);
                item.Checked = profile.Id == active.Id;
                item.Click += (s, e) => { UseProfile(monitor, profile); _settings.Save(); };
                parent.DropDownItems.Add(item);
            }
            StyleSubmenu(parent, renderer);
            return parent;
        }

        private ToolStripMenuItem SetupsMenu(MenuRenderer renderer)
        {
            var parent = Item("Setups");
            foreach (var setup in _settings.Setups)
                parent.DropDownItems.Add(Action(setup.Name, () => ApplySetup(setup)));
            if (_settings.Setups.Count > 0) parent.DropDownItems.Add(new ToolStripSeparator());
            parent.DropDownItems.Add(Action("Manage setups…", OpenSetupWindow));
            StyleSubmenu(parent, renderer);
            return parent;
        }

        private ToolStripMenuItem MoreMenu(MenuRenderer renderer)
        {
            var parent = Item("More");
            parent.DropDownItems.Add(Action("Import…", ImportFile));
            parent.DropDownItems.Add(Action("Export…", ExportFile));
            parent.DropDownItems.Add(new ToolStripSeparator());

            var startup = Item("Start with Windows");
            startup.Checked = AutoStart.Enabled;
            startup.Click += (s, e) => AutoStart.Enabled = !AutoStart.Enabled;
            parent.DropDownItems.Add(startup);

            var alerts = Item("Update alerts");
            alerts.Checked = !_settings.UpdateAlertsOff;
            alerts.Click += (s, e) =>
            {
                _settings.UpdateAlertsOff = !_settings.UpdateAlertsOff;
                _settings.Save();
            };
            parent.DropDownItems.Add(alerts);
            parent.DropDownItems.Add(Action("Check for updates now", () => CheckForUpdates(true)));

            var version = Header($"Version {Updates.Current.ToString(3)}", null);
            parent.DropDownItems.Add(version);

            StyleSubmenu(parent, renderer);
            return parent;
        }

        private void SwitchTo(Monitor monitor, DisplayMode mode)
        {
            string problem = Monitors.Apply(monitor, mode);
            if (problem != null) Warn(problem);
        }

        /// <summary>Applies and remembers a profile. The caller saves the settings.</summary>
        private void UseProfile(Monitor monitor, ColorProfile profile)
        {
            string problem = ColorEngine.Apply(monitor, profile);
            if (problem != null) Warn(problem);

            // Default leaves no entry, so RezTray goes back to not touching that monitor.
            if (profile.IsDefault) _settings.Active.Remove(monitor.Key);
            else _settings.Active[monitor.Key] = profile.Id;
        }

        private void ApplySetup(Setup setup)
        {
            _monitors = Monitors.Read();
            var problems = new List<string>();

            foreach (var entry in setup.Monitors)
            {
                var monitor = _monitors.FirstOrDefault(m => m.Key == entry.MonitorKey);
                if (monitor == null)
                {
                    problems.Add($"{entry.MonitorName} is not connected.");
                    continue;
                }

                bool same = monitor.IsRunning(entry.Mode) && monitor.Current.Hz == entry.Hz;
                string problem = same ? null : Monitors.Apply(monitor, entry.Mode);
                if (problem != null) problems.Add($"{entry.MonitorName}: {problem}");

                UseProfile(monitor, _settings.Profiles.FirstOrDefault(p => p.Id == entry.ProfileId) ?? _settings.Default);
            }

            _settings.Save();
            if (problems.Count > 0) Warn(string.Join(" ", problems));
        }

        private void RunHotkey(HotkeyBinding binding)
        {
            if (binding.Action == HotkeyBinding.SetupAction)
            {
                var setup = _settings.Setups.FirstOrDefault(s => s.Id == binding.TargetId);
                if (setup != null) ApplySetup(setup);
                else Warn($"The setup for {binding.Keys} no longer exists.");
                return;
            }

            _monitors = Monitors.Read();
            var monitor = _monitors.FirstOrDefault(m => m.Key == binding.MonitorKey);
            if (monitor == null)
            {
                Warn($"The monitor for {binding.Keys} is not connected.");
                return;
            }

            switch (binding.Action)
            {
                case HotkeyBinding.Native:
                    SwitchTo(monitor, monitor.Native);
                    break;

                case HotkeyBinding.Resolution:
                    SwitchTo(monitor, new DisplayMode { Width = binding.Width, Height = binding.Height, Hz = binding.Hz });
                    break;

                case HotkeyBinding.Color:
                    var profile = _settings.Profiles.FirstOrDefault(p => p.Id == binding.TargetId);
                    if (profile == null)
                    {
                        Warn($"The color profile for {binding.Keys} no longer exists.");
                        return;
                    }
                    UseProfile(monitor, profile);
                    _settings.Save();
                    break;
            }
        }

        private async void CheckForUpdates(bool userAsked)
        {
            string tag = await Updates.LatestTagAsync();

            if (tag == null)
            {
                if (userAsked) Warn("Could not reach GitHub. Try again later.");
                return;
            }

            if (!Updates.IsNewer(tag, Updates.Current))
            {
                _availableUpdate = null;
                if (userAsked) Tell($"You have the latest version ({Updates.Current.ToString(3)}).");
                return;
            }

            _availableUpdate = tag;

            // Automatic checks mention each new version once; the menu keeps showing it.
            if (!userAsked && _settings.LastAlertedVersion == tag) return;
            _settings.LastAlertedVersion = tag;
            _settings.Save();

            _balloonOpensUpdate = true;
            _icon.ShowBalloonTip(6000, "RezTray update", $"Version {tag} is out. Click to open the download page.", ToolTipIcon.Info);
        }

        private static void OpenReleasePage() => Process.Start(Updates.ReleasePage);

        private void ExportFile()
        {
            using (var dialog = new SaveFileDialog { Filter = Sharing.Filter, FileName = "RezTray-profiles.reztray" })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                try
                {
                    Sharing.Export(dialog.FileName, _settings, Monitors.Read());
                    Tell("Saved. Send that file to a friend, who can load it with More, then Import.");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Warn($"Could not save the file: {e.Message}");
                }
            }
        }

        private void ImportFile()
        {
            using (var dialog = new OpenFileDialog { Filter = Sharing.Filter })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                try
                {
                    var result = Sharing.Import(dialog.FileName, _settings);
                    _settings.Save();

                    int profiles = result.ProfilesAdded;
                    Tell(profiles == 1 ? "Added 1 color profile." : $"Added {profiles} color profiles.");
                    if (result.Resolutions.Count > 0) OpenResolutionWindow(result.Resolutions);
                }
                catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
                {
                    Warn(e.Message);
                }
            }
        }

        private void OpenResolutionWindow(List<DisplayMode> imported)
        {
            if (_resolutionWindow == null)
            {
                _resolutionWindow = new ResolutionWindow(_monitors.FirstOrDefault());
                _resolutionWindow.ResolutionsChanged += (s, e) => OnEnvironmentChanged(s, e);
                _resolutionWindow.FormClosed += (s, e) => _resolutionWindow = null;
                _resolutionWindow.Show();
            }
            if (imported != null) _resolutionWindow.ShowImported(imported);
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

        private void OpenHotkeyWindow()
        {
            if (_hotkeyWindow != null) { _hotkeyWindow.Activate(); return; }

            _hotkeyWindow = new HotkeyWindow(_settings, _monitors,
                paused =>
                {
                    if (paused) _hotkeys.Clear();
                    else _hotkeys.Register(_settings.Hotkeys);
                },
                () => _hotkeys.Register(_settings.Hotkeys));
            _hotkeyWindow.FormClosed += (s, e) => _hotkeyWindow = null;
            _hotkeyWindow.Show();
            _hotkeyWindow.Activate();
        }

        private void OpenSetupWindow()
        {
            if (_setupWindow != null) { _setupWindow.Activate(); return; }

            _setupWindow = new SetupWindow(_settings, Monitors.Read, ApplySetup);
            _setupWindow.FormClosed += (s, e) =>
            {
                _setupWindow = null;
                _hotkeys.Register(_settings.Hotkeys);   // a deleted setup takes its hotkey with it
            };
            _setupWindow.Show();
            _setupWindow.Activate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnEnvironmentChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerChanged;
                SystemEvents.UserPreferenceChanged -= OnThemeChanged;
                _hotkeys.Dispose();
                _instanceSignal.Dispose();
                _reapply.Dispose();
                _updateTimer.Dispose();
                _icon.Visible = false;
                _icon.Dispose();
                _menu.Dispose();
                _ui.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
