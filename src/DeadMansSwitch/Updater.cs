using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

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

        /// <summary>The release's installer: the public download link, and the API link used with a token.</summary>
        public string SetupUrl { get; set; }
        public string SetupApiUrl { get; set; }

        /// <summary>Notes of every release newer than this build, newest first.</summary>
        public List<ReleaseNotes> Notes { get; set; } = new List<ReleaseNotes>();
    }

    /// <summary>
    /// Finds newer releases on GitHub and installs them by running the release's
    /// installer silently, set to update this copy whether it was installed or
    /// set up as portable.
    /// </summary>
    public static class Updater
    {
        const string Repo = "samschultzponsys/dead-mans-switch";
        const string ExeName = "DeadMansSwitch.exe";
        static readonly Regex SetupName = new Regex(@"^DeadMansSwitch-Setup-(\d+\.\d+)\.exe$", RegexOptions.IgnoreCase);

        // Must match AppId in installer/DeadMansSwitch.iss; Inno Setup adds "_is1".
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6B1E-4D7A-9C2E-5A1F0B7D3E44}_is1";

        /// <summary>Optional GitHub token (from Settings); needed if the repository is private.</summary>
        public static string Token { get; set; }

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
            [DataMember(Name = "browser_download_url")] public string DownloadUrl { get; set; }
            [DataMember(Name = "url")] public string ApiUrl { get; set; }
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

        /// <summary>True when this exe is the one the installer installed (not a portable copy).</summary>
        public static bool IsInstalled
        {
            get
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                    var location = key?.GetValue("InstallLocation") as string;
                    return !string.IsNullOrEmpty(location) && string.Equals(
                        Path.GetFullPath(location).TrimEnd('\\'), Path.GetDirectoryName(ExePath).TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception) { return false; }
            }
        }

        static Version ParseTag(string tag) =>
            Version.TryParse(tag?.TrimStart('v', 'V'), out var v) ? new Version(v.Major, v.Minor) : null;

        static HttpClient CreateClient(TimeSpan timeout, string token, bool followRedirects = true)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = followRedirects }) { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeadMansSwitch/{Current}");
            if (!string.IsNullOrEmpty(token))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }

        /// <summary>Returns the newest release if it is newer than this build, or null (also when GitHub can't be reached).</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            try
            {
                using var http = CreateClient(TimeSpan.FromSeconds(10), Token);
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
            var setup = latest.Release.Assets?.FirstOrDefault(a => SetupName.IsMatch(a.Name ?? ""));
            if (setup == null) return null;

            return new UpdateInfo
            {
                Version = latest.Version.ToString(),
                SetupUrl = setup.DownloadUrl,
                SetupApiUrl = setup.ApiUrl,
                Notes = newer.Select(x => new ReleaseNotes { Version = x.Version.ToString(), Body = x.Release.Body ?? "" }).ToList(),
            };
        }

        /// <summary>Checks a token against the repository; returns null if it works, or what's wrong.</summary>
        public static async Task<string> TestTokenAsync(string token)
        {
            try
            {
                using var http = CreateClient(TimeSpan.FromSeconds(10), token);
                using var response = await http.GetAsync($"https://api.github.com/repos/{Repo}");
                if (response.IsSuccessStatusCode) return null;
                return response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "GitHub didn't accept that token. Check it was copied in full and hasn't expired.",
                    HttpStatusCode.NotFound => $"That token can't see {Repo}. Give it read access to the repository's contents.",
                    _ => $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.",
                };
            }
            catch (Exception ex)
            {
                return "Couldn't reach GitHub to check the token: " + ex.Message;
            }
        }

        /// <summary>
        /// Downloads the update's installer and starts it silently; the caller
        /// should exit right away. The installer closes this app if it's still
        /// running, replaces the exe and reopens it.
        /// </summary>
        public static async Task InstallAsync(UpdateInfo update)
        {
            byte[] setup = await DownloadSetup(update);
            string setupPath = Path.Combine(Path.GetTempPath(), $"DeadMansSwitch-Setup-{update.Version}.exe");
            File.WriteAllBytes(setupPath, setup);
            Process.Start(setupPath, SetupArguments(IsInstalled, ExePath));
        }

        /// <summary>
        /// Arguments for a silent update. /SILENT shows only a progress window.
        /// A portable copy is updated in its own folder, and an exe with another
        /// name (an older download like DeadMansSwitch-1.3.exe) is replaced by
        /// DeadMansSwitch.exe and then removed.
        /// </summary>
        static string SetupArguments(bool installed, string exePath)
        {
            var args = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH";
            if (installed) return args;
            args += $" /PORTABLE \"/DIR={Path.GetDirectoryName(exePath)}\"";
            if (!string.Equals(Path.GetFileName(exePath), ExeName, StringComparison.OrdinalIgnoreCase))
                args += $" \"/REPLACES={exePath}\"";
            return args;
        }

        static async Task<byte[]> DownloadSetup(UpdateInfo update)
        {
            byte[] bytes;
            if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(update.SetupApiUrl))
            {
                using var http = CreateClient(TimeSpan.FromMinutes(5), token: null);
                bytes = await http.GetByteArrayAsync(update.SetupUrl);
            }
            else
            {
                // With a token, ask the API for the file. It answers with a redirect to
                // storage that must be followed without the token, which that host rejects.
                using var api = CreateClient(TimeSpan.FromMinutes(5), Token, followRedirects: false);
                var request = new HttpRequestMessage(HttpMethod.Get, update.SetupApiUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                using var response = await api.SendAsync(request);
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null)
                {
                    using var storage = CreateClient(TimeSpan.FromMinutes(5), token: null);
                    bytes = await storage.GetByteArrayAsync(response.Headers.Location);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    bytes = await response.Content.ReadAsByteArrayAsync();
                }
            }
            if (bytes.Length < 4096 || bytes[0] != 'M' || bytes[1] != 'Z')
                throw new InvalidDataException("The download is not a Windows program.");
            return bytes;
        }

        /// <summary>True when the installer started this copy after an update; the old one may still be exiting.</summary>
        public static bool StartedByUpdate(string[] args) => args.Contains("--updated") || args.Contains("--replaces");

        /// <summary>Deletes an old exe an update replaced (--replaces), retrying while its process exits.</summary>
        public static void CleanUpAfterUpdate(string[] args)
        {
            int i = Array.IndexOf(args, "--replaces");
            if (i < 0 || i + 1 >= args.Length) return;
            string old = Path.GetFullPath(args[i + 1]);

            // Only ever delete an old copy of this app, next to this exe.
            if (!File.Exists(old)
                || string.Equals(old, ExePath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(old), Path.GetDirectoryName(ExePath), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(old).StartsWith("DeadMansSwitch", StringComparison.OrdinalIgnoreCase)
                || !old.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                for (int attempt = 0; attempt < 30 && File.Exists(old); attempt++)
                {
                    try { File.Delete(old); }
                    catch (Exception) { Thread.Sleep(500); }
                }
            });
        }

        /// <summary>Checks release selection and installer arguments (used by --selftest).</summary>
        internal static void SelfTest()
        {
            var releases = new[]
            {
                new Release { TagName = "v1.3", Body = "three", Assets = new[] { new Asset { Name = "DeadMansSwitch-Setup-1.3.exe", DownloadUrl = "setup", ApiUrl = "api" } } },
                new Release { TagName = "v1.4", Body = "draft", Draft = true, Assets = new Asset[0] },
                new Release { TagName = "v1.2", Body = "two", Assets = new Asset[0] },
                new Release { TagName = "v1.1", Body = "one", Assets = new Asset[0] },
            };
            var u = FindUpdate(releases, new Version(1, 1));
            if (u == null || u.Version != "1.3" || u.SetupUrl != "setup" || u.SetupApiUrl != "api"
                || string.Join(",", u.Notes.Select(n => n.Version)) != "1.3,1.2")
                throw new Exception("Update selection picked the wrong release or notes.");
            if (FindUpdate(releases, new Version(1, 3)) != null)
                throw new Exception("Update offered when already on the latest version.");

            const string installedArgs = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH";
            if (SetupArguments(true, @"C:\Apps\DeadMansSwitch.exe") != installedArgs
                || SetupArguments(false, @"C:\Tools\DMS\DeadMansSwitch.exe") != installedArgs + " /PORTABLE \"/DIR=C:\\Tools\\DMS\""
                || SetupArguments(false, @"C:\Tools\DeadMansSwitch-1.3.exe") != installedArgs + " /PORTABLE \"/DIR=C:\\Tools\" \"/REPLACES=C:\\Tools\\DeadMansSwitch-1.3.exe\"")
                throw new Exception("Installer arguments for an update are wrong.");
        }
    }
}
