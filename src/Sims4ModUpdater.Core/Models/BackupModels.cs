namespace Sims4ModUpdater.Core.Models;

/// <summary>
/// 백업 세션 정보
/// </summary>
public sealed class BackupSessionInfo
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<string> TopLevelItems { get; set; } = new();
}
