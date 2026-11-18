using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DeadMansSwitch
{
    /// <summary>How this copy was installed, which decides how it updates.</summary>
    public enum InstallKind
    {
        WindowsInstalled,   // Inno Setup install: run the new installer silently
        WindowsPortable,    // a folder from the installer's portable mode: same, with /PORTABLE /DIR
        Deb,                // Debian/Ubuntu package: install the new .deb through pkexec
        Rpm,                // Fedora package: install the new .rpm through pkexec
        Flatpak,            // sandboxed: can't update itself, so it opens the download page
        Unmanaged,          // anything else (e.g. a build run from source): download page
    }

    /// <summary>The parts of a GitHub release the updater reads.</summary>
    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; }
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public GitHubAsset[] Assets { get; set; }
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; }
        [JsonPropertyName("url")] public string ApiUrl { get; set; }
    }

    public sealed class ReleaseNotes
    {
        public string Version { get; set; }
        public string Body { get; set; }
    }

    public sealed class UpdateInfo
    {
        public string Version { get; set; }

        /// <summary>The release page, for copies that can't install the update themselves.</summary>
        public string PageUrl { get; set; }

        /// <summary>The download for this kind of install: the public link, and the API link used with a token. Null if the release has none.</summary>
        public string AssetUrl { get; set; }
        public string AssetApiUrl { get; set; }
        public string AssetName { get; set; }

        /// <summary>Notes of every release newer than this build, newest first.</summary>
        public List<ReleaseNotes> Notes { get; set; } = new List<ReleaseNotes>();

        public bool CanInstall => AssetUrl != null;
    }

    /// <summary>Finds newer releases on GitHub and installs them the way this copy was installed.</summary>
    public static class Updater
    {
        const string Repo = "samschultzponsys/dead-mans-switch";
        public const string LinuxPackage = "dead-mans-switch";
        const string LinuxInstallDir = "/usr/lib/dead-mans-switch";

        // The file each kind of install updates from. Must match the names CI gives release assets.
        static readonly Dictionary<InstallKind, Regex> AssetNames = new Dictionary<InstallKind, Regex>
        {
            [InstallKind.WindowsInstalled] = new Regex(@"^DeadMansSwitch-Setup-(\d+\.\d+)\.exe$", RegexOptions.IgnoreCase),
            [InstallKind.WindowsPortable] = new Regex(@"^DeadMansSwitch-Setup-(\d+\.\d+)\.exe$", RegexOptions.IgnoreCase),
            [InstallKind.Deb] = new Regex(@"^dead-mans-switch_(\d+\.\d+)_amd64\.deb$"),
            [InstallKind.Rpm] = new Regex(@"^dead-mans-switch-(\d+\.\d+)-1\.x86_64\.rpm$"),
        };

        // Must match AppId in installer/DeadMansSwitch.iss; Inno Setup adds "_is1".
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A51-6B1E-4D7A-9C2E-5A1F0B7D3E44}_is1";

        /// <summary>Optional GitHub token (from Settings); needed if the repository is private.</summary>
        public static string Token { get; set; }

        /// <summary>MAJOR.MINOR of this build, taken from the VERSION file at build time.</summary>
        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return new Version(v.Major, v.Minor);
            }
        }

        static string ExePath => Environment.ProcessPath;

        static readonly Lazy<InstallKind> kind = new Lazy<InstallKind>(DetectKind);
        public static InstallKind Kind => kind.Value;

        static InstallKind DetectKind()
        {
            if (OperatingSystem.IsWindows()) return IsInstalledOnWindows() ? InstallKind.WindowsInstalled : InstallKind.WindowsPortable;
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID"))) return InstallKind.Flatpak;
            if (!AppContext.BaseDirectory.TrimEnd('/').Equals(LinuxInstallDir, StringComparison.Ordinal)) return InstallKind.Unmanaged;
            if (Succeeds("dpkg-query", "-W", LinuxPackage)) return InstallKind.Deb;
            if (Succeeds("rpm", "-q", LinuxPackage)) return InstallKind.Rpm;
            return InstallKind.Unmanaged;
        }

        static bool IsInstalledOnWindows()
        {
            if (!OperatingSystem.IsWindows()) return false;
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

        static bool Succeeds(string file, params string[] args)
        {
            try
            {
                var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) start.ArgumentList.Add(a);
                using var process = Process.Start(start);
                return process.WaitForExit(5000) && process.ExitCode == 0;
            }
            catch (Exception) { return false; }
        }

        static Version ParseTag(string tag) =>
            Version.TryParse(tag?.TrimStart('v', 'V'), out var v) ? new Version(v.Major, v.Minor) : null;

        static HttpClient CreateClient(TimeSpan timeout, string token, bool followRedirects = true)
        {
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
                var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30");
                return FindUpdate(JsonSerializer.Deserialize(json, AppJson.Default.GitHubReleaseArray), Current, Kind);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static UpdateInfo FindUpdate(IEnumerable<GitHubRelease> releases, Version current, InstallKind kind)
        {
            var newer = releases
                .Where(r => !r.Draft && !r.Prerelease)
                .Select(r => (Release: r, Version: ParseTag(r.TagName)))
                .Where(x => x.Version != null && x.Version > current)
                .OrderByDescending(x => x.Version)
                .ToList();
            if (newer.Count == 0) return null;

            var latest = newer[0];
            var asset = AssetNames.TryGetValue(kind, out var pattern)
                ? latest.Release.Assets?.FirstOrDefault(a => pattern.IsMatch(a.Name ?? ""))
                : null;
            return new UpdateInfo
            {
                Version = latest.Version.ToString(),
                PageUrl = latest.Release.HtmlUrl,
                AssetUrl = asset?.DownloadUrl,
                AssetApiUrl = asset?.ApiUrl,
                AssetName = asset?.Name,
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
        /// Installs the update. Windows: starts the new installer silently and
        /// returns; the caller exits and the installer reopens the app. Linux
        /// packages: installs through pkexec (the system asks for a password),
        /// starts the new version and returns; the caller exits.
        /// </summary>
        public static async Task InstallAsync(UpdateInfo update)
        {
            if (!update.CanInstall) throw new InvalidOperationException("This release has no download for this kind of install.");
            byte[] bytes = await Download(update);
            if (OperatingSystem.IsWindows() && (bytes[0] != 'M' || bytes[1] != 'Z'))
                throw new InvalidDataException("The download is not a Windows program.");
            string path = Path.Combine(Path.GetTempPath(), update.AssetName);
            await File.WriteAllBytesAsync(path, bytes);

            switch (Kind)
            {
                case InstallKind.WindowsInstalled:
                case InstallKind.WindowsPortable:
                    Process.Start(path, SetupArguments(Kind == InstallKind.WindowsInstalled, ExePath));
                    return;

                case InstallKind.Deb:
                    await RunPrivileged("/usr/bin/apt-get", "install", "-y", "--allow-downgrades", path);
                    break;

                case InstallKind.Rpm:
                    if (File.Exists("/usr/bin/dnf")) await RunPrivileged("/usr/bin/dnf", "install", "-y", path);
                    else await RunPrivileged("/usr/bin/rpm", "-U", path);
                    break;
            }
            Process.Start(new ProcessStartInfo("/usr/bin/" + LinuxPackage, "--updated") { UseShellExecute = false });
        }

        /// <summary>Runs a package manager as root, through polkit's password prompt.</summary>
        static async Task RunPrivileged(string program, params string[] args)
        {
            var start = new ProcessStartInfo("pkexec") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add(program);
            foreach (var a in args) start.ArgumentList.Add(a);
            Process process;
            try { process = Process.Start(start); }
            catch (Exception) { throw new InvalidOperationException("pkexec isn't installed, so the update can't ask for permission to install."); }
            using (process)
            {
                var errors = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode == 126 || process.ExitCode == 127)
                    throw new InvalidOperationException("The update wasn't authorised.");
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"Installing the update failed: {(await errors).Trim().Split('\n').LastOrDefault()}");
            }
        }

        /// <summary>
        /// Arguments for a silent Windows update. /SILENT shows only a progress window.
        /// A portable copy is updated in its own folder, and an exe with another
        /// name (an older download like DeadMansSwitch-1.3.exe) is replaced by
        /// DeadMansSwitch.exe and then removed.
        /// </summary>
        static string SetupArguments(bool installed, string exePath)
        {
            var args = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH";
            if (installed) return args;
            string dir = exePath.Substring(0, exePath.LastIndexOfAny(new[] { '\\', '/' }));
            string name = exePath.Substring(dir.Length + 1);
            args += $" /PORTABLE \"/DIR={dir}\"";
            if (!string.Equals(name, "DeadMansSwitch.exe", StringComparison.OrdinalIgnoreCase))
                args += $" \"/REPLACES={exePath}\"";
            return args;
        }

        static async Task<byte[]> Download(UpdateInfo update)
        {
            byte[] bytes;
            if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(update.AssetApiUrl))
            {
                using var http = CreateClient(TimeSpan.FromMinutes(5), token: null);
                bytes = await http.GetByteArrayAsync(update.AssetUrl);
            }
            else
            {
                // With a token, ask the API for the file. It answers with a redirect to
                // storage that must be followed without the token, which that host rejects.
                using var api = CreateClient(TimeSpan.FromMinutes(5), Token, followRedirects: false);
                var request = new HttpRequestMessage(HttpMethod.Get, update.AssetApiUrl);
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
            if (bytes.Length < 4096) throw new InvalidDataException("The download is too small to be the app.");
            return bytes;
        }

        /// <summary>True when an update started this copy; the old one may still be exiting.</summary>
        public static bool StartedByUpdate(string[] args) => args.Contains("--updated") || args.Contains("--replaces");

        /// <summary>Deletes an old exe a Windows update replaced (--replaces), retrying while its process exits.</summary>
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
                new GitHubRelease { TagName = "v1.3", HtmlUrl = "page", Body = "three", Assets = new[]
                {
                    new GitHubAsset { Name = "DeadMansSwitch-Setup-1.3.exe", DownloadUrl = "setup", ApiUrl = "api" },
                    new GitHubAsset { Name = "dead-mans-switch_1.3_amd64.deb", DownloadUrl = "deb" },
                    new GitHubAsset { Name = "dead-mans-switch-1.3-1.x86_64.rpm", DownloadUrl = "rpm" },
                    new GitHubAsset { Name = "DeadMansSwitch-1.3.flatpak", DownloadUrl = "flatpak" },
                } },
                new GitHubRelease { TagName = "v1.4", Body = "draft", Draft = true, Assets = new GitHubAsset[0] },
                new GitHubRelease { TagName = "v1.2", Body = "two", Assets = new GitHubAsset[0] },
                new GitHubRelease { TagName = "v1.1", Body = "one", Assets = new GitHubAsset[0] },
            };
            var u = FindUpdate(releases, new Version(1, 1), InstallKind.WindowsInstalled);
            if (u == null || u.Version != "1.3" || u.AssetUrl != "setup" || u.AssetApiUrl != "api" || u.PageUrl != "page"
                || string.Join(",", u.Notes.Select(n => n.Version)) != "1.3,1.2")
                throw new Exception("Update selection picked the wrong release or notes.");
            if (FindUpdate(releases, new Version(1, 1), InstallKind.Deb)?.AssetUrl != "deb"
                || FindUpdate(releases, new Version(1, 1), InstallKind.Rpm)?.AssetUrl != "rpm")
                throw new Exception("Linux packages don't pick their own download.");
            var flatpak = FindUpdate(releases, new Version(1, 1), InstallKind.Flatpak);
            if (flatpak == null || flatpak.CanInstall || flatpak.PageUrl != "page")
                throw new Exception("A Flatpak should be offered the download page, not an install.");
            if (FindUpdate(releases, new Version(1, 3), InstallKind.WindowsInstalled) != null)
                throw new Exception("Update offered when already on the latest version.");

            const string installedArgs = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH";
            if (SetupArguments(true, @"C:\Apps\DeadMansSwitch.exe") != installedArgs
                || SetupArguments(false, @"C:\Tools\DMS\DeadMansSwitch.exe") != installedArgs + " /PORTABLE \"/DIR=C:\\Tools\\DMS\""
                || SetupArguments(false, @"C:\Tools\DeadMansSwitch-1.3.exe") != installedArgs + " /PORTABLE \"/DIR=C:\\Tools\" \"/REPLACES=C:\\Tools\\DeadMansSwitch-1.3.exe\"")
                throw new Exception("Installer arguments for an update are wrong.");
        }
    }
}
