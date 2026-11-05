using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace RezTray
{
    /// <summary>Lets the user bind key combinations to resolutions, colors and setups.</summary>
    internal sealed class HotkeyWindow : ThemedForm
    {
        private readonly AppSettings _settings;
        private readonly List<Monitor> _monitors;
        private readonly Action<bool> _pause;
        private readonly Func<List<HotkeyBinding>> _apply;

        private readonly ListBox _list;
        private readonly ComboBox _action;
        private readonly TextBox _keys;
        private readonly Button _add, _remove;
        private readonly Label _status;
        private int _modifiers;
        private Keys _key;

        /// <param name="pause">Turns hotkeys off while the user presses a new combination,
        /// otherwise pressing an existing one fires it instead of reaching this window.</param>
        /// <param name="apply">Registers the saved hotkeys and returns any that failed.</param>
        public HotkeyWindow(AppSettings settings, List<Monitor> monitors, Action<bool> pause, Func<List<HotkeyBinding>> apply)
            : base("Hotkeys", 480, 446)
        {
            _settings = settings;
            _monitors = monitors;
            _pause = pause;
            _apply = apply;

            AddLabel("Your hotkeys", 20, 18, 440, dim: true);
            _list = AddList(20, 40, 440, 170);
            var quick = AddButton("Add Ctrl+Alt+0-9 for main monitor", 20, 220, 270);
            _remove = AddButton("Remove", 360, 220, 100);

            AddLabel("What it does", 20, 270, 440, dim: true);
            _action = AddCombo(20, 292, 440);
            AddLabel("Keys: click here, then press them", 20, 332, 300, dim: true);
            _keys = AddTextBox(20, 354, 300);
            _keys.ReadOnly = true;
            _keys.ShortcutsEnabled = false;
            _keys.BackColor = P.Surface;
            _add = AddButton("Add hotkey", 330, 351, 130, primary: true);

            _status = AddLabel("", 20, 392, 440, 42);
            _status.ForeColor = P.Warning;

            _action.Items.AddRange(HotkeyActions.All(monitors, settings).Cast<object>().ToArray());
            if (_action.Items.Count > 0) _action.SelectedIndex = 0;

            _keys.PreviewKeyDown += (s, e) => e.IsInputKey = true;
            _keys.KeyDown += ReadKeys;
            _keys.Enter += (s, e) => _pause(true);
            _keys.Leave += (s, e) => _pause(false);
            _action.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _add.Click += (s, e) => AddSelected();
            _remove.Click += (s, e) => RemoveSelected();
            quick.Click += (s, e) => AddQuickSet();
            FormClosed += (s, e) => _pause(false);

            ReloadList();
            ShowFailures(apply());
        }

        private void ReadKeys(object sender, KeyEventArgs e)
        {
            e.Handled = e.SuppressKeyPress = true;
            if (Hotkey.IsModifierKey(e.KeyCode)) return;

            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete || e.KeyCode == Keys.Escape)
            {
                SetKeys(0, Keys.None);
                return;
            }

            int modifiers = Hotkey.ModifiersFrom(e);
            if (!Hotkey.IsAllowed(modifiers, e.KeyCode))
            {
                _status.Text = "Use Ctrl or Alt with that key, or an F key on its own.";
                return;
            }

            _status.Text = "";
            SetKeys(modifiers, e.KeyCode);
        }

        private void SetKeys(int modifiers, Keys key)
        {
            _modifiers = modifiers;
            _key = key;
            _keys.Text = key == Keys.None ? "" : Hotkey.Format(modifiers, key);
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            _add.Enabled = _key != Keys.None && _action.SelectedItem != null;
            _remove.Enabled = _list.SelectedItem != null;
        }

        private bool IsTaken(int modifiers, Keys key) =>
            _settings.Hotkeys.Any(b => b.Modifiers == modifiers && b.Key == (int)key);

        private void AddSelected()
        {
            if (IsTaken(_modifiers, _key))
            {
                _status.Text = $"{Hotkey.Format(_modifiers, _key)} is already in your list.";
                return;
            }

            var action = (HotkeyBinding)_action.SelectedItem;
            _settings.Hotkeys.Add(action.WithKeys(_modifiers, _key));
            SetKeys(0, Keys.None);
            Commit();
        }

        private void AddQuickSet()
        {
            var main = _monitors.FirstOrDefault();
            if (main == null) return;

            var actions = new List<HotkeyBinding> { HotkeyActions.Native(main) };
            actions.AddRange(main.Added.Where(a => a.Usable).Take(9).Select(a => HotkeyActions.Resolution(main, a.Mode)));

            for (int i = 0; i < actions.Count; i++)
            {
                var key = Keys.D0 + i;
                if (!IsTaken(Hotkey.Ctrl | Hotkey.Alt, key))
                    _settings.Hotkeys.Add(actions[i].WithKeys(Hotkey.Ctrl | Hotkey.Alt, key));
            }
            Commit();
        }

        private void RemoveSelected()
        {
            if (!(_list.SelectedItem is HotkeyBinding binding)) return;
            _settings.Hotkeys.Remove(binding);
            Commit();
        }

        private void Commit()
        {
            _settings.Save();
            ReloadList();
            _status.Text = "";
            ShowFailures(_apply());
        }

        private void ReloadList()
        {
            _list.Items.Clear();
            _list.Items.AddRange(_settings.Hotkeys.Cast<object>().ToArray());
            UpdateButtons();
        }

        private void ShowFailures(List<HotkeyBinding> failed)
        {
            if (failed.Count == 0) return;
            string keys = string.Join(", ", failed.Select(b => b.Keys));
            _status.Text = $"Another app already owns {keys}. Remove it and pick different keys.";
        }
    }
}
