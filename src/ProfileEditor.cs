using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace RezTray
{
    /// <summary>Creates and edits color profiles, previewing them live on one monitor.</summary>
    internal sealed class ProfileEditor : ThemedForm
    {
        private readonly AppSettings _settings;
        private readonly HashSet<Monitor> _previewed = new HashSet<Monitor>();
        private readonly ListBox _list;
        private readonly ComboBox _monitor;
        private readonly TextBox _name;
        private readonly FlatSlider _vibrance, _brightness, _contrast, _gamma;
        private readonly Button _delete, _use;
        private readonly Label _status;
        private readonly bool _livePreview;
        private bool _loading;

        public ProfileEditor(AppSettings settings, List<Monitor> monitors, bool livePreview = true)
            : base("Color profiles", 560, 420)
        {
            _settings = settings;
            _livePreview = livePreview;

            AddLabel("Profiles", 20, 18, 170, dim: true);
            _list = AddList(20, 40, 170, 310);
            var add = AddButton("New", 20, 362, 80);
            _delete = AddButton("Delete", 110, 362, 80);

            AddLabel("Preview on", 210, 18, 330, dim: true);
            _monitor = AddCombo(210, 40, 330);
            AddLabel("Name", 210, 80, 330, dim: true);
            _name = AddTextBox(210, 102, 330);

            _vibrance = Slider("Digital vibrance", 0, 100, 50, 142, v => $"{v}%");
            _brightness = Slider("Brightness", 0, 100, 50, 192, v => $"{v}%");
            _contrast = Slider("Contrast", 0, 100, 50, 242, v => $"{v}%");
            _gamma = Slider("Gamma", 30, 280, 100, 292, v => $"{v / 100.0:0.00}");

            _status = AddLabel("", 210, 340, 330, 20, dim: true);
            _status.ForeColor = P.Warning;
            _use = AddButton("Use on this monitor", 210, 362, 330, primary: true);

            _monitor.Items.AddRange(monitors.Cast<object>().ToArray());
            _list.Items.AddRange(settings.Profiles.Cast<object>().ToArray());

            _list.SelectedIndexChanged += (s, e) => ShowProfile();
            _monitor.SelectedIndexChanged += (s, e) => UpdateUseButton();
            _name.TextChanged += (s, e) => Rename();
            add.Click += (s, e) => AddProfile();
            _delete.Click += (s, e) => DeleteProfile();
            _use.Click += (s, e) => UseProfile();

            if (monitors.Count > 0) _monitor.SelectedIndex = 0;
            _list.SelectedItem = monitors.Count > 0 ? settings.ActiveProfile(monitors[0]) : settings.Default;
        }

        private ColorProfile Profile => (ColorProfile)_list.SelectedItem;
        private Monitor Target => (Monitor)_monitor.SelectedItem;

        private FlatSlider Slider(string label, int min, int max, int defaultValue, int y, Func<int, string> format)
        {
            var slider = new FlatSlider(P, label, min, max, defaultValue, format)
            {
                Bounds = new System.Drawing.Rectangle(S(210), S(y), S(330), S(46)),
            };
            slider.ValueChanged += (s, e) => SliderMoved();
            Controls.Add(slider);
            return slider;
        }

        private void ShowProfile()
        {
            var profile = Profile;
            if (profile == null) return;

            _loading = true;
            _name.Text = profile.Name;
            _vibrance.Value = profile.Vibrance;
            _brightness.Value = profile.Brightness;
            _contrast.Value = profile.Contrast;
            _gamma.Value = profile.Gamma;
            _loading = false;

            bool editable = !profile.IsDefault;
            _name.Enabled = _vibrance.Enabled = _brightness.Enabled = _contrast.Enabled = _gamma.Enabled = _delete.Enabled = editable;

            UpdateUseButton();
            Preview();
        }

        private void SliderMoved()
        {
            if (_loading || Profile == null || Profile.IsDefault) return;
            Profile.Vibrance = _vibrance.Value;
            Profile.Brightness = _brightness.Value;
            Profile.Contrast = _contrast.Value;
            Profile.Gamma = _gamma.Value;
            Preview();
        }

        private void Rename()
        {
            if (_loading || Profile == null || Profile.IsDefault) return;
            string name = _name.Text.Trim();
            if (name.Length == 0) return;
            Profile.Name = name;
            _list.Invalidate();
        }

        private void Preview()
        {
            if (!_livePreview || Target == null || Profile == null) return;
            _previewed.Add(Target);
            _status.Text = ColorEngine.Apply(Target, Profile) ?? "";
        }

        private void AddProfile()
        {
            string name = Enumerable.Range(1, 99).Select(i => $"Profile {i}")
                .First(n => _settings.Profiles.All(p => p.Name != n));
            var profile = (Profile ?? _settings.Default).CopyAs(name);
            _settings.Profiles.Add(profile);
            _list.Items.Add(profile);
            _list.SelectedItem = profile;
            _name.Focus();
            _name.SelectAll();
        }

        private void DeleteProfile()
        {
            var profile = Profile;
            if (profile == null || profile.IsDefault) return;

            int index = _list.SelectedIndex;
            foreach (var key in _settings.Active.Where(a => a.Value == profile.Id).Select(a => a.Key).ToList())
                _settings.Active.Remove(key);
            _settings.Profiles.Remove(profile);
            _list.Items.Remove(profile);
            _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
        }

        private void UseProfile()
        {
            if (Target == null || Profile == null) return;
            _settings.Active[Target.Key] = Profile.Id;
            _settings.Save();
            UpdateUseButton();
        }

        private void UpdateUseButton()
        {
            if (Target == null || Profile == null) { _use.Enabled = false; return; }
            bool inUse = _settings.ActiveProfile(Target).Id == Profile.Id;
            _use.Enabled = !inUse;
            _use.Text = inUse ? $"In use on {Target.Name}" : $"Use on {Target.Name}";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // Previews end here: every monitor goes back to the profile it is really using.
            foreach (var monitor in _previewed)
                ColorEngine.Apply(monitor, _settings.ActiveProfile(monitor));
            if (_livePreview) _settings.Save();
            base.OnFormClosed(e);
        }
    }
}
