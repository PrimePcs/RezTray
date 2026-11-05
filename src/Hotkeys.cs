using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RezTray
{
    internal static class Hotkey
    {
        public const int Alt = 1, Ctrl = 2, Shift = 4;

        public static int ModifiersFrom(KeyEventArgs e) =>
            (e.Control ? Ctrl : 0) | (e.Alt ? Alt : 0) | (e.Shift ? Shift : 0);

        public static bool IsFunctionKey(Keys key) => key >= Keys.F1 && key <= Keys.F24;

        public static bool IsModifierKey(Keys key) =>
            key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu;

        /// <summary>Plain keys need Ctrl or Alt, or the hotkey would swallow normal typing.</summary>
        public static bool IsAllowed(int modifiers, Keys key)
        {
            if (key == Keys.None || IsModifierKey(key)) return false;
            return IsFunctionKey(key) || (modifiers & (Ctrl | Alt)) != 0;
        }

        public static string Format(int modifiers, Keys key)
        {
            var parts = new List<string>();
            if ((modifiers & Ctrl) != 0) parts.Add("Ctrl");
            if ((modifiers & Alt) != 0) parts.Add("Alt");
            if ((modifiers & Shift) != 0) parts.Add("Shift");
            parts.Add(key >= Keys.D0 && key <= Keys.D9 ? ((char)key).ToString() : key.ToString());
            return string.Join("+", parts);
        }
    }

    /// <summary>Everything a hotkey can do right now, as bindings with no keys yet.</summary>
    internal static class HotkeyActions
    {
        public static List<HotkeyBinding> All(IEnumerable<Monitor> monitors, AppSettings settings)
        {
            var actions = new List<HotkeyBinding>();
            var list = monitors.ToList();

            foreach (var monitor in list)
            {
                actions.Add(Native(monitor));
                actions.AddRange(monitor.Added.Where(a => a.Usable).Select(a => Resolution(monitor, a.Mode)));
            }

            foreach (var monitor in list)
                foreach (var profile in settings.Profiles)
                    actions.Add(new HotkeyBinding
                    {
                        Action = HotkeyBinding.Color,
                        MonitorKey = monitor.Key,
                        TargetId = profile.Id,
                        Label = $"Color on {monitor.Name}: {profile.Name}",
                    });

            actions.AddRange(settings.Setups.Select(setup => new HotkeyBinding
            {
                Action = HotkeyBinding.SetupAction,
                TargetId = setup.Id,
                Label = $"Setup: {setup.Name}",
            }));

            return actions;
        }

        public static HotkeyBinding Native(Monitor monitor) => new HotkeyBinding
        {
            Action = HotkeyBinding.Native,
            MonitorKey = monitor.Key,
            Label = $"{monitor.Name}: Native",
        };

        public static HotkeyBinding Resolution(Monitor monitor, DisplayMode mode) => new HotkeyBinding
        {
            Action = HotkeyBinding.Resolution,
            MonitorKey = monitor.Key,
            Width = mode.Width,
            Height = mode.Height,
            Hz = mode.Hz,
            Label = $"{monitor.Name}: {mode}",
        };
    }

    /// <summary>Owns the system-wide hotkeys, which work while a game has focus.</summary>
    internal sealed class HotkeyManager : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int MOD_NOREPEAT = 0x4000;
        private static readonly IntPtr MessageOnly = new IntPtr(-3);

        private readonly Dictionary<int, HotkeyBinding> _registered = new Dictionary<int, HotkeyBinding>();

        public event Action<HotkeyBinding> Pressed;

        public HotkeyManager() => CreateHandle(new CreateParams { Parent = MessageOnly });

        /// <summary>Replaces all hotkeys. Returns the ones another app already owns.</summary>
        public List<HotkeyBinding> Register(IEnumerable<HotkeyBinding> bindings)
        {
            Clear();
            var failed = new List<HotkeyBinding>();
            int id = 1;

            foreach (var binding in bindings)
            {
                bool ok = Hotkey.IsAllowed(binding.Modifiers, (Keys)binding.Key)
                    && RegisterHotKey(Handle, id, binding.Modifiers | MOD_NOREPEAT, binding.Key);
                if (ok) _registered[id++] = binding;
                else failed.Add(binding);
            }
            return failed;
        }

        public void Clear()
        {
            foreach (int id in _registered.Keys) UnregisterHotKey(Handle, id);
            _registered.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && _registered.TryGetValue(m.WParam.ToInt32(), out var binding))
                Pressed?.Invoke(binding);
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Clear();
            DestroyHandle();
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, int modifiers, int key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    }
}
