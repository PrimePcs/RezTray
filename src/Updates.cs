using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;

namespace RezTray
{
    /// <summary>
    /// Asks GitHub for the newest release. It only ever tells the user: nothing is downloaded
    /// or installed, and the request carries no information about the PC.
    /// </summary>
    internal static class Updates
    {
        public const string ReleasePage = "https://github.com/PrimePcs/RezTray/releases/latest";
        private const string ApiUrl = "https://api.github.com/repos/PrimePcs/RezTray/releases/latest";
        private const int TimeoutMs = 10000;

        [DataContract]
        private sealed class Release
        {
            [DataMember(Name = "tag_name")] public string Tag = null;
        }

        public static Version Current => Assembly.GetExecutingAssembly().GetName().Version;

        /// <summary>Compares a release tag such as v1.2.0 with a version. A bad tag is never newer.</summary>
        public static bool IsNewer(string tag, Version current)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            if (!Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var latest)) return false;
            return Plain(latest) > Plain(current);
        }

        // Version treats 1.1.0 and 1.1.0.0 as different, so compare three parts only.
        private static Version Plain(Version v) => new Version(v.Major, v.Minor, Math.Max(v.Build, 0));

        /// <summary>The latest release tag, or null if GitHub cannot be reached.</summary>
        public static Task<string> LatestTagAsync() => Task.Run(() =>
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(ApiUrl);
                request.UserAgent = "RezTray";
                request.Accept = "application/vnd.github+json";
                request.Timeout = request.ReadWriteTimeout = TimeoutMs;

                using (var response = request.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(Release));
                    return ((Release)serializer.ReadObject(stream))?.Tag;
                }
            }
            catch (Exception e) when (e is WebException || e is IOException || e is SerializationException)
            {
                return null;   // offline or rate limited: try again next time
            }
        });
    }
}
