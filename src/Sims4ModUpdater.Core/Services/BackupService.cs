using System.IO;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 심즈 4 모드 업데이트 전 자동 백업 생성·조회·복원·삭제 관리 서비스
/// </summary>
public sealed class BackupService
{
    private readonly string _modsFolder;
    private readonly string _backupRoot;

    public BackupService(string modsFolder, string backupRoot)
    {
        _modsFolder = modsFolder;
        _backupRoot = backupRoot;
    }

    public string BackupRoot => _backupRoot;

    public string CreateBackupSession(string label = "manual")
    {
        Directory.CreateDirectory(_backupRoot);
        string safeLabel = Regex.Replace((label ?? "manual").Trim(), @"[<>:""/\\|?*]+", "_");
        if (string.IsNullOrWhiteSpace(safeLabel))
            safeLabel = "manual";

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string sessionDir = Path.Combine(_backupRoot, $"{safeLabel}_{timestamp}");
        Directory.CreateDirectory(sessionDir);
        return sessionDir;
    }

    public void BackupItem(string sourcePath, string backupSessionDir)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return;

        string relativeSubPath = ResolveRelativeBackupPath(sourcePath);

        if (File.Exists(sourcePath))
        {
            string dest = Path.Combine(backupSessionDir, relativeSubPath);
            string? destParent = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destParent))
                Directory.CreateDirectory(destParent);
            File.Copy(sourcePath, dest, overwrite: true);
        }
        else if (Directory.Exists(sourcePath))
        {
            string destDir = string.IsNullOrEmpty(relativeSubPath) || relativeSubPath == "."
                ? backupSessionDir
                : Path.Combine(backupSessionDir, relativeSubPath);
            CopyDirectoryRecursive(sourcePath, destDir);
        }
    }

    private string ResolveRelativeBackupPath(string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(_modsFolder))
        {
            try
            {
                string modsFull = Path.GetFullPath(_modsFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string srcFull = Path.GetFullPath(sourcePath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(modsFull, srcFull, StringComparison.OrdinalIgnoreCase))
                    return ".";

                if (srcFull.StartsWith(modsFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return srcFull[(modsFull.Length + 1)..];
            }
            catch
            {
                // 잘못된 경로 문자열이면 파일/폴더명만 사용
            }
        }

        return Path.GetFileName(sourcePath);
    }

    public List<BackupSessionInfo> ListBackups()
    {
        var results = new List<BackupSessionInfo>();
        if (string.IsNullOrWhiteSpace(_backupRoot) || !Directory.Exists(_backupRoot))
            return results;

        foreach (string dir in Directory.EnumerateDirectories(_backupRoot))
        {
            var di = new DirectoryInfo(dir);
            int fileCount = 0;
            long totalBytes = 0;
            var topItems = new List<string>();

            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir))
                    topItems.Add(Path.GetFileName(entry));

                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    fileCount++;
                    totalBytes += new FileInfo(f).Length;
                }
            }
            catch
            {
                // 접근 불가 파일 무시
            }

            results.Add(new BackupSessionInfo
            {
                Name = di.Name,
                FullPath = dir,
                CreatedAt = di.LastWriteTime,
                FileCount = fileCount,
                TotalBytes = totalBytes,
                TopLevelItems = topItems.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        return results.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public int RestoreBackup(string backupSessionDir)
    {
        if (string.IsNullOrWhiteSpace(backupSessionDir) || !Directory.Exists(backupSessionDir))
            throw new DirectoryNotFoundException($"백업 경로를 찾을 수 없습니다: {backupSessionDir}");
        if (string.IsNullOrWhiteSpace(_modsFolder))
            throw new InvalidOperationException("Mods 폴더가 설정되어 있지 않습니다.");

        Directory.CreateDirectory(_modsFolder);
        int restoredCount = 0;

        foreach (string item in Directory.EnumerateFileSystemEntries(backupSessionDir))
        {
            string name = Path.GetFileName(item);
            string target = Path.Combine(_modsFolder, name);
            if (Directory.Exists(item))
            {
                restoredCount += CopyDirectoryRecursive(item, target);
            }
            else if (File.Exists(item))
            {
                File.Copy(item, target, overwrite: true);
                restoredCount++;
            }
        }

        return restoredCount;
    }

    public bool DeleteBackup(string backupSessionDir)
    {
        if (string.IsNullOrWhiteSpace(backupSessionDir) || !Directory.Exists(backupSessionDir))
            return false;

        string rootFull = Path.GetFullPath(_backupRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string targetFull = Path.GetFullPath(backupSessionDir);
        if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("백업 루트 외부의 폴더는 삭제할 수 없습니다.");

        Directory.Delete(targetFull, recursive: true);
        return true;
    }

    private static int CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        int copied = 0;
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
            copied++;
        }

        foreach (string subDir in Directory.EnumerateDirectories(sourceDir))
        {
            string destSub = Path.Combine(destDir, Path.GetFileName(subDir));
            copied += CopyDirectoryRecursive(subDir, destSub);
        }

        return copied;
    }
}
