using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeadMansSwitch
{
    public sealed class ReleaseNotes
    {
        public string Version { get; set; }
        public string Body { get; set; }
    }

    public sealed class UpdateInfo
    {
        public string Version { get; set; }
        public string DownloadUrl { get; set; }

        /// <summary>Notes of every release newer than this build, newest first.</summary>
        public List<ReleaseNotes> Notes { get; set; } = new List<ReleaseNotes>();
    }

    /// <summary>
    /// Finds newer releases on GitHub and replaces the running exe with the new one.
    /// </summary>
    public static class Updater
    {
        const string Repo = "samschultzponsys/dead-mans-switch";

        // Releases ship DeadMansSwitch-X.Y.exe, plus a plain DeadMansSwitch.exe
        // for 1.1, whose updater only knows that name.
        static readonly Regex VersionedName = new Regex(@"^DeadMansSwitch-(\d+\.\d+)\.exe$", RegexOptions.IgnoreCase);
        const string PlainName = "DeadMansSwitch.exe";

        [DataContract]
        sealed class Release
        {
            [DataMember(Name = "tag_name")] public string TagName { get; set; }
            [DataMember(Name = "body")] public string Body { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
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

        /// <summary>True for file names like DeadMansSwitch-1.2.exe.</summary>
        public static bool IsVersionedName(string path) => VersionedName.IsMatch(Path.GetFileName(path));

        static Version ParseTag(string tag) =>
            Version.TryParse(tag?.TrimStart('v', 'V'), out var v) ? new Version(v.Major, v.Minor) : null;

        static HttpClient CreateClient(TimeSpan timeout)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeadMansSwitch/{Current}");
            return http;
        }

        /// <summary>Returns the newest release if it is newer than this build, or null (also when GitHub can't be reached).</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            try
            {
                using var http = CreateClient(TimeSpan.FromSeconds(10));
                using var json = await http.GetStreamAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30");
                var releases = (Release[])new DataContractJsonSerializer(typeof(Release[])).ReadObject(json);
                return FindUpdate(releases, Current);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static UpdateInfo FindUpdate(IEnumerable<Release> releases, Version current)
        {
            var newer = releases
                .Where(r => !r.Draft && !r.Prerelease)
                .Select(r => (Release: r, Version: ParseTag(r.TagName)))
                .Where(x => x.Version != null && x.Version > current)
                .OrderByDescending(x => x.Version)
                .ToList();
            if (newer.Count == 0) return null;

            var latest = newer[0];
            var assets = latest.Release.Assets ?? new Asset[0];
            var asset = assets.FirstOrDefault(a => VersionedName.IsMatch(a.Name ?? ""))
                     ?? assets.FirstOrDefault(a => string.Equals(a.Name, PlainName, StringComparison.OrdinalIgnoreCase));
            if (asset == null) return null;

            return new UpdateInfo
            {
                Version = latest.Version.ToString(),
                DownloadUrl = asset.Url,
                Notes = newer.Select(x => new ReleaseNotes { Version = x.Version.ToString(), Body = x.Release.Body ?? "" }).ToList(),
            };
        }

        /// <summary>
        /// Downloads the update and starts it; the caller should exit afterwards.
        /// A versioned exe (DeadMansSwitch-1.1.exe) is replaced by a new file
        /// with the new version in its name, which deletes the old file once
        /// this process has exited. Any other name is kept: Windows won't
        /// overwrite a running exe but will rename it, so the current one
        /// becomes .old and is deleted by the new copy on its first start.
        /// </summary>
        public static async Task InstallAsync(UpdateInfo update)
        {
            string self = ExePath, dir = Path.GetDirectoryName(self);

            byte[] bytes;
            using (var http = CreateClient(TimeSpan.FromMinutes(2)))
                bytes = await http.GetByteArrayAsync(update.DownloadUrl);
            if (bytes.Length < 4096 || bytes[0] != 'M' || bytes[1] != 'Z')
                throw new InvalidDataException("The download is not a Windows program.");

            try
            {
                if (IsVersionedName(self))
                {
                    string target = Path.Combine(dir, $"DeadMansSwitch-{update.Version}.exe");
                    File.WriteAllBytes(target, bytes);
                    Process.Start(target, $"--replaces \"{self}\"");
                    return;
                }

                string incoming = self + ".new", old = self + ".old";
                try
                {
                    File.WriteAllBytes(incoming, bytes);
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(self, old);
                    try { File.Move(incoming, self); }
                    catch { File.Move(old, self); throw; }
                }
                finally
                {
                    if (File.Exists(incoming)) File.Delete(incoming);
                }
                Process.Start(self, "--updated");
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException(
                    $"No permission to write to {dir}. Move the app to a folder you own, or update it by hand.");
            }
        }

        /// <summary>True when this process was started by an update and the old copy may still be exiting.</summary>
        public static bool StartedByUpdate(string[] args) => args.Contains("--replaces") || args.Contains("--updated");

        /// <summary>Deletes what an update left behind, retrying while the old process exits.</summary>
        public static void CleanUpAfterUpdate(string[] args)
        {
            var leftovers = new List<string> { ExePath + ".old" };
            int i = Array.IndexOf(args, "--replaces");
            if (i >= 0 && i + 1 < args.Length) leftovers.Add(args[i + 1]);

            string dir = Path.GetDirectoryName(ExePath);
            leftovers = leftovers.Where(path =>
                    File.Exists(path)
                    && !string.Equals(Path.GetFullPath(path), ExePath, StringComparison.OrdinalIgnoreCase)
                    // only ever delete an old copy of this app, next to this exe
                    && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), dir, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(path).StartsWith("DeadMansSwitch", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (leftovers.Count == 0) return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (var path in leftovers)
                    for (int attempt = 0; attempt < 30 && File.Exists(path); attempt++)
                    {
                        try { File.Delete(path); }
                        catch (Exception) { Thread.Sleep(500); }
                    }
            });
        }

        /// <summary>Checks release selection against a canned GitHub response (used by --selftest).</summary>
        internal static void SelfTest()
        {
            var releases = new[]
            {
                new Release { TagName = "v1.3", Body = "three", Assets = new[] { new Asset { Name = "DeadMansSwitch.exe", Url = "plain" },
                                                                                 new Asset { Name = "DeadMansSwitch-1.3.exe", Url = "versioned" } } },
                new Release { TagName = "v1.4", Body = "draft", Draft = true, Assets = new Asset[0] },
                new Release { TagName = "v1.2", Body = "two", Assets = new[] { new Asset { Name = "DeadMansSwitch.exe", Url = "old" } } },
                new Release { TagName = "v1.1", Body = "one", Assets = new Asset[0] },
            };
            var u = FindUpdate(releases, new Version(1, 1));
            if (u == null || u.Version != "1.3" || u.DownloadUrl != "versioned"
                || string.Join(",", u.Notes.Select(n => n.Version)) != "1.3,1.2")
                throw new Exception("Update selection picked the wrong release or notes.");
            if (FindUpdate(releases, new Version(1, 3)) != null)
                throw new Exception("Update offered when already on the latest version.");
            if (!IsVersionedName(@"C:\x\DeadMansSwitch-1.2.exe") || IsVersionedName(@"C:\x\DeadMansSwitch.exe"))
                throw new Exception("Versioned file name detection is wrong.");
        }
    }
}
