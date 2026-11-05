using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RezTray
{
    [DataContract]
    internal sealed class SharedResolution
    {
        [DataMember] public int Width, Height, Hz;
    }

    /// <summary>
    /// What goes in a .reztray file. It holds no monitor ids, hotkeys or setups: those belong
    /// to one PC, and monitor ids contain a hardware path the user has no reason to hand out.
    /// </summary>
    [DataContract]
    internal sealed class ShareFile
    {
        [DataMember] public string App = "RezTray";
        [DataMember] public int Format = 1;
        [DataMember] public List<ColorProfile> Profiles = new List<ColorProfile>();
        [DataMember] public List<SharedResolution> Resolutions = new List<SharedResolution>();
    }

    internal sealed class ImportResult
    {
        public int ProfilesAdded;
        public List<DisplayMode> Resolutions = new List<DisplayMode>();
    }

    /// <summary>
    /// Writes and reads share files. An imported file came from someone else, so nothing in it
    /// is trusted, and importing changes no display: profiles are only added to the list, and
    /// resolutions only fill in the Create window, where the 15-second test still applies.
    /// </summary>
    internal static class Sharing
    {
        public const string Filter = "RezTray file (*.reztray)|*.reztray";

        private const int MaxFileBytes = 1024 * 1024;
        private const int MaxItems = 50, MaxNameLength = 40;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(ShareFile));

        public static void Export(string path, AppSettings settings, IEnumerable<Monitor> monitors)
        {
            var file = new ShareFile
            {
                Profiles = settings.Profiles.Where(p => !p.IsDefault).ToList(),
                Resolutions = monitors
                    .SelectMany(m => m.Added)
                    .Select(a => a.Mode)
                    .Distinct()
                    .Select(m => new SharedResolution { Width = m.Width, Height = m.Height, Hz = m.Hz })
                    .ToList(),
            };

            using (var stream = File.Create(path))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true))
                Serializer.WriteObject(writer, file);
        }

        /// <summary>Adds the file's profiles to the settings. Throws InvalidDataException if it is not usable.</summary>
        public static ImportResult Import(string path, AppSettings settings)
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                throw new InvalidDataException("That file is too big to be a RezTray file.");

            ShareFile file;
            try
            {
                using (var stream = File.OpenRead(path))
                    file = (ShareFile)Serializer.ReadObject(stream);
            }
            catch (SerializationException)
            {
                throw new InvalidDataException("That file is not a RezTray file.");
            }

            if (file?.App != "RezTray")
                throw new InvalidDataException("That file is not a RezTray file.");
            if (file.Format != 1)
                throw new InvalidDataException("That file is from a newer version of RezTray.");

            var result = new ImportResult();

            foreach (var incoming in (file.Profiles ?? new List<ColorProfile>()).Take(MaxItems))
            {
                if (incoming == null) continue;
                var profile = Sanitize(incoming, settings);
                if (settings.Profiles.Any(p => SameColors(p, profile) && p.Name == incoming.Name)) continue;

                settings.Profiles.Add(profile);
                result.ProfilesAdded++;
            }

            result.Resolutions = (file.Resolutions ?? new List<SharedResolution>())
                .Where(r => r != null && r.Width > 0 && r.Height > 0 && r.Hz > 0)
                .Select(r => new DisplayMode { Width = r.Width, Height = r.Height, Hz = r.Hz })
                .Distinct()
                .Take(MaxItems)
                .ToList();

            return result;
        }

        private static ColorProfile Sanitize(ColorProfile incoming, AppSettings settings)
        {
            string name = new string((incoming.Name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            if (name.Length == 0) name = "Imported";
            if (name.Length > MaxNameLength) name = name.Substring(0, MaxNameLength);

            var profile = new ColorProfile
            {
                Name = name,
                Vibrance = Clamp(incoming.Vibrance, 0, 100),
                Brightness = Clamp(incoming.Brightness, 0, 100),
                Contrast = Clamp(incoming.Contrast, 0, 100),
                Gamma = Clamp(incoming.Gamma, 30, 280),
            };

            // Same name with different colors: keep both rather than overwrite the user's profile.
            if (settings.Profiles.Any(p => p.Name == name && !SameColors(p, profile)))
                profile.Name = $"{name} (imported)";
            return profile;
        }

        private static bool SameColors(ColorProfile a, ColorProfile b) =>
            a.Vibrance == b.Vibrance && a.Brightness == b.Brightness && a.Contrast == b.Contrast && a.Gamma == b.Gamma;

        private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    }
}
