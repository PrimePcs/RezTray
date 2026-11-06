using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Win32;

namespace RezTray
{
    [DataContract]
    internal sealed class ColorProfile
    {
        public const string DefaultId = "default";

        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name = "New profile";
        [DataMember] public int Vibrance = 50;     // 0-100, 50 = off
        [DataMember] public int Brightness = 50;   // 0-100, 50 = neutral
        [DataMember] public int Contrast = 50;     // 0-100, 50 = neutral
        [DataMember] public int Gamma = 100;       // 30-280, 100 = 1.00

        public bool IsDefault => Id == DefaultId;

        public ColorProfile CopyAs(string name) => new ColorProfile
        {
            Name = name, Vibrance = Vibrance, Brightness = Brightness, Contrast = Contrast, Gamma = Gamma,
        };

        public override string ToString() => Name;
    }

    /// <summary>A key combination and what it does.</summary>
    [DataContract]
    internal sealed class HotkeyBinding
    {
        public const string Resolution = "resolution", Native = "native", Color = "color", SetupAction = "setup";

        [DataMember] public int Modifiers;
        [DataMember] public int Key;
        [DataMember] public string Action;
        [DataMember] public string MonitorKey;
        [DataMember] public int Width, Height, Hz;
        [DataMember] public string TargetId;       // color profile or setup id
        [DataMember] public string Label;

        public string Keys => Hotkey.Format(Modifiers, (System.Windows.Forms.Keys)Key);

        public HotkeyBinding WithKeys(int modifiers, System.Windows.Forms.Keys key)
        {
            var copy = (HotkeyBinding)MemberwiseClone();
            copy.Modifiers = modifiers;
            copy.Key = (int)key;
            return copy;
        }

        public override string ToString() => Key == 0 ? Label : $"{Keys}   →   {Label}";
    }

    /// <summary>One monitor's part of a saved setup.</summary>
    [DataContract]
    internal sealed class SetupMonitor
    {
        [DataMember] public string MonitorKey;
        [DataMember] public string MonitorName;
        [DataMember] public int Width, Height, Hz;
        [DataMember] public string ProfileId;

        public DisplayMode Mode => new DisplayMode { Width = Width, Height = Height, Hz = Hz };
    }

    /// <summary>A named resolution and color for every monitor.</summary>
    [DataContract]
    internal sealed class Setup
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name;
        [DataMember] public List<SetupMonitor> Monitors = new List<SetupMonitor>();

        public static Setup Capture(string name, IEnumerable<Monitor> monitors, AppSettings settings) => new Setup
        {
            Name = name,
            Monitors = monitors.Select(m => new SetupMonitor
            {
                MonitorKey = m.Key,
                MonitorName = m.Name,
                Width = m.Current.Width,
                Height = m.Current.Height,
                Hz = m.Current.Hz,
                ProfileId = settings.ActiveProfile(m).Id,
            }).ToList(),
        };

        public override string ToString() => Name;
    }

    [DataContract]
    internal sealed class AppSettings
    {
        [DataMember] public List<ColorProfile> Profiles = new List<ColorProfile>();
        [DataMember] public List<HotkeyBinding> Hotkeys = new List<HotkeyBinding>();
        [DataMember] public List<Setup> Setups = new List<Setup>();

        // Named this way round so that a settings file from 1.0, which lacks it, leaves alerts on.
        [DataMember] public bool UpdateAlertsOff;
        [DataMember] public string LastAlertedVersion;

        /// <summary>Monitor key to profile id. No entry means RezTray leaves that monitor alone.</summary>
        [DataMember] public Dictionary<string, string> Active = new Dictionary<string, string>();

        private static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RezTray");
        private static string FilePath => Path.Combine(Folder, "settings.json");

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(AppSettings),
                new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public ColorProfile Default => Profiles.First(p => p.IsDefault);

        public ColorProfile ActiveProfile(Monitor monitor) =>
            Active.TryGetValue(monitor.Key, out var id)
                ? Profiles.FirstOrDefault(p => p.Id == id) ?? Default
                : Default;

        public static AppSettings Load()
        {
            AppSettings settings = null;
            try
            {
                if (File.Exists(FilePath))
                    using (var stream = File.OpenRead(FilePath))
                        settings = Read(stream);
            }
            catch (Exception e) when (e is IOException || e is SerializationException || e is UnauthorizedAccessException)
            {
                // Keep a broken file for the user instead of silently overwriting their profiles.
                try { File.Copy(FilePath, FilePath + ".broken", true); } catch (IOException) { }
            }

            return Normalized(settings);
        }

        internal static AppSettings Read(Stream stream) => (AppSettings)Serializer.ReadObject(stream);

        /// <summary>The serializer skips initializers, so fields a file lacks arrive as null.</summary>
        internal static AppSettings Normalized(AppSettings settings)
        {
            settings = settings ?? new AppSettings();
            settings.Profiles = settings.Profiles ?? new List<ColorProfile>();
            settings.Hotkeys = settings.Hotkeys ?? new List<HotkeyBinding>();
            settings.Setups = settings.Setups ?? new List<Setup>();
            settings.Active = settings.Active ?? new Dictionary<string, string>();
            settings.EnsureDefaults();
            return settings;
        }

        private void EnsureDefaults()
        {
            if (Profiles.Any(p => p.IsDefault)) return;

            Profiles.Insert(0, new ColorProfile { Id = ColorProfile.DefaultId, Name = "Default" });
            if (Profiles.Count == 1)
            {
                Profiles.Add(new ColorProfile { Name = "Vibrant", Vibrance = 70 });
                Profiles.Add(new ColorProfile { Name = "Competitive", Vibrance = 75, Brightness = 54, Gamma = 115 });
            }
        }

        internal void Write(Stream stream)
        {
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                Serializer.WriteObject(writer, this);
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            using (var stream = File.Create(temp))
                Write(stream);

            if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
            else File.Move(temp, FilePath);
        }
    }

    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "RezTray";

        public static bool Enabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key?.GetValue(ValueName) != null;
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) key.SetValue(ValueName, Command);
                    else key.DeleteValue(ValueName, false);
                }
            }
        }

        // --startup keeps the "RezTray is running" message from appearing at every sign-in.
        private static string Command => $"\"{System.Windows.Forms.Application.ExecutablePath}\" --startup";

        /// <summary>If the user turned this on and the app has since moved, follow it.</summary>
        public static void KeepPathCurrent()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key.GetValue(ValueName) is string current && current != Command)
                    key.SetValue(ValueName, Command);
            }
        }
    }
}
