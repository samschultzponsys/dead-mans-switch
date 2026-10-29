using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeadMansSwitch
{
    public sealed class UpdateInfo
    {
        public string Version { get; set; }
        public string DownloadUrl { get; set; }
    }

    /// <summary>
    /// Finds newer releases on GitHub and swaps the running exe for the new one.
    /// </summary>
    public static class Updater
    {
        const string Repo = "samschultzponsys/dead-mans-switch";
        const string AssetName = "DeadMansSwitch.exe";

        [DataContract]
        sealed class Release
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "assets")] public Asset[] Assets { get; set; }
        }

        [DataContract]
        sealed class Asset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
        }

        /// <summary>MAJOR.MINOR of this build, taken from the VERSION file at build time.</summary>
        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor);
            }
        }

        static string ExePath => Process.GetCurrentProcess().MainModule.FileName;

        static HttpClient CreateClient(TimeSpan timeout)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeadMansSwitch/{Current}");
            return http;
        }

        /// <summary>Returns the newer release, or null if there is none or GitHub can't be reached.</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            try
            {
                using var http = CreateClient(TimeSpan.FromSeconds(10));
                using var json = await http.GetStreamAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                var release = (Release)new DataContractJsonSerializer(typeof(Release)).ReadObject(json);

                if (!Version.TryParse(release.TagName?.TrimStart('v', 'V'), out var latest)) return null;
                latest = new Version(latest.Major, latest.Minor);
                if (latest <= Current) return null;

                var asset = release.Assets?.FirstOrDefault(a => a.Name == AssetName);
                return asset == null ? null : new UpdateInfo { Version = latest.ToString(), DownloadUrl = asset.Url };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Downloads the update and puts it in place of the running exe, then
        /// starts it. Windows won't overwrite a running exe but will rename it,
        /// so the current one becomes DeadMansSwitch.exe.old and the new copy
        /// deletes it on its first start. The caller should exit afterwards.
        /// </summary>
        public static async Task InstallAsync(UpdateInfo update)
        {
            string self = ExePath, incoming = self + ".new", old = self + ".old";

            byte[] bytes;
            using (var http = CreateClient(TimeSpan.FromMinutes(2)))
                bytes = await http.GetByteArrayAsync(update.DownloadUrl);
            if (bytes.Length < 4096 || bytes[0] != 'M' || bytes[1] != 'Z')
                throw new InvalidDataException("The download is not a Windows program.");

            try
            {
                File.WriteAllBytes(incoming, bytes);
                if (File.Exists(old)) File.Delete(old);
                File.Move(self, old);
                try { File.Move(incoming, self); }
                catch { File.Move(old, self); throw; }
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException(
                    $"No permission to replace files in {Path.GetDirectoryName(self)}. Move the app to a folder you own, or update it by hand.");
            }
            finally
            {
                if (File.Exists(incoming)) File.Delete(incoming);
            }

            Process.Start(self);
        }

        /// <summary>Deletes the previous exe left behind by an update, once its process has exited.</summary>
        public static void CleanUpAfterUpdate()
        {
            string old = ExePath + ".old";
            if (!File.Exists(old)) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int i = 0; i < 20 && File.Exists(old); i++)
                {
                    try { File.Delete(old); }
                    catch (Exception) { Thread.Sleep(500); }
                }
            });
        }
    }
}
