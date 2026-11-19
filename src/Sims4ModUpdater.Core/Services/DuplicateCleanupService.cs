using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// Mods 폴더 내 중복 모드 파일(.package / .ts4script)을 탐지하고
/// 우선순위(Overrides > 정리된 제작자 폴더 > 원본 파일명 > 최신 수정본)에 따라 1개만 남기고
/// 백업 후 일괄 삭제하는 서비스
/// </summary>
public sealed class DuplicateCleanupService
{
    private static readonly Regex CopySuffixRegex = new(
        @"(?:\s*\(\d+\)|[_\-\s]+(?:old|copy|backup)|복사본|사본)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly EnumerationOptions RecursiveEnumOpts = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System
    };

    public DuplicateCleanupPlan BuildCleanupPlan(
        string modsFolder,
        bool includeContentHashDuplicates = true)
    {
        if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder))
        {
            return new DuplicateCleanupPlan
            {
                ModsFolder = modsFolder ?? string.Empty,
                IncludeContentHashDuplicates = includeContentHashDuplicates
            };
        }

        string modsFull = Path.GetFullPath(modsFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var allModFiles = new List<(string FullPath, string RelativePath, string FileName, long Length, DateTime LastWriteUtc)>();
        foreach (string f in Directory.EnumerateFiles(modsFull, "*", RecursiveEnumOpts))
        {
            string fn = Path.GetFileName(f);
            if (CreatorOrganizerService.IsResourceCfgFile(fn))
                continue;

            string ext = Path.GetExtension(fn);
            if (!ext.Equals(".package", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".ts4script", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var fi = new FileInfo(f);
                if (!fi.Exists)
                    continue;
                string full = Path.GetFullPath(f);
                allModFiles.Add((full, Path.GetRelativePath(modsFull, full), fn, fi.Length, fi.LastWriteTimeUtc));
            }
            catch
            {
                // 접근 불가 파일 건너뜀
            }
        }

        var items = new List<DuplicateDeletePlanItem>();
        var scheduledDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shaCache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        int groupCount = 0;

        byte[]? TryGetHash(string path)
        {
            if (shaCache.TryGetValue(path, out byte[]? cached))
                return cached;
            try
            {
                byte[] h = CreatorOrganizerService.ComputeFileSha256(path);
                shaCache[path] = h;
                return h;
            }
            catch
            {
                return null;
            }
        }

        // 1) 동일 파일명 그룹
        foreach (var group in allModFiles.GroupBy(f => f.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToList();
            if (members.Count < 2)
                continue;

            groupCount++;
            members.Sort((a, b) => CompareKeepPriority(a, b, modsFull));
            var keep = members[0];
            byte[]? keepHash = TryGetHash(keep.FullPath);

            for (int i = 1; i < members.Count; i++)
            {
                var del = members[i];
                bool exactMatch = false;
                if (del.Length == keep.Length && keepHash is not null)
                {
                    byte[]? delHash = TryGetHash(del.FullPath);
                    exactMatch = delHash is not null && CryptographicOperations.FixedTimeEquals(keepHash, delHash);
                }

                scheduledDeletes.Add(del.FullPath);
                items.Add(new DuplicateDeletePlanItem
                {
                    IsSelected = true,
                    GroupName = keep.FileName,
                    MatchType = exactMatch ? "동일 파일명 (내용 100% 일치)" : "동일 파일명 (크기·내용 다름 ⚠️)",
                    IsExactContentMatch = exactMatch,
                    KeepPath = keep.FullPath,
                    DeletePath = del.FullPath,
                    KeepRelativePath = keep.RelativePath,
                    DeleteRelativePath = del.RelativePath,
                    FileSizeBytes = del.Length,
                    KeepModifiedUtc = keep.LastWriteUtc,
                    DeleteModifiedUtc = del.LastWriteUtc
                });
            }
        }

        // 2) 파일명이 달라도 내용(SHA-256)이 100% 동일한 그룹
        if (includeContentHashDuplicates)
        {
            var remaining = allModFiles
                .Where(f => !scheduledDeletes.Contains(f.FullPath) && f.Length > 0)
                .ToList();

            foreach (var lenGroup in remaining.GroupBy(f => f.Length))
            {
                var sameSize = lenGroup.ToList();
                if (sameSize.Count < 2)
                    continue;

                var byHash = new Dictionary<string, List<(string FullPath, string RelativePath, string FileName, long Length, DateTime LastWriteUtc)>>(StringComparer.Ordinal);
                foreach (var f in sameSize)
                {
                    byte[]? h = TryGetHash(f.FullPath);
                    if (h is null)
                        continue;
                    string key = Convert.ToHexString(h);
                    if (!byHash.TryGetValue(key, out var list))
                    {
                        list = new List<(string, string, string, long, DateTime)>(2);
                        byHash[key] = list;
                    }
                    list.Add(f);
                }

                foreach (var hashList in byHash.Values)
                {
                    if (hashList.Count < 2)
                        continue;

                    groupCount++;
                    hashList.Sort((a, b) => CompareKeepPriority(a, b, modsFull));
                    var keep = hashList[0];

                    for (int i = 1; i < hashList.Count; i++)
                    {
                        var del = hashList[i];
                        scheduledDeletes.Add(del.FullPath);
                        items.Add(new DuplicateDeletePlanItem
                        {
                            IsSelected = true,
                            GroupName = keep.FileName,
                            MatchType = "내용 100% 동일 (다른 파일명)",
                            IsExactContentMatch = true,
                            KeepPath = keep.FullPath,
                            DeletePath = del.FullPath,
                            KeepRelativePath = keep.RelativePath,
                            DeleteRelativePath = del.RelativePath,
                            FileSizeBytes = del.Length,
                            KeepModifiedUtc = keep.LastWriteUtc,
                            DeleteModifiedUtc = del.LastWriteUtc
                        });
                    }
                }
            }
        }

        items.Sort((a, b) =>
        {
            int cmp = string.Compare(a.DeleteRelativePath, b.DeleteRelativePath, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.GroupName, b.GroupName, StringComparison.OrdinalIgnoreCase);
        });

        return new DuplicateCleanupPlan
        {
            ModsFolder = modsFull,
            IncludeContentHashDuplicates = includeContentHashDuplicates,
            TotalFilesScanned = allModFiles.Count,
            DuplicateGroupCount = groupCount,
            Items = items
        };
    }

    public DuplicateCleanupResult ExecuteCleanup(
        DuplicateCleanupPlan plan,
        IReadOnlyCollection<DuplicateDeletePlanItem> selectedItems,
        BackupService? backupService = null,
        bool createBackup = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectedItems);

        string modsFull = Path.GetFullPath(plan.ModsFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootResourceCfg = Path.GetFullPath(Path.Combine(modsFull, "Resource.cfg"));

        var targets = selectedItems
            .Where(i => !string.IsNullOrWhiteSpace(i.DeletePath) &&
                        !string.Equals(Path.GetFullPath(i.DeletePath), Path.GetFullPath(i.KeepPath), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var keepPaths = targets
            .Select(i => Path.GetFullPath(i.KeepPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? backupSessionDir = null;
        if (targets.Count > 0 && createBackup && backupService is not null)
        {
            backupSessionDir = backupService.CreateBackupSession("delete_duplicates");
            foreach (DuplicateDeletePlanItem item in targets)
            {
                if (File.Exists(item.DeletePath))
                    backupService.BackupItem(item.DeletePath, backupSessionDir);
            }
        }

        int deletedCount = 0;
        int skippedCount = 0;
        long freedBytes = 0;
        var warnings = new List<string>();
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DuplicateDeletePlanItem item in targets)
        {
            string delFull = Path.GetFullPath(item.DeletePath);
            if (string.Equals(delFull, rootResourceCfg, StringComparison.OrdinalIgnoreCase) ||
                CreatorOrganizerService.IsResourceCfgFile(delFull))
            {
                skippedCount++;
                continue;
            }

            if (keepPaths.Contains(delFull))
            {
                skippedCount++;
                warnings.Add($"보존 대상 파일과 겹쳐 삭제 건너뜀: {item.DeleteRelativePath}");
                continue;
            }

            if (!delFull.StartsWith(modsFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                skippedCount++;
                continue;
            }

            if (!File.Exists(item.KeepPath))
            {
                skippedCount++;
                warnings.Add($"원본 파일이 없어 삭제 건너뜀: {item.DeleteRelativePath}");
                continue;
            }

            if (!File.Exists(delFull))
            {
                skippedCount++;
                continue;
            }

            try
            {
                string? parent = Path.GetDirectoryName(delFull);
                if (!string.IsNullOrEmpty(parent))
                    touchedDirs.Add(parent);

                long sz = new FileInfo(delFull).Length;
                File.Delete(delFull);
                deletedCount++;
                freedBytes += sz;
            }
            catch (Exception ex)
            {
                skippedCount++;
                warnings.Add($"삭제 실패 ({item.DeleteRelativePath}): {ex.Message}");
            }
        }

        int cleanedFolders = CleanupEmptySubfolders(modsFull, touchedDirs);

        return new DuplicateCleanupResult
        {
            DeletedCount = deletedCount,
            SkippedCount = skippedCount,
            FreedBytes = freedBytes,
            CleanedEmptyFolderCount = cleanedFolders,
            BackupSessionDir = backupSessionDir,
            Warnings = warnings
        };
    }

    private static int CompareKeepPriority(
        (string FullPath, string RelativePath, string FileName, long Length, DateTime LastWriteUtc) a,
        (string FullPath, string RelativePath, string FileName, long Length, DateTime LastWriteUtc) b,
        string modsFull)
    {
        int scoreA = ComputeKeepScore(a.FullPath, a.RelativePath, a.FileName);
        int scoreB = ComputeKeepScore(b.FullPath, b.RelativePath, b.FileName);
        int cmp = scoreB.CompareTo(scoreA);
        if (cmp != 0) return cmp;

        cmp = b.LastWriteUtc.CompareTo(a.LastWriteUtc);
        if (cmp != 0) return cmp;

        cmp = b.Length.CompareTo(a.Length);
        if (cmp != 0) return cmp;

        cmp = a.RelativePath.Length.CompareTo(b.RelativePath.Length);
        if (cmp != 0) return cmp;

        return string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static int ComputeKeepScore(string fullPath, string relativePath, string fileName)
    {
        string[] parts = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        string topFolder = parts.Length > 1 ? parts[0] : string.Empty;

        int score = 0;

        if (CreatorOrganizerService.IsForcedOverridesFile(fileName))
        {
            if (topFolder.Equals(CreatorOrganizerService.OverridesFolderName, StringComparison.OrdinalIgnoreCase))
                score += 10000;
        }
        else if (topFolder.Equals(CreatorOrganizerService.OverridesFolderName, StringComparison.OrdinalIgnoreCase))
        {
            score += 2000;
        }

        if (!string.IsNullOrEmpty(topFolder))
        {
            if (!CreatorOrganizerService.IsStagingOrSpecialFolderName(topFolder))
                score += 1500; // 이미 정리된 제작자/카테고리 폴더
            else if (topFolder.Equals(CreatorOrganizerService.UncategorizedFolderName, StringComparison.OrdinalIgnoreCase))
                score += 400;  // Unknown 폴더 > test/test2 임시 폴더
            else if (topFolder.Equals("CC", StringComparison.OrdinalIgnoreCase))
                score += 300;
        }
        else
        {
            // Mods 루트 직속보다는 폴더 안 정리본 우선
            score += 100;
        }

        if (parts.Length == 2)
            score += 150; // 깔끔한 1단계 깊이 우선

        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (!CopySuffixRegex.IsMatch(stem))
            score += 250; // 원본 이름 > (1), _OLD, 복사본

        return score;
    }

    private static int CleanupEmptySubfolders(string modsFull, IEnumerable<string> touchedDirs)
    {
        int removed = 0;
        string overridesDir = Path.Combine(modsFull, CreatorOrganizerService.OverridesFolderName);
        string unknownDir = Path.Combine(modsFull, CreatorOrganizerService.UncategorizedFolderName);

        foreach (string dir in touchedDirs.Select(Path.GetFullPath).OrderByDescending(d => d.Length))
        {
            string? cur = dir;
            while (!string.IsNullOrEmpty(cur) &&
                   cur.StartsWith(modsFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string curTrim = cur.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(curTrim, overridesDir, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(curTrim, unknownDir, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                try
                {
                    if (Directory.Exists(curTrim) && !Directory.EnumerateFileSystemEntries(curTrim).Any())
                    {
                        Directory.Delete(curTrim);
                        removed++;
                        cur = Path.GetDirectoryName(curTrim);
                        continue;
                    }
                }
                catch
                {
                    // 무시
                }
                break;
            }
        }

        return removed;
    }
}
