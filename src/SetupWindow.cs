using System;
using System.Collections.Generic;
using System.Linq;

namespace RezTray
{
    /// <summary>Saves, applies and deletes named setups covering every monitor.</summary>
    internal sealed class SetupWindow : ThemedForm
    {
        private readonly AppSettings _settings;
        private readonly Func<List<Monitor>> _readMonitors;
        private readonly Action<Setup> _apply;
        private readonly System.Windows.Forms.ListBox _list;
        private readonly System.Windows.Forms.Label _detail;
        private readonly System.Windows.Forms.TextBox _name;
        private readonly System.Windows.Forms.Button _use, _delete, _save;

        public SetupWindow(AppSettings settings, Func<List<Monitor>> readMonitors, Action<Setup> apply)
            : base("Setups", 440, 400)
        {
            _settings = settings;
            _readMonitors = readMonitors;
            _apply = apply;

            AddLabel("Saved setups", 20, 18, 400, dim: true);
            _list = AddList(20, 40, 400, 130);
            _detail = AddLabel("", 20, 178, 400, 62, dim: true);
            _use = AddButton("Use this setup", 20, 246, 195, primary: true);
            _delete = AddButton("Delete", 225, 246, 195);

            AddLabel("Save how your monitors are right now", 20, 300, 400, dim: true);
            _name = AddTextBox(20, 324, 270);
            _save = AddButton("Save setup", 300, 321, 120);

            _list.SelectedIndexChanged += (s, e) => ShowSelected();
            _name.TextChanged += (s, e) => _save.Enabled = _name.Text.Trim().Length > 0;
            _use.Click += (s, e) => { if (_list.SelectedItem is Setup setup) _apply(setup); };
            _delete.Click += (s, e) => DeleteSelected();
            _save.Click += (s, e) => SaveCurrent();
            AcceptButton = _save;

            _save.Enabled = false;
            ReloadList(null);
        }

        private void ReloadList(Setup select)
        {
            _list.Items.Clear();
            _list.Items.AddRange(_settings.Setups.Cast<object>().ToArray());
            if (select != null) _list.SelectedItem = select;
            else if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            ShowSelected();
        }

        private void ShowSelected()
        {
            var setup = _list.SelectedItem as Setup;
            _use.Enabled = _delete.Enabled = setup != null;
            _detail.Text = setup == null
                ? "No setups yet. Set your resolutions and colors, then name it below."
                : string.Join(Environment.NewLine, setup.Monitors.Select(Describe));
        }

        private string Describe(SetupMonitor entry)
        {
            var profile = _settings.Profiles.FirstOrDefault(p => p.Id == entry.ProfileId) ?? _settings.Default;
            return $"{entry.MonitorName}: {entry.Mode}, {profile.Name}";
        }

        private void SaveCurrent()
        {
            string name = _name.Text.Trim();
            if (name.Length == 0) return;

            var captured = Setup.Capture(name, _readMonitors(), _settings);

            // Saving under an existing name updates it, which keeps any hotkey pointing at it working.
            var existing = _settings.Setups.FirstOrDefault(s => s.Name == name);
            if (existing != null) existing.Monitors = captured.Monitors;
            else _settings.Setups.Add(captured);

            _settings.Save();
            _name.Clear();
            ReloadList(existing ?? captured);
        }

        private void DeleteSelected()
        {
            if (!(_list.SelectedItem is Setup setup)) return;
            _settings.Setups.Remove(setup);
            _settings.Hotkeys.RemoveAll(h => h.Action == HotkeyBinding.SetupAction && h.TargetId == setup.Id);
            _settings.Save();
            ReloadList(null);
        }
    }
}
