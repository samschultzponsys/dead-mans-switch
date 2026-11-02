using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace DeadMansSwitch
{
    public enum EndAction { Shutdown, Restart, Hibernate }

    /// <summary>
    /// User settings, stored as JSON in %APPDATA%\DeadMansSwitch\settings.json,
    /// or beside the exe for a portable copy.
    /// Fields are nullable because the serializer leaves missing keys null
    /// (older files only have CheckMinutes and WarnSeconds); <see cref="Normalize"/>
    /// fills those and any out-of-range values with defaults.
    /// </summary>
    [DataContract]
    public sealed class Settings
    {
        public const int MinCheckMinutes = 1, MaxCheckMinutes = 1440;
        public const int MinWarnSeconds = 10, MaxWarnSeconds = 600;

        static readonly string AppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeadMansSwitch", "settings.json");

        static string ExeDir => Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

        /// <summary>True when the installer set this copy up as portable (it leaves a marker file beside the exe).</summary>
        public static bool IsPortable => File.Exists(Path.Combine(ExeDir, "DeadMansSwitch.portable"));

        public static readonly string FilePath = IsPortable ? Path.Combine(ExeDir, "settings.json") : AppDataPath;

        // Ties the encrypted token to this app as well as to the Windows account.
        static readonly byte[] TokenEntropy = Encoding.UTF8.GetBytes("DeadMansSwitch.GitHubToken");

        [DataMember(Order = 1)] public int? CheckMinutes { get; set; }
        [DataMember(Order = 2)] public int? WarnSeconds { get; set; }
        [DataMember(Order = 3)] public string Action { get; set; }
        [DataMember(Order = 4)] public bool? PlaySound { get; set; }
        [DataMember(Order = 5)] public bool? CheckForUpdates { get; set; }

        /// <summary>Hide to the tray instead of quitting when closed. Null until the user is first asked.</summary>
        [DataMember(Order = 6)] public bool? CloseToTray { get; set; }

        /// <summary>The installed version whose changelog was last shown on start.</summary>
        [DataMember(Order = 7)] public string SeenChangelogFor { get; set; }

        /// <summary>The newest available release whose notes were already shown.</summary>
        [DataMember(Order = 8)] public string SeenReleaseNotesFor { get; set; }

        /// <summary>Install a newer version as soon as the app opens.</summary>
        [DataMember(Order = 9)] public bool? AutoInstallUpdates { get; set; }

        /// <summary>The GitHub token, encrypted for the current Windows account (DPAPI). Use <see cref="GitHubToken"/>.</summary>
        [DataMember(Order = 10)] public string GitHubTokenProtected { get; set; }

        /// <summary>
        /// Optional GitHub token for update checks and downloads. Null when none is
        /// saved, or when the saved one can't be decrypted (another account or PC).
        /// </summary>
        public string GitHubToken
        {
            get
            {
                if (string.IsNullOrEmpty(GitHubTokenProtected)) return null;
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(GitHubTokenProtected), TokenEntropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch (Exception) { return null; }
            }
            set => GitHubTokenProtected = string.IsNullOrEmpty(value) ? null : Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(value), TokenEntropy, DataProtectionScope.CurrentUser));
        }

        public Settings() => Normalize();

        public EndAction EndAction
        {
            get => Enum.TryParse(Action, true, out EndAction a) ? a : EndAction.Shutdown;
            set => Action = value.ToString();
        }

        public static bool ValidCheckMinutes(int m) => m >= MinCheckMinutes && m <= MaxCheckMinutes;
        public static bool ValidWarnSeconds(int s) => s >= MinWarnSeconds && s <= MaxWarnSeconds;

        public void Normalize()
        {
            if (!(CheckMinutes is int m && ValidCheckMinutes(m))) CheckMinutes = 5;
            if (!(WarnSeconds is int s && ValidWarnSeconds(s))) WarnSeconds = 60;
            EndAction = EndAction;   // rewrites unknown names as Shutdown
            PlaySound ??= true;
            CheckForUpdates ??= true;
            AutoInstallUpdates ??= true;
        }

        public static Settings Load(string path = null)
        {
            // A portable copy's first run starts from the settings it had before.
            if (path == null && !File.Exists(FilePath) && File.Exists(AppDataPath)) path = AppDataPath;
            try
            {
                using var stream = File.OpenRead(path ?? FilePath);
                var settings = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                settings.Normalize();
                return settings;
            }
            catch (Exception)
            {
                return new Settings();   // missing or unreadable file: defaults
            }
        }

        public void Save(string path = null)
        {
            path ??= FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using var stream = File.Create(path);
            using var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, ownsStream: false, indent: true);
            new DataContractJsonSerializer(typeof(Settings)).WriteObject(writer, this);
        }
    }
}
