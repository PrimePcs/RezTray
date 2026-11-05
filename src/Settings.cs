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

    [DataContract]
    internal sealed class AppSettings
    {
        [DataMember] public List<ColorProfile> Profiles = new List<ColorProfile>();

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
                        settings = (AppSettings)Serializer.ReadObject(stream);
            }
            catch (Exception e) when (e is IOException || e is SerializationException || e is UnauthorizedAccessException)
            {
                // Keep a broken file for the user instead of silently overwriting their profiles.
                try { File.Copy(FilePath, FilePath + ".broken", true); } catch (IOException) { }
            }

            settings = settings ?? new AppSettings();
            settings.Profiles = settings.Profiles ?? new List<ColorProfile>();
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

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            string temp = FilePath + ".tmp";
            using (var stream = File.Create(temp))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                Serializer.WriteObject(writer, this);

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
                    if (value) key.SetValue(ValueName, $"\"{System.Windows.Forms.Application.ExecutablePath}\"");
                    else key.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
