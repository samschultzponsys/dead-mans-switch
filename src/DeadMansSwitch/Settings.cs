using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadMansSwitch
{
    public enum EndAction { Shutdown, Restart, Hibernate }

    /// <summary>JSON code generated at build time, so publishing can trim unused framework code safely.</summary>
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Settings))]
    [JsonSerializable(typeof(GitHubRelease[]))]
    internal partial class AppJson : JsonSerializerContext { }

    /// <summary>
    /// User settings, stored as JSON: %APPDATA%\DeadMansSwitch\settings.json on
    /// Windows, ~/.config/DeadMansSwitch/settings.json on Linux (inside a
    /// Flatpak, its own ~/.var/app folder), or beside the exe for a portable
    /// Windows copy. Fields are nullable so a key missing from an older file
    /// can be told apart; <see cref="Normalize"/> fills it in.
    /// </summary>
    public sealed class Settings
    {
        public const int MinCheckMinutes = 1, MaxCheckMinutes = 1440;
        public const int MinWarnSeconds = 10, MaxWarnSeconds = 600;

        static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeadMansSwitch", "settings.json");

        /// <summary>True when the Windows installer set this copy up as portable (it leaves a marker file beside the exe).</summary>
        public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "DeadMansSwitch.portable"));

        public static readonly string FilePath = IsPortable ? Path.Combine(AppContext.BaseDirectory, "settings.json") : ConfigPath;

        public int? CheckMinutes { get; set; }
        public int? WarnSeconds { get; set; }
        public string Action { get; set; }
        public bool? PlaySound { get; set; }
        public bool? CheckForUpdates { get; set; }

        /// <summary>Hide to the tray instead of quitting when closed. Null until the user is first asked.</summary>
        public bool? CloseToTray { get; set; }

        /// <summary>The installed version whose changelog was last shown on start.</summary>
        public string SeenChangelogFor { get; set; }

        /// <summary>The newest available release whose notes were already shown.</summary>
        public string SeenReleaseNotesFor { get; set; }

        /// <summary>Install a newer version as soon as the app opens.</summary>
        public bool? AutoInstallUpdates { get; set; }

        /// <summary>The GitHub token as stored (see <see cref="Platform.ProtectToken"/>). Use <see cref="GitHubToken"/>.</summary>
        public string GitHubTokenProtected { get; set; }

        /// <summary>
        /// Optional GitHub token for update checks and downloads. Null when none is
        /// saved, or when the saved one can't be read here (another account or PC).
        /// </summary>
        [JsonIgnore]
        public string GitHubToken
        {
            get => Platform.UnprotectToken(GitHubTokenProtected);
            set => GitHubTokenProtected = string.IsNullOrEmpty(value) ? null : Platform.ProtectToken(value);
        }

        public Settings() => Normalize();

        [JsonIgnore]
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
            if (path == null && !File.Exists(FilePath) && File.Exists(ConfigPath)) path = ConfigPath;
            try
            {
                var settings = JsonSerializer.Deserialize(File.ReadAllText(path ?? FilePath), AppJson.Default.Settings) ?? new Settings();
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
            File.WriteAllText(path, JsonSerializer.Serialize(this, AppJson.Default.Settings));
            Platform.RestrictToOwner(path);   // it can hold the GitHub token
        }
    }
}
