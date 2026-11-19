using System.IO;
using System.Text;
using System.Text.Json;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 심즈 4 Mods 경로 자동 탐지 및 config.json / mods_manifest.json 경로 해결 서비스
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _baseDirectory;

    public ConfigService(string? baseDirectory = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? ResolveDefaultBaseDirectory()
            : baseDirectory;
    }

    public string BaseDirectory => _baseDirectory;
    public string ConfigFilePath => Path.Combine(_baseDirectory, "config.json");

    /// <summary>
    /// PC 내 심즈 4 Mods 폴더 후보 경로(OneDrive/로컬 문서)를 탐색하여 자동 탐지합니다.
    /// </summary>
    public static string? DetectSims4ModsFolder(string? userProfileOverride = null)
    {
        string userProfile = userProfileOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, "Documents", "Electronic Arts", "The Sims 4", "Mods"));
            candidates.Add(Path.Combine(userProfile, "OneDrive", "Documents", "Electronic Arts", "The Sims 4", "Mods"));
            candidates.Add(Path.Combine(userProfile, "OneDrive", "문서", "Electronic Arts", "The Sims 4", "Mods"));
            candidates.Add(Path.Combine(userProfile, "문서", "Electronic Arts", "The Sims 4", "Mods"));
        }

        if (userProfileOverride is null)
        {
            string myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(myDocs))
                candidates.Add(Path.Combine(myDocs, "Electronic Arts", "The Sims 4", "Mods"));
        }

        foreach (string cand in candidates)
        {
            if (Directory.Exists(cand))
                return cand;
        }

        return null;
    }

    public AppConfig LoadConfig()
    {
        AppConfig config = CreateDefaultConfig();

        if (File.Exists(ConfigFilePath))
        {
            try
            {
                string json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json);
                if (loaded is not null)
                    config = loaded;
            }
            catch
            {
                // 손상된 설정 파일은 기본값으로 폴백
            }
        }

        if (string.IsNullOrWhiteSpace(config.ModsFolder) || !Directory.Exists(config.ModsFolder))
        {
            string? detected = DetectSims4ModsFolder();
            if (!string.IsNullOrWhiteSpace(detected))
                config.ModsFolder = detected;
        }

        if (string.IsNullOrWhiteSpace(config.BackupFolder))
            config.BackupFolder = "backups";
        else if (Path.IsPathRooted(config.BackupFolder) && !Directory.Exists(config.BackupFolder))
        {
            // 타 PC 절대 경로 이식 시 상대 경로로 안전 폴백
            string? root = Path.GetPathRoot(config.BackupFolder);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                config.BackupFolder = "backups";
        }

        if (string.IsNullOrWhiteSpace(config.ManifestPath))
            config.ManifestPath = "mods_manifest.json";

        if (config.MaxConcurrentChecks < 1)
            config.MaxConcurrentChecks = 4;

        SaveConfig(config);
        return config;
    }

    public void SaveConfig(AppConfig config)
    {
        Directory.CreateDirectory(_baseDirectory);
        string json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(ConfigFilePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string ResolvePath(string? relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
            return _baseDirectory;

        return Path.IsPathRooted(relativeOrAbsolute)
            ? relativeOrAbsolute
            : Path.GetFullPath(Path.Combine(_baseDirectory, relativeOrAbsolute));
    }

    public string ResolveBackupFolder(AppConfig config) => ResolvePath(config.BackupFolder);
    public string ResolveManifestPath(AppConfig config) => ResolvePath(config.ManifestPath);

    public string ResolveCreatorOverridesPath(AppConfig config)
    {
        string manifestDir = Path.GetDirectoryName(ResolveManifestPath(config)) ?? _baseDirectory;
        return Path.Combine(manifestDir, "creator_overrides.json");
    }

    private static AppConfig CreateDefaultConfig()
    {
        return new AppConfig
        {
            ModsFolder = DetectSims4ModsFolder() ?? string.Empty,
            BackupFolder = "backups",
            AutoBackup = true,
            ManifestPath = "mods_manifest.json",
            MaxConcurrentChecks = 4
        };
    }

    private static string ResolveDefaultBaseDirectory()
    {
        string cwd = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(cwd, "mods_manifest.json")) || File.Exists(Path.Combine(cwd, "config.json")))
            return cwd;

        string appDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(appDir, "mods_manifest.json")) || File.Exists(Path.Combine(appDir, "config.json")))
            return appDir;

        return cwd;
    }
}
