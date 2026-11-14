using System.Text.Json.Serialization;

namespace Sims4ModUpdater.Core.Models;

/// <summary>
/// 사용자 환경 설정 모델 (config.json)
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("mods_folder")]
    public string ModsFolder { get; set; } = string.Empty;

    [JsonPropertyName("backup_folder")]
    public string BackupFolder { get; set; } = "backups";

    [JsonPropertyName("auto_backup")]
    public bool AutoBackup { get; set; } = true;

    [JsonPropertyName("manifest_path")]
    public string ManifestPath { get; set; } = "mods_manifest.json";

    [JsonPropertyName("max_concurrent_checks")]
    public int MaxConcurrentChecks { get; set; } = 4;
}
