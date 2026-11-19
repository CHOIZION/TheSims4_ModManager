using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 심즈 4 모드 업데이트 조회(GitHub/MCCC/커뮤니티/범용 웹 + CRC32 대조),
/// 매니페스트 URL 피닝, 병렬 점검 및 안전 백업/설치 엔진
/// </summary>
public sealed class ModUpdaterService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _modsFolder;
    private readonly string _manifestPath;
    private readonly BackupService? _backupService;
    private readonly CatalogService _catalog;
    private readonly ModMetadataExtractor _metadataExtractor;
    private readonly ModFinderService _finder;
    private readonly ArchiveAndFileService _archives;
    private readonly HttpClient _http;
    private readonly object _manifestLock = new();

    public ModUpdaterService(
        string modsFolder,
        string manifestPath,
        BackupService? backupService = null,
        CatalogService? catalog = null,
        ModFinderService? finder = null,
        ArchiveAndFileService? archives = null,
        HttpClient? httpClient = null)
    {
        _modsFolder = modsFolder;
        _manifestPath = manifestPath;
        _backupService = backupService;
        _catalog = catalog ?? new CatalogService();
        _metadataExtractor = new ModMetadataExtractor(_catalog);
        _http = httpClient ?? ModFinderService.CreateDefaultHttpClient();
        _finder = finder ?? new ModFinderService(_catalog, _metadataExtractor, _http);
        _archives = archives ?? new ArchiveAndFileService(_http, _finder);
    }

    public string ModsFolder => _modsFolder;
    public string ManifestPath => _manifestPath;

    public List<ManifestEntry> LoadManifest()
    {
        lock (_manifestLock)
        {
            if (string.IsNullOrWhiteSpace(_manifestPath) || !File.Exists(_manifestPath))
                return new List<ManifestEntry>();

            try
            {
                string json = File.ReadAllText(_manifestPath, Encoding.UTF8);
                var doc = JsonSerializer.Deserialize<ManifestDocument>(json);
                return doc?.Mods ?? new List<ManifestEntry>();
            }
            catch
            {
                return new List<ManifestEntry>();
            }
        }
    }

    public void SaveManifest(List<ManifestEntry> mods)
    {
        lock (_manifestLock)
        {
            string? dir = Path.GetDirectoryName(_manifestPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var doc = new ManifestDocument { Mods = mods };
            string json = JsonSerializer.Serialize(doc, JsonOptions);
            File.WriteAllText(_manifestPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public string GetPinnedUrl(string targetName, string folderName = "")
    {
        var queryKeys = BuildLookupKeys(targetName, folderName);
        if (queryKeys.Count == 0)
            return string.Empty;

        foreach (ManifestEntry entry in LoadManifest())
        {
            var entryKeys = BuildLookupKeys(entry.Name, entry.LocalFile);
            if (entryKeys.Overlaps(queryKeys))
            {
                string pinned = !string.IsNullOrWhiteSpace(entry.PinnedPageUrl)
                    ? entry.PinnedPageUrl
                    : (entry.Homepage ?? string.Empty);
                if ((pinned.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     pinned.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                    !pinned.Contains("google.com/search", StringComparison.OrdinalIgnoreCase))
                {
                    return pinned;
                }
            }
        }

        return string.Empty;
    }

    public void PinVerifiedUrl(
        ModQuery mod,
        string pageUrl,
        string latestVer = "",
        string localFileName = "")
    {
        if (string.IsNullOrWhiteSpace(pageUrl) ||
            (!pageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !pageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
            pageUrl.Contains("google.com/search", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_manifestLock)
        {
            var mods = LoadManifest();
            string modNameKey = !string.IsNullOrWhiteSpace(mod.Name)
                ? mod.Name.Trim()
                : (!string.IsNullOrWhiteSpace(localFileName) ? Path.GetFileNameWithoutExtension(localFileName.Trim()) : string.Empty);
            string localFn = !string.IsNullOrWhiteSpace(localFileName)
                ? localFileName.Trim()
                : (!string.IsNullOrWhiteSpace(mod.FilePath) ? Path.GetFileName(mod.FilePath) : modNameKey);

            string fName = (!string.IsNullOrWhiteSpace(mod.FolderName)
                ? mod.FolderName
                : (!string.IsNullOrWhiteSpace(mod.InstallFolder) ? mod.InstallFolder : modNameKey)).Trim();

            if (string.IsNullOrEmpty(modNameKey) && string.IsNullOrEmpty(localFn))
                return;

            var targetKeys = BuildLookupKeys(modNameKey, localFn);
            ManifestEntry? existing = mods.FirstOrDefault(e => BuildLookupKeys(e.Name, e.LocalFile).Overlaps(targetKeys));

            if (existing is not null)
            {
                existing.Name = string.IsNullOrEmpty(modNameKey) ? existing.Name : modNameKey;
                existing.LocalFile = localFn;
                existing.PinnedPageUrl = pageUrl;
                if (!string.IsNullOrEmpty(latestVer))
                    existing.CurrentVersion = latestVer;
            }
            else
            {
                mods.Add(new ManifestEntry
                {
                    Name = string.IsNullOrEmpty(modNameKey) ? localFn : modNameKey,
                    LocalFile = localFn,
                    SourceType = string.IsNullOrWhiteSpace(mod.SourceType) ? "generic_web" : mod.SourceType,
                    InstallFolder = string.IsNullOrEmpty(fName) ? modNameKey : fName,
                    PinnedPageUrl = pageUrl,
                    CurrentVersion = latestVer
                });
            }

            SaveManifest(mods);
        }
    }

    public bool RemovePinnedUrl(string targetName)
    {
        var targetKeys = BuildLookupKeys(targetName);
        if (targetKeys.Count == 0)
            return false;

        lock (_manifestLock)
        {
            var mods = LoadManifest();
            bool changed = false;
            foreach (ManifestEntry entry in mods)
            {
                if (BuildLookupKeys(entry.Name, entry.LocalFile).Overlaps(targetKeys) &&
                    !string.IsNullOrWhiteSpace(entry.PinnedPageUrl))
                {
                    entry.PinnedPageUrl = null;
                    changed = true;
                }
            }

            if (changed)
                SaveManifest(mods);
            return changed;
        }
    }

    private static HashSet<string> BuildLookupKeys(params string?[] rawValues)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in rawValues)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string trimmed = raw.Trim();
            keys.Add(trimmed);
            string stem = Path.GetFileNameWithoutExtension(trimmed);
            if (!string.IsNullOrWhiteSpace(stem))
                keys.Add(stem);
        }
        return keys;
    }

    public static ModQuery InferModConfig(string folderName)
    {
        string nameUpper = folderName.ToUpperInvariant();
        if (nameUpper.Contains("MCCC", StringComparison.Ordinal) ||
            nameUpper.Contains("COMMAND CENTER", StringComparison.Ordinal))
        {
            return new ModQuery
            {
                Name = folderName,
                FolderName = folderName,
                SourceType = "mccc",
                FolderKeywords = [folderName, "MOD MCCC", "MOD MC Command Center"],
                InstallFolder = folderName
            };
        }

        if (nameUpper.Contains("PRIO", StringComparison.Ordinal) ||
            nameUpper.Contains("OVERRIDE", StringComparison.Ordinal) ||
            nameUpper.Contains("WAMTIK", StringComparison.Ordinal) ||
            folderName.Contains("한글", StringComparison.Ordinal) ||
            folderName.Contains("바람별", StringComparison.Ordinal))
        {
            return new ModQuery
            {
                Name = folderName,
                FolderName = folderName,
                SourceType = "barambyul",
                FolderKeywords = [folderName, "PRIO", "Overrides"],
                InstallFolder = folderName,
                Homepage = "https://www.patreon.com/cw/wwindystarZIMS"
            };
        }

        return new ModQuery
        {
            Name = folderName,
            FolderName = folderName,
            SourceType = "generic_web",
            FolderKeywords = [folderName],
            InstallFolder = folderName,
            Creator = "파일 내부 메타데이터 자동 감지"
        };
    }

    public static ModQuery CreateQueryFromUnit(ModUnit unit)
    {
        ModQuery cfg = InferModConfig(unit.PrimaryFileName);
        if (unit.DirectoryPath.Contains("mccc", StringComparison.OrdinalIgnoreCase) &&
            unit.PrimaryFileName.StartsWith("mc_", StringComparison.OrdinalIgnoreCase))
        {
            cfg.SourceType = "mccc";
        }

        cfg.Name = unit.PrimaryFileName;
        cfg.FolderName = unit.FolderName;
        cfg.InstallFolder = unit.InstallFolder;
        cfg.DirectoryPath = unit.DirectoryPath;
        cfg.FilePath = unit.PrimaryFilePath;
        cfg.IsStandalone = unit.IsStandalone;
        cfg.UnitFiles = unit.UnitFiles.ToList();
        cfg.ModType = unit.ModType;
        return cfg;
    }

    public static ModQuery CreateQueryFromFolder(ModFolderSummary folder, ModScannerService scanner)
    {
        ModQuery cfg = InferModConfig(folder.Name);
        var (_, _, allFiles, _) = scanner.ScanAllModFiles(folder.Path, rootOnly: false);
        string? primaryScript = allFiles.FirstOrDefault(f => f.Extension == ".ts4script")?.FullPath;
        string? primaryFile = primaryScript ?? allFiles.FirstOrDefault()?.FullPath;

        cfg.FolderName = folder.Name;
        cfg.InstallFolder = folder.Name;
        cfg.DirectoryPath = folder.Path;
        cfg.FilePath = primaryFile;
        cfg.UnitFiles = allFiles.Select(f => f.FullPath).ToList();
        cfg.ModType = folder.ModType;
        return cfg;
    }

    public string ExtractLocalVersionToken(
        string detectedFileName,
        string? detectedFilePath,
        List<ManifestEntry> manifestEntries,
        string folderName = "")
    {
        string ver = VersionUtils.ExtractVersionFromFilename(detectedFileName);
        if (!string.IsNullOrEmpty(ver))
            return ver;

        if (!string.IsNullOrEmpty(detectedFilePath) && File.Exists(detectedFilePath))
        {
            ModMetadata meta = _metadataExtractor.Extract(detectedFilePath);
            string mVer = (meta.Version ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(mVer))
                return VersionUtils.FormatVersionToken(mVer);
        }

        if (!string.IsNullOrWhiteSpace(folderName))
        {
            string fVer = VersionUtils.ExtractVersionFromFilename(folderName);
            if (!string.IsNullOrEmpty(fVer))
                return fVer;
        }

        string tLow = (detectedFileName ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(tLow))
        {
            foreach (ManifestEntry entry in manifestEntries)
            {
                string eName = (entry.Name ?? string.Empty).Trim().ToLowerInvariant();
                string eLocal = (entry.LocalFile ?? string.Empty).Trim().ToLowerInvariant();
                if (tLow == eName || tLow == eLocal)
                {
                    string cVer = VersionUtils.ExtractVersionFromFilename(entry.CurrentVersion);
                    if (!string.IsNullOrEmpty(cVer))
                        return cVer;
                }
            }
        }

        return string.Empty;
    }

    public async Task<ModCheckResult> CheckModUpdateAsync(ModQuery mod, CancellationToken ct = default)
    {
        string folderName = !string.IsNullOrEmpty(mod.FolderName) ? mod.FolderName : mod.InstallFolder;
        string modName = !string.IsNullOrEmpty(mod.Name) ? mod.Name : folderName;
        var manifestEntries = LoadManifest();
        string localVer = !string.IsNullOrWhiteSpace(mod.CurrentVersion)
            ? mod.CurrentVersion
            : ExtractLocalVersionToken(modName, mod.FilePath, manifestEntries, folderName);

        mod.CurrentVersion = localVer;
        string sourceType = string.IsNullOrWhiteSpace(mod.SourceType) ? "generic_web" : mod.SourceType;

        ModCheckResult result;
        try
        {
            result = sourceType switch
            {
                "github" => await CheckGitHubUpdateAsync(mod.Repo, localVer, modName, ct),
                "mccc" => await CheckMcccUpdateAsync(localVer, modName, ct),
                "barambyul" => await CheckBarambyulUpdateAsync(localVer, modName, ct),
                _ => await CheckGenericWebUpdateAsync(mod, localVer, ct)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new ModCheckResult
            {
                ModName = modName,
                FolderName = folderName,
                StatusCode = ModUpdateStatusCode.Error,
                StatusLabel = $"오류: {ex.Message}",
                ErrorMessage = ex.Message
            };
        }

        result.ModName = string.IsNullOrEmpty(result.ModName) ? modName : result.ModName;
        result.FolderName = string.IsNullOrEmpty(result.FolderName) ? folderName : result.FolderName;
        result.FilePath = mod.FilePath ?? string.Empty;
        result.UnitFiles = mod.UnitFiles.ToList();
        result.ModType = mod.ModType;
        result.IsPinned = !string.IsNullOrEmpty(GetPinnedUrl(modName, folderName));

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            result.StatusCode = ModUpdateStatusCode.Error;
            result.StatusLabel = result.ErrorMessage;
            result.LocalVersionDisplay = string.IsNullOrEmpty(localVer) ? "버전 미표기" : localVer;
            return result;
        }

        // 로컬 vs 원격 버전 비교 및 ZIP Tail CRC32 대조
        if (result.LinkNotFound || (string.IsNullOrEmpty(result.PageUrl) && string.IsNullOrEmpty(result.DownloadUrl)))
        {
            result.LatestVersion = "링크를 찾을 수 없습니다.";
            result.StatusCode = ModUpdateStatusCode.LinkNotFound;
            result.StatusLabel = "링크를 찾을 수 없습니다.";
            result.StatusDisplay = "링크를 찾을 수 없습니다.";
        }
        else if (result.IsPaid && string.IsNullOrEmpty(localVer))
        {
            result.StatusCode = ModUpdateStatusCode.Paid;
            result.StatusLabel = "유료 상품 (자동 다운로드 불가)";
            result.StatusDisplay = "유료 상품";
        }
        else
        {
            var (statusCode, statusLabel) = VersionUtils.CompareLocalAndRemoteVersion(
                localVer,
                result.LatestVersion,
                result.DetectedFilename,
                result.AssetName);

            result.StatusCode = statusCode;
            result.StatusLabel = statusLabel;
            result.HasUpdate = statusCode == ModUpdateStatusCode.UpdateNeeded;
        }

        string? unitDir = !string.IsNullOrWhiteSpace(mod.DirectoryPath)
            ? mod.DirectoryPath
            : (!string.IsNullOrEmpty(folderName) && folderName != "(Mods 루트)" ? Path.Combine(_modsFolder, folderName) : null);

        if (result.StatusCode == ModUpdateStatusCode.UpdateNeeded)
        {
            bool? zipMatch = await _archives.VerifyLocalMatchesRemoteAsync(
                unitDir,
                result.DownloadUrl,
                result.DetectedFilename,
                result.AssetName,
                mod.FilePath,
                ct);

            if (zipMatch is true)
            {
                string remTok = FirstNonEmpty(
                    VersionUtils.ExtractVersionFromFilename(result.LatestVersion),
                    VersionUtils.ExtractVersionFromFilename(result.DetectedFilename),
                    VersionUtils.ExtractVersionFromFilename(result.AssetName));
                if (!string.IsNullOrEmpty(remTok))
                    localVer = remTok;

                result.StatusCode = ModUpdateStatusCode.UpToDate;
                result.StatusLabel = "최신 상태";
                result.HasUpdate = false;
            }
        }
        else if (result.StatusCode == ModUpdateStatusCode.ManualCheck && result.DirectFileDetected)
        {
            bool? matchState = await _archives.VerifyLocalMatchesRemoteAsync(
                unitDir,
                result.DownloadUrl,
                result.DetectedFilename,
                result.AssetName,
                mod.FilePath,
                ct);

            if (matchState is true)
            {
                if (string.IsNullOrEmpty(localVer))
                    localVer = "최신본 일치";
                result.StatusCode = ModUpdateStatusCode.UpToDate;
                result.StatusLabel = "최신 상태 (파일 일치)";
                result.HasUpdate = false;
            }
            else if (matchState is false)
            {
                result.StatusCode = ModUpdateStatusCode.UpdateNeeded;
                result.StatusLabel = "업데이트 필요";
                result.HasUpdate = true;
            }
        }

        if (string.IsNullOrEmpty(result.StatusDisplay))
            result.StatusDisplay = result.StatusLabel;

        result.CurrentVersion = localVer;
        result.LocalVersionDisplay = string.IsNullOrEmpty(localVer) ? "버전 미표기" : localVer;

        return result;
    }

    public async Task<List<ModCheckResult>> CheckMultipleModsAsync(
        IReadOnlyList<ModQuery> mods,
        int maxConcurrency = 4,
        IProgress<(int Completed, int Total, ModCheckResult Result)>? progress = null,
        CancellationToken ct = default)
    {
        int total = mods.Count;
        if (total == 0)
            return new List<ModCheckResult>();

        var results = new ModCheckResult[total];
        int completedCount = 0;
        using var sem = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var tasks = Enumerable.Range(0, total).Select(async idx =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ModCheckResult r = await CheckModUpdateAsync(mods[idx], ct);
                results[idx] = r;
                int done = Interlocked.Increment(ref completedCount);
                progress?.Report((done, total, r));
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results.ToList();
    }

    public async Task<List<string>> UpdateModAsync(
        ModQuery mod,
        ModCheckResult checkResult,
        OverwriteMode overwriteMode = OverwriteMode.All,
        bool skipBackup = false,
        IProgress<(long DownloadedBytes, long TotalBytes)>? downloadProgress = null,
        CancellationToken ct = default)
    {
        string downloadUrl = checkResult.DownloadUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("다운로드 URL이 유효하지 않습니다.");

        string? selectedFilePath = !string.IsNullOrWhiteSpace(mod.FilePath) && File.Exists(mod.FilePath)
            ? mod.FilePath
            : null;

        string targetDir;
        if (!string.IsNullOrEmpty(selectedFilePath))
        {
            targetDir = Path.GetDirectoryName(selectedFilePath)!;
        }
        else
        {
            string targetSubfolder = !string.IsNullOrWhiteSpace(mod.InstallFolder) ? mod.InstallFolder : mod.FolderName;
            string candidate = !string.IsNullOrWhiteSpace(targetSubfolder) && targetSubfolder != "(Mods 루트)"
                ? Path.Combine(_modsFolder, targetSubfolder)
                : _modsFolder;
            targetDir = Directory.Exists(candidate) ? candidate : _modsFolder;
        }

        // 1. 자동 백업
        string? backupSession = null;
        var backedUpPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_backupService is not null && !skipBackup)
        {
            if (!string.IsNullOrEmpty(selectedFilePath) && File.Exists(selectedFilePath))
            {
                backupSession = _backupService.CreateBackupSession(Path.GetFileNameWithoutExtension(selectedFilePath));
                _backupService.BackupItem(selectedFilePath, backupSession);
                backedUpPaths.Add(Path.GetFullPath(selectedFilePath));
            }
            else if (Directory.Exists(targetDir) && !PathsEqual(targetDir, _modsFolder))
            {
                backupSession = _backupService.CreateBackupSession(mod.Name);
                _backupService.BackupItem(targetDir, backupSession);
            }
        }

        void BackupIfExists(string destP)
        {
            if (_backupService is not null && !string.IsNullOrEmpty(backupSession) && File.Exists(destP))
            {
                string full = Path.GetFullPath(destP);
                if (backedUpPaths.Add(full))
                    _backupService.BackupItem(destP, backupSession);
            }
        }

        var installedNames = new List<string>();
        string tmpDir = Path.Combine(Path.GetTempPath(), $"s4mu_dl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        try
        {
            string rawAssetName = !string.IsNullOrWhiteSpace(checkResult.AssetName) ? checkResult.AssetName : "download.zip";
            string assetName = Regex.Replace(rawAssetName, @"[<>:""/\\|?*]+", "_").Trim();
            if (!EndsWithAnyExt(assetName, [".package", ".ts4script", ".zip", ".rar", ".7z"]))
                assetName += ".zip";

            string downloadedFile = Path.Combine(tmpDir, assetName);
            string pageUrlRef = checkResult.PageUrl ?? string.Empty;

            await _archives.DownloadFileAsync(
                downloadUrl,
                downloadedFile,
                downloadProgress,
                fallbackSourceDir: targetDir,
                pageUrl: pageUrlRef,
                targetModName: !string.IsNullOrEmpty(mod.Name) ? mod.Name : assetName,
                ct: ct);

            ArchiveAndFileService.VerifyDownloadedModFile(downloadedFile);

            Directory.CreateDirectory(targetDir);
            var existingNamesMap = Directory.EnumerateFiles(targetDir)
                .ToDictionary(f => Path.GetFileName(f) ?? string.Empty, f => f, StringComparer.OrdinalIgnoreCase);

            string dlExt = Path.GetExtension(downloadedFile).ToLowerInvariant();
            if (dlExt is ".zip" or ".rar" or ".7z")
            {
                string? targetFn = !string.IsNullOrEmpty(selectedFilePath) ? Path.GetFileName(selectedFilePath) : null;
                var extractedPaths = ArchiveAndFileService.ExtractArchive(
                    downloadedFile,
                    targetDir,
                    targetFn,
                    overwriteMode);

                foreach (string ep in extractedPaths)
                {
                    string epName = Path.GetFileName(ep);
                    if (!installedNames.Contains(epName, StringComparer.OrdinalIgnoreCase))
                        installedNames.Add(epName);
                }

                // 상호 배타적 에디션 정리 (Political World / Fitness Controls Limits)
                var extNamesLow = extractedPaths.Select(ep => Path.GetFileName(ep).ToLowerInvariant()).ToList();
                if (extNamesLow.Any(n => n.Contains("politicalworld", StringComparison.Ordinal)))
                {
                    bool installedFictional = extNamesLow.Any(n => n.Contains("fictional", StringComparison.Ordinal));
                    foreach (string exFile in Directory.EnumerateFiles(targetDir).ToList())
                    {
                        string exName = Path.GetFileName(exFile);
                        if (installedNames.Contains(exName, StringComparer.OrdinalIgnoreCase)) continue;
                        string exLow = exName.ToLowerInvariant();
                        if (!exLow.Contains("politicalworld", StringComparison.Ordinal)) continue;

                        if ((installedFictional && (exLow.Contains("coremod(realisticedition)_required", StringComparison.Ordinal) ||
                                                   exLow.Contains("politicalgurus_realisticedition", StringComparison.Ordinal))) ||
                            (!installedFictional && exLow.Contains("fictional", StringComparison.Ordinal)))
                        {
                            BackupIfExists(exFile);
                            ArchiveAndFileService.SafeRemove(exFile);
                        }
                    }
                }
                else if (extNamesLow.Any(n => n.Contains("fitnesscontrols-limits", StringComparison.Ordinal)))
                {
                    foreach (string exFile in Directory.EnumerateFiles(targetDir).ToList())
                    {
                        string exName = Path.GetFileName(exFile);
                        if (installedNames.Contains(exName, StringComparer.OrdinalIgnoreCase)) continue;
                        if (exName.Contains("fitnesscontrols-limits", StringComparison.OrdinalIgnoreCase))
                        {
                            BackupIfExists(exFile);
                            ArchiveAndFileService.SafeRemove(exFile);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(selectedFilePath) && File.Exists(selectedFilePath))
                {
                    string selExt = Path.GetExtension(selectedFilePath);
                    var sameExtFiles = extractedPaths
                        .Where(ep => Path.GetExtension(ep).Equals(selExt, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (sameExtFiles.Count == 1 && !PathsEqual(sameExtFiles[0], selectedFilePath))
                    {
                        ArchiveAndFileService.SafeRemove(selectedFilePath);
                        mod.FilePath = sameExtFiles[0];
                    }
                }
            }
            else
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(downloadedFile));
                if (!string.IsNullOrEmpty(selectedFilePath) && File.Exists(selectedFilePath) &&
                    Path.GetExtension(selectedFilePath).Equals(Path.GetExtension(downloadedFile), StringComparison.OrdinalIgnoreCase) &&
                    !PathsEqual(selectedFilePath, dest))
                {
                    ArchiveAndFileService.SafeRemove(selectedFilePath);
                }

                ArchiveAndFileService.SafeRemove(dest);
                File.Copy(downloadedFile, dest, overwrite: true);
                installedNames.Add(Path.GetFileName(dest));
                mod.FilePath = dest;
            }

            // 4. 다중 번들 / all 모드 추가 파일 처리
            bool isMultiBundle = checkResult.IsMultiFileBundle;
            if (overwriteMode == OverwriteMode.All || isMultiBundle)
            {
                foreach (RemoteFileEntry rf in checkResult.AllRemoteFiles)
                {
                    if (string.IsNullOrEmpty(rf.FileName) || string.IsNullOrEmpty(rf.DownloadUrl) ||
                        rf.DownloadUrl == downloadUrl || installedNames.Contains(rf.FileName, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string remExt = Path.GetExtension(rf.FileName).ToLowerInvariant();
                    if (remExt is ".package" or ".ts4script" &&
                        (isMultiBundle || existingNamesMap.ContainsKey(rf.FileName)))
                    {
                        try
                        {
                            string cleanRemFn = Regex.Replace(rf.FileName, @"[<>:""/\\|?*]+", "_").Trim();
                            string subDl = Path.Combine(tmpDir, cleanRemFn);
                            await _archives.DownloadFileAsync(rf.DownloadUrl, subDl, pageUrl: pageUrlRef, targetModName: cleanRemFn, ct: ct);
                            ArchiveAndFileService.VerifyDownloadedModFile(subDl);
                            string destSub = Path.Combine(targetDir, cleanRemFn);
                            BackupIfExists(destSub);
                            ArchiveAndFileService.SafeRemove(destSub);
                            File.Copy(subDl, destSub, overwrite: true);
                            if (!installedNames.Contains(cleanRemFn, StringComparer.OrdinalIgnoreCase))
                                installedNames.Add(cleanRemFn);
                        }
                        catch
                        {
                            // 개별 보조 파일 실패는 전체 중단하지 않음
                        }
                    }
                    else if (remExt is ".zip" or ".rar" or ".7z")
                    {
                        string remStemLow = Path.GetFileNameWithoutExtension(rf.FileName).ToLowerInvariant();
                        if (remStemLow.Contains("limits", StringComparison.Ordinal) &&
                            !existingNamesMap.Keys.Any(exN => exN.Contains(remStemLow, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                        try
                        {
                            string subDl = Path.Combine(tmpDir, Path.GetFileName(rf.FileName));
                            await _archives.DownloadFileAsync(rf.DownloadUrl, subDl, pageUrl: pageUrlRef, targetModName: rf.FileName, ct: ct);
                            ArchiveAndFileService.VerifyDownloadedModFile(subDl);
                            var subExtracted = ArchiveAndFileService.ExtractArchive(subDl, targetDir, overwriteMode: OverwriteMode.All);
                            foreach (string sep in subExtracted)
                            {
                                string sepName = Path.GetFileName(sep);
                                if (!installedNames.Contains(sepName, StringComparer.OrdinalIgnoreCase))
                                    installedNames.Add(sepName);
                            }
                        }
                        catch
                        {
                            // 무시
                        }
                    }
                }
            }
        }
        finally
        {
            ArchiveAndFileService.SafeRemove(tmpDir);
        }

        // 5. 버전 및 URL 자동 피닝 갱신
        string originalLocalName = !string.IsNullOrEmpty(selectedFilePath) ? Path.GetFileName(selectedFilePath) : mod.Name;
        if (overwriteMode == OverwriteMode.Single && installedNames.Count > 0)
            mod.Name = installedNames[0];
        else if (!string.IsNullOrWhiteSpace(checkResult.DetectedFilename))
            mod.Name = checkResult.DetectedFilename;

        mod.CurrentVersion = checkResult.LatestVersion;

        string verifiedPage = checkResult.PageUrl ?? string.Empty;
        if (!string.IsNullOrEmpty(verifiedPage) && installedNames.Count > 0)
            PinVerifiedUrl(mod, verifiedPage, mod.CurrentVersion, originalLocalName);

        return installedNames;
    }

    public async Task<ModCheckResult> CheckGitHubUpdateAsync(
        string repo,
        string currentVer,
        string modName = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return new ModCheckResult { ErrorMessage = "저장소(repo)가 지정되지 않았습니다." };

        string apiUrl = $"https://api.github.com/repos/{repo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github.v3+json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await _http.SendAsync(req, cts.Token);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new ModCheckResult { ErrorMessage = $"저장소 '{repo}'를 찾을 수 없거나 공식 Release가 등록되어 있지 않습니다 (404 Not Found)." };
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            return new ModCheckResult { ErrorMessage = "GitHub API 요청 한도를 초과했습니다 (잠시 후 다시 시도하세요)." };

        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        JsonElement root = doc.RootElement;

        string latestTag = (root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "").TrimStart('v', 'V');
        string releaseBody = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() ?? "" : "";
        string htmlPageUrl = root.TryGetProperty("html_url", out var htmlEl) ? htmlEl.GetString() ?? "" : $"https://github.com/{repo}/releases";

        string downloadUrl = string.Empty;
        string assetName = string.Empty;
        if (root.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assetsEl.EnumerateArray())
            {
                string aName = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                if (aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    aName.EndsWith(".package", StringComparison.OrdinalIgnoreCase) ||
                    aName.EndsWith(".ts4script", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out var dlEl) ? dlEl.GetString() ?? "" : "";
                    assetName = aName;
                    break;
                }
            }
        }

        int? cmp = VersionUtils.CompareVersions(latestTag, currentVer);
        bool hasUpdate = string.IsNullOrEmpty(currentVer) || (cmp is null ? !string.Equals(latestTag, currentVer, StringComparison.OrdinalIgnoreCase) : cmp > 0);

        return new ModCheckResult
        {
            ModName = modName,
            LatestVersion = latestTag,
            HasUpdate = hasUpdate,
            DirectFileDetected = !string.IsNullOrEmpty(downloadUrl),
            DownloadUrl = downloadUrl,
            PageUrl = htmlPageUrl,
            AssetName = assetName,
            DetectedFilename = assetName,
            ReleaseNotes = releaseBody,
            FinderDiagnostics = new FinderDiagnostics { MatchStage = "github_release" }
        };
    }

    public async Task<ModCheckResult> CheckMcccUpdateAsync(
        string currentVer,
        string targetName = "",
        CancellationToken ct = default)
    {
        const string url = "https://deaderpool-mccc.com/downloads.html";
        string? htmlStr = await _finder.GetStringSafeAsync(url, 15, ct);
        if (string.IsNullOrEmpty(htmlStr))
            return new ModCheckResult { ErrorMessage = "MCCC 웹사이트 접속 실패" };

        var doc = new HtmlDocument();
        doc.LoadHtml(htmlStr);

        bool isWoohoo = targetName.Contains("woohoo", StringComparison.OrdinalIgnoreCase);
        string latestVersion = string.Empty;
        string downloadUrl = string.Empty;
        string assetName = string.Empty;

        if (isWoohoo)
        {
            foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                string text = WebUtility.HtmlDecode(a.InnerText).Trim();
                string href = a.GetAttributeValue("href", "").Trim();
                string hrefLow = href.ToLowerInvariant();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    var mVer = Regex.Match(text, @"MC\s+Woohoo\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                    if (mVer.Success) latestVersion = mVer.Groups[1].Value;
                }
                if (hrefLow.Contains("mediafire.com", StringComparison.Ordinal) &&
                    hrefLow.Contains("mcwoohoo", StringComparison.Ordinal))
                {
                    downloadUrl = href;
                    var mFn = Regex.Match(href, @"(MCWoohoo_[^/?#]+\.zip)", RegexOptions.IgnoreCase);
                    if (mFn.Success) assetName = mFn.Groups[1].Value;
                    if (string.IsNullOrEmpty(latestVersion))
                    {
                        var mVu = Regex.Match(href, @"(\d+_\d+_\d+)");
                        if (mVu.Success) latestVersion = mVu.Groups[1].Value.Replace('_', '.');
                    }
                    break;
                }
            }
            if (string.IsNullOrEmpty(assetName) && !string.IsNullOrEmpty(latestVersion))
                assetName = $"MCWoohoo_ScriptAndPackage_{latestVersion.Replace('.', '_')}.zip";
        }
        else
        {
            string fallbackDriveUrl = string.Empty;
            foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                string text = WebUtility.HtmlDecode(a.InnerText).Trim();
                string href = a.GetAttributeValue("href", "").Trim();
                string hrefLow = href.ToLowerInvariant();
                if (string.IsNullOrEmpty(latestVersion))
                {
                    var match = Regex.Match(text, @"MC Command Center\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                    if (match.Success && !text.Contains("legacy", StringComparison.OrdinalIgnoreCase))
                    {
                        latestVersion = match.Groups[1].Value;
                        if (hrefLow.Contains("drive.google.com") || hrefLow.Contains("mediafire.com"))
                            fallbackDriveUrl = href;
                    }
                }
                if (hrefLow.Contains("mediafire.com", StringComparison.Ordinal) &&
                    hrefLow.Contains("mccmdcenter", StringComparison.Ordinal))
                {
                    downloadUrl = href;
                    var mFn = Regex.Match(href, @"(McCmdCenter_[^/?#]+\.zip)", RegexOptions.IgnoreCase);
                    if (mFn.Success) assetName = mFn.Groups[1].Value;
                    break;
                }
            }
            if (string.IsNullOrEmpty(downloadUrl))
                downloadUrl = fallbackDriveUrl;
            if (string.IsNullOrEmpty(assetName) && !string.IsNullOrEmpty(latestVersion))
                assetName = $"McCmdCenter_AllModules_{latestVersion.Replace('.', '_')}.zip";
        }

        if (string.IsNullOrEmpty(latestVersion) || string.IsNullOrEmpty(downloadUrl))
            return new ModCheckResult { ErrorMessage = "MCCC/MC Woohoo 최신 다운로드 링크를 파싱하지 못했습니다." };

        string labelPrefix = isWoohoo ? "MC Woohoo" : "MCCC";
        return new ModCheckResult
        {
            LatestVersion = $"Deaderpool (v{latestVersion})",
            StatusLabel = "최신 버전 배포 중",
            DirectFileDetected = true,
            RequiresBrowser = false,
            DownloadUrl = downloadUrl,
            PageUrl = url,
            AssetName = assetName,
            DetectedFilename = assetName,
            MatchedCreator = "Deaderpool",
            VerificationNote = $"Deaderpool 공식 다운로드 페이지 ({labelPrefix} MediaFire) 확인됨",
            ReleaseNotes = $"Deaderpool {labelPrefix} {latestVersion} Official Release (MediaFire: {assetName})",
            FinderDiagnostics = new FinderDiagnostics { MatchStage = "dedicated_scraper (MCCC)" }
        };
    }

    public async Task<ModCheckResult> CheckBarambyulUpdateAsync(
        string currentVer,
        string targetName = "",
        CancellationToken ct = default)
    {
        const string patreonUrl = "https://www.patreon.com/cw/wwindystarZIMS";
        const string postypeUrl = "https://www.postype.com/@wwwindystar";
        const string naverUrl = "https://blog.naver.com/wwwindystar";

        CatalogMatchResult? catMatch = !string.IsNullOrEmpty(targetName) ? _catalog.CheckKnownCatalog(targetName) : null;
        if (catMatch is not null && !catMatch.IsEarlyAccess)
        {
            string directPostUrl = catMatch.PrimaryUrl;
            if (!string.IsNullOrEmpty(directPostUrl) &&
                (directPostUrl.StartsWith("https://blog.naver.com/wwwindystar/", StringComparison.OrdinalIgnoreCase) ||
                 directPostUrl.Contains("/posts/", StringComparison.OrdinalIgnoreCase) ||
                 directPostUrl.Contains("/post/", StringComparison.OrdinalIgnoreCase)))
            {
                var remoteInfo = await _finder.InspectRemoteModPageAsync(directPostUrl, targetName, 12, ct: ct);
                if (remoteInfo.Status == "free_direct" && !string.IsNullOrEmpty(remoteInfo.DownloadUrl))
                {
                    string detectedFn = !string.IsNullOrEmpty(remoteInfo.FileName) ? remoteInfo.FileName : targetName;
                    string remVerRaw = FirstNonEmpty(remoteInfo.Version, catMatch.Version, "97.8").TrimStart('v', 'V');
                    string latestVerStr = Regex.IsMatch(remVerRaw, @"^20\d{2}\.\d{2}\.\d{2}$") ? remVerRaw : $"V{remVerRaw}";
                    string platLabel = directPostUrl.Contains("patreon.com", StringComparison.OrdinalIgnoreCase)
                        ? "Patreon"
                        : directPostUrl.Contains("postype.com", StringComparison.OrdinalIgnoreCase)
                            ? "Postype"
                            : "네이버 블로그";

                    return new ModCheckResult
                    {
                        LatestVersion = $"{platLabel} ({latestVerStr})",
                        StatusLabel = "최신 버전 배포 중",
                        DirectFileDetected = true,
                        RequiresBrowser = false,
                        DownloadUrl = remoteInfo.DownloadUrl,
                        PageUrl = directPostUrl,
                        PatreonUrl = directPostUrl.Contains("patreon.com", StringComparison.OrdinalIgnoreCase) ? directPostUrl : patreonUrl,
                        PostypeUrl = directPostUrl.Contains("postype.com", StringComparison.OrdinalIgnoreCase) ? directPostUrl : postypeUrl,
                        NaverBlogUrl = directPostUrl.Contains("blog.naver.com", StringComparison.OrdinalIgnoreCase) ? directPostUrl : naverUrl,
                        AssetName = detectedFn,
                        DetectedFilename = detectedFn,
                        AllRemoteFiles = remoteInfo.AllFiles,
                        MatchedCreator = "바람별 (wwindystarZIMS)",
                        VerificationNote = "공식 카탈로그 검증됨 (바람별 공식 한글화 모드)",
                        ReleaseNotes = !string.IsNullOrEmpty(catMatch.ReleaseNotes) ? catMatch.ReleaseNotes : $"바람별 공식 한글화 모드 ({detectedFn})",
                        FinderDiagnostics = new FinderDiagnostics { MatchStage = "dedicated_scraper (바람별 한글화)" }
                    };
                }
            }
        }

        string tLow = (targetName ?? string.Empty).ToLowerInvariant();
        bool isFf = tLow.Contains("-ff", StringComparison.Ordinal);
        string ffSuffix = isFf ? "-FF" : string.Empty;
        string ffDesc = isFf ? " · 풀네임 성씨-이름(FF) 표시" : string.Empty;
        string leadPrefix = tLow.StartsWith("_", StringComparison.Ordinal) ? "_" : "!";

        bool isPureEdition =
            (catMatch is not null && catMatch.CandidateLinks.Any(l => l.Url.Contains("9789832", StringComparison.Ordinal))) ||
            Regex.IsMatch(tLow, @"v\d+p\d+");

        if (isPureEdition)
        {
            string latestVersion = FirstNonEmpty(catMatch?.Version, "V98P8");
            string postLink = "https://www.postype.com/@wwwindystar/post/9789832";
            (latestVersion, postLink) = await ScrapePostypeChannelAsync(postypeUrl, @"통합한글화모음\s*(V\d+P\d+)", latestVersion, postLink, ct);

            return new ModCheckResult
            {
                LatestVersion = latestVersion,
                IsPaid = true,
                IsEarlyAccess = true,
                RequiresBrowser = true,
                DownloadUrl = postLink,
                PageUrl = postLink,
                PatreonUrl = patreonUrl,
                PostypeUrl = postLink,
                PostLink = postLink,
                NaverBlogUrl = naverUrl,
                AssetName = $"{leadPrefix}WAMTIK-{latestVersion}{ffSuffix}.package",
                MatchedCreator = "바람별 (wwindystarZIMS)",
                ReleaseNotes = $"바람별 한글화 모드 순한맛(순결버전){ffDesc} (최신 빌드: {latestVersion} · 수동 다운로드 안내)",
                FinderDiagnostics = new FinderDiagnostics { MatchStage = "dedicated_scraper (바람별 순결버전)" }
            };
        }

        bool isBetaRedEdition =
            (catMatch is not null && catMatch.PrimaryUrl.Contains("69805432", StringComparison.Ordinal)) ||
            Regex.IsMatch(tLow, @"v\d+b\d+");

        if (isBetaRedEdition)
        {
            string latestVersion = FirstNonEmpty(catMatch?.Version, "V98B8");
            string patreonPostUrl = (catMatch is not null && catMatch.PrimaryUrl.Contains("/posts/", StringComparison.OrdinalIgnoreCase))
                ? catMatch.PrimaryUrl
                : "https://www.patreon.com/wwindystarZIMS/posts/tonghabhangeulhw-69805432";
            string postLink = $"{postypeUrl}/post/11103833";
            (latestVersion, postLink) = await ScrapePostypeChannelAsync(postypeUrl, @"통합한글화모음\s*(V\d+B\d+)", latestVersion, postLink, ct);

            return new ModCheckResult
            {
                LatestVersion = latestVersion,
                IsPaid = true,
                IsEarlyAccess = true,
                RequiresBrowser = true,
                DownloadUrl = patreonPostUrl,
                PageUrl = patreonPostUrl,
                PatreonUrl = patreonPostUrl,
                PostypeUrl = postypeUrl,
                PostLink = postLink,
                NaverBlogUrl = naverUrl,
                AssetName = $"{leadPrefix}WAMTIK-{latestVersion}{ffSuffix}.package",
                MatchedCreator = "바람별 (wwindystarZIMS)",
                ReleaseNotes = $"베타버전 빨간맛{ffDesc} (최신 빌드: {latestVersion} · 수동 다운로드 안내)",
                FinderDiagnostics = new FinderDiagnostics { MatchStage = "dedicated_scraper (바람별 베타)" }
            };
        }

        // 광역후원자 선배포 버전 (V98G8 등)
        string latestGVer = "V98G8";
        string gPostLink = $"{postypeUrl}/post/7214790";
        (latestGVer, gPostLink) = await ScrapePostypeChannelAsync(postypeUrl, @"통합한글화모음\s*(V\d+G\d+)", latestGVer, gPostLink, ct);

        return new ModCheckResult
        {
            LatestVersion = latestGVer,
            IsPaid = true,
            IsEarlyAccess = true,
            RequiresBrowser = true,
            DownloadUrl = patreonUrl,
            PageUrl = patreonUrl,
            PatreonUrl = patreonUrl,
            PostypeUrl = postypeUrl,
            PostLink = gPostLink,
            NaverBlogUrl = naverUrl,
            AssetName = $"{leadPrefix}WAMTIK-{latestGVer}{ffSuffix}.package",
            MatchedCreator = "바람별 (wwindystarZIMS)",
            ReleaseNotes = $"선배포모드 번역이 추가되는 광역후원자 버전{ffDesc} (최신 빌드: {latestGVer} · 수동 다운로드 안내)",
            FinderDiagnostics = new FinderDiagnostics { MatchStage = "dedicated_scraper (바람별 선배포)" }
        };
    }

    private async Task<ModCheckResult> CheckGenericWebUpdateAsync(ModQuery mod, string localVer, CancellationToken ct)
    {
        string creator = string.IsNullOrWhiteSpace(mod.Creator) ? "공식 배포처" : mod.Creator;
        string matchedCreator = string.Empty;
        var finderDiagnostics = new FinderDiagnostics();
        string url = !string.IsNullOrWhiteSpace(mod.DownloadUrl) ? mod.DownloadUrl : mod.Homepage;
        var candidateLinks = new List<CatalogLink>();
        var extractedMeta = new ModMetadata();

        string? filePath = mod.FilePath;
        string folderName = !string.IsNullOrEmpty(mod.FolderName) ? mod.FolderName : mod.InstallFolder;
        string targetName = !string.IsNullOrEmpty(mod.Name) ? mod.Name : folderName;

        string customReleaseNotes = string.Empty;
        bool isPaidCatalog = false;
        bool isEarlyAccessCatalog = false;
        string pinnedUrl = GetPinnedUrl(targetName, folderName);
        bool isVerifiedSource = !string.IsNullOrEmpty(pinnedUrl) ||
                                (!string.IsNullOrEmpty(url) && !url.Contains("google.com", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(url) || url.Contains("google.com", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ModPageDiscoveryResult finderRes = await _finder.FindDirectModPageAsync(targetName, filePath, folderName, ct);
                url = finderRes.PrimaryUrl;
                creator = finderRes.SourceName;
                candidateLinks = finderRes.CandidateLinks;
                extractedMeta = finderRes.ExtractedMeta;
                customReleaseNotes = finderRes.ReleaseNotes;
                isPaidCatalog = finderRes.IsPaid;
                isEarlyAccessCatalog = finderRes.IsEarlyAccess;
                finderDiagnostics = finderRes.Diagnostics;

                matchedCreator = FirstNonEmpty(
                    finderRes.Creator,
                    extractedMeta.InferredCreator,
                    extractedMeta.Creators.FirstOrDefault(),
                    finderDiagnostics.TokenizedCreator);

                if (finderRes.IsVerifiedSource)
                    isVerifiedSource = true;
            }
            catch
            {
                // 메타데이터 검색 실패 시 계속 진행
            }
        }

        string localFnVer = FirstNonEmpty(
            VersionUtils.ExtractVersionFromFilename(targetName),
            VersionUtils.ExtractVersionFromFilename(folderName),
            localVer);

        var remoteInfo = new RemotePageInspectionResult { Status = "browser_required" };
        string verificationNote = string.Empty;

        try
        {
            string searchModLabel = FirstNonEmpty(targetName, extractedMeta.ManifestName);
            var urlsToCheck = new List<string>();
            if (!string.IsNullOrEmpty(pinnedUrl)) urlsToCheck.Add(pinnedUrl);
            if (!string.IsNullOrEmpty(url) && !urlsToCheck.Contains(url, StringComparer.OrdinalIgnoreCase))
                urlsToCheck.Add(url);
            foreach (CatalogLink cl in candidateLinks.Take(3))
            {
                if (!string.IsNullOrEmpty(cl.Url) && !urlsToCheck.Contains(cl.Url, StringComparer.OrdinalIgnoreCase))
                    urlsToCheck.Add(cl.Url);
            }

            foreach (string checkU in urlsToCheck)
            {
                var infoRes = await _finder.InspectRemoteModPageAsync(checkU, searchModLabel, 10, ct: ct);
                if (infoRes.Status == "free_direct")
                {
                    var (isSafe, _, matchReason) = _finder.IsSafeRemoteFileMatch(
                        targetName,
                        folderName,
                        extractedMeta,
                        infoRes,
                        checkU);

                    if (!isSafe)
                    {
                        verificationNote = $"자동 다운로드 보류 ({matchReason} - 브라우저 직접 확인 권장)";
                        continue;
                    }

                    verificationNote = matchReason;
                    string candFn = infoRes.FileName ?? string.Empty;
                    string candVer = VersionUtils.ExtractVersionFromFilename(candFn);

                    if (!string.IsNullOrEmpty(localFnVer) && !string.IsNullOrEmpty(candVer) &&
                        !VersionUtils.IsRemoteNewerOrEqual(localFnVer, candVer))
                    {
                        if (remoteInfo.Status != "free_direct")
                            remoteInfo = infoRes;
                        continue;
                    }

                    remoteInfo = infoRes;
                    break;
                }
                else if (infoRes.Status == "early_access")
                {
                    remoteInfo = infoRes;
                    if (checkU.Contains("/posts/", StringComparison.OrdinalIgnoreCase))
                        break;
                }
                else if (!string.IsNullOrEmpty(infoRes.Version) && string.IsNullOrEmpty(remoteInfo.Version))
                {
                    remoteInfo = infoRes;
                }
            }
        }
        catch
        {
            // 원격 검사 예외 무시
        }

        string chosenVer = ResolveWebFirstVersion(remoteInfo, localFnVer, extractedMeta, folderName, targetName);
        bool isFreeDirect = remoteInfo.Status == "free_direct" && !string.IsNullOrEmpty(remoteInfo.DownloadUrl);
        bool hasVerFound = !string.IsNullOrEmpty(chosenVer) || isFreeDirect;

        // 0) 유료 상품
        if (isPaidCatalog && !string.IsNullOrEmpty(url))
        {
            string paidVerLabel = !string.IsNullOrEmpty(chosenVer)
                ? FormatLatestVersionLabel(chosenVer, url)
                : $"{PlatformShortLabel(url)} (유료 상품)";

            return new ModCheckResult
            {
                ModName = mod.Name,
                FolderName = folderName,
                LatestVersion = paidVerLabel,
                StatusLabel = "유료 상품 (자동 다운로드 불가)",
                DirectFileDetected = false,
                IsPaid = true,
                RequiresBrowser = true,
                DownloadUrl = url,
                PageUrl = url,
                PatreonUrl = url.Contains("patreon.com", StringComparison.OrdinalIgnoreCase) ? url : string.Empty,
                CandidateLinks = candidateLinks,
                ExtractedMeta = extractedMeta,
                MatchedCreator = FirstNonEmpty(matchedCreator, creator),
                FinderDiagnostics = finderDiagnostics,
                VerificationNote = verificationNote,
                ReleaseNotes = !string.IsNullOrEmpty(customReleaseNotes) ? customReleaseNotes : "유료 상품이어서 자동 다운로드가 불가능합니다."
            };
        }

        // 1) 무료 직접 다운로드 감지됨
        if (isFreeDirect)
        {
            string detectedFn = !string.IsNullOrEmpty(remoteInfo.FileName) ? remoteInfo.FileName : $"{targetName}.zip";
            string finalPageU = !string.IsNullOrEmpty(remoteInfo.PageUrl) ? remoteInfo.PageUrl : url;
            string detectedVerLabel = FormatLatestVersionLabel(chosenVer, finalPageU, isDirect: true);
            string vBadge = !string.IsNullOrEmpty(verificationNote) ? $" | 검증: {verificationNote}" : string.Empty;

            var rawPkgFiles = remoteInfo.AllFiles
                .Where(f => EndsWithAnyExt(f.FileName, [".package", ".ts4script"]) && !string.IsNullOrEmpty(f.DownloadUrl))
                .ToList();

            bool isMultiBundle = rawPkgFiles.Count > 1 && EndsWithAnyExt(detectedFn, [".package", ".ts4script"]);
            var bundleFilenames = isMultiBundle ? rawPkgFiles.Select(f => f.FileName).ToList() : new List<string>();
            string displayDetectedFn = isMultiBundle
                ? $"{detectedFn} 외 {bundleFilenames.Count - 1}개 (총 {bundleFilenames.Count}개 파일)"
                : detectedFn;

            return new ModCheckResult
            {
                ModName = mod.Name,
                FolderName = folderName,
                LatestVersion = detectedVerLabel,
                HasUpdate = hasVerFound,
                StatusLabel = hasVerFound ? "최신 버전 배포 중" : "수동 등록 필요",
                DirectFileDetected = true,
                RequiresBrowser = false,
                DownloadUrl = remoteInfo.DownloadUrl,
                AssetName = detectedFn,
                DetectedFilename = displayDetectedFn,
                IsMultiFileBundle = isMultiBundle,
                BundleFilenames = bundleFilenames,
                VariantChoices = remoteInfo.VariantChoices,
                AllRemoteFiles = remoteInfo.AllFiles,
                PageUrl = finalPageU,
                CandidateLinks = candidateLinks,
                ExtractedMeta = extractedMeta,
                MatchedCreator = FirstNonEmpty(matchedCreator, creator),
                FinderDiagnostics = finderDiagnostics,
                VerificationNote = !string.IsNullOrEmpty(verificationNote) ? verificationNote : "정상 확인됨",
                ReleaseNotes = $"모드 파일 자동 감지됨 ({displayDetectedFn}){vBadge}"
            };
        }

        // 2) 얼리 액세스 / 선배포
        if (remoteInfo.Status == "early_access" || isEarlyAccessCatalog)
        {
            string eaPage = !string.IsNullOrEmpty(remoteInfo.PageUrl) ? remoteInfo.PageUrl : url;
            string eaTitle = !string.IsNullOrEmpty(remoteInfo.Title) ? remoteInfo.Title : targetName;
            string eaVerLabel = FormatLatestVersionLabel(chosenVer, eaPage);

            return new ModCheckResult
            {
                ModName = mod.Name,
                FolderName = folderName,
                LatestVersion = eaVerLabel,
                HasUpdate = hasVerFound,
                StatusLabel = hasVerFound ? "최신 버전 배포 중" : "수동 등록 필요",
                IsEarlyAccess = true,
                RequiresBrowser = true,
                DownloadUrl = eaPage,
                PageUrl = eaPage,
                CandidateLinks = candidateLinks,
                ExtractedMeta = extractedMeta,
                MatchedCreator = FirstNonEmpty(matchedCreator, creator),
                FinderDiagnostics = finderDiagnostics,
                VerificationNote = verificationNote,
                ReleaseNotes = !string.IsNullOrEmpty(customReleaseNotes) ? customReleaseNotes : $"얼리 액세스(선배포) 제한 포스트 ({eaTitle})"
            };
        }

        // 3) 비검증 링크 안전 보류 처리
        if (!isVerifiedSource && remoteInfo.Status != "free_direct" &&
            verificationNote.StartsWith("자동 다운로드 보류", StringComparison.Ordinal))
        {
            finderDiagnostics.FailureReason = $"웹 검색으로 후보 링크({url})를 찾았으나 원격 파일 유사도 안전 검증에서 기각됨 ({verificationNote})";
            url = string.Empty;
            candidateLinks = new List<CatalogLink>();
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new ModCheckResult
            {
                ModName = mod.Name,
                FolderName = folderName,
                LatestVersion = "링크를 찾을 수 없습니다.",
                StatusLabel = "링크를 찾을 수 없습니다.",
                LinkNotFound = true,
                ExtractedMeta = extractedMeta,
                MatchedCreator = matchedCreator,
                FinderDiagnostics = finderDiagnostics,
                VerificationNote = verificationNote,
                ReleaseNotes = "링크를 찾을 수 없습니다."
            };
        }

        string finalReleaseNotes;
        if (!string.IsNullOrEmpty(customReleaseNotes))
            finalReleaseNotes = customReleaseNotes;
        else if (url.Contains("blog.naver.com", StringComparison.OrdinalIgnoreCase))
            finalReleaseNotes = "네이버 블로그 참고 (무료)";
        else if (url.Contains("dcinside.com", StringComparison.OrdinalIgnoreCase))
            finalReleaseNotes = "디시인사이드 심즈 갤러리 참고 (무료)";
        else
        {
            var notesParts = new List<string> { $"{creator} 확인" };
            if (!string.IsNullOrEmpty(extractedMeta.InferredCreator))
                notesParts.Add($"제작자: {extractedMeta.InferredCreator}");
            if (!string.IsNullOrEmpty(extractedMeta.ManifestName))
                notesParts.Add($"모드명: {extractedMeta.ManifestName}");
            finalReleaseNotes = string.Join(" | ", notesParts);
        }

        return new ModCheckResult
        {
            ModName = mod.Name,
            FolderName = folderName,
            LatestVersion = FormatLatestVersionLabel(chosenVer, url),
            HasUpdate = hasVerFound,
            StatusLabel = hasVerFound ? "최신 버전 배포 중" : "수동 등록 필요",
            DirectFileDetected = false,
            RequiresBrowser = true,
            DownloadUrl = url,
            PageUrl = url,
            CandidateLinks = candidateLinks,
            ExtractedMeta = extractedMeta,
            MatchedCreator = FirstNonEmpty(matchedCreator, creator),
            FinderDiagnostics = finderDiagnostics,
            VerificationNote = verificationNote,
            ReleaseNotes = finalReleaseNotes
        };
    }

    private async Task<(string Version, string PostLink)> ScrapePostypeChannelAsync(
        string postypeUrl,
        string versionRegex,
        string defaultVer,
        string defaultPostLink,
        CancellationToken ct)
    {
        string latestVer = defaultVer;
        string postLink = defaultPostLink;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, postypeUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await _http.SendAsync(req, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct);
                var m = Regex.Match(body, versionRegex);
                if (m.Success)
                    latestVer = m.Groups[1].Value;

                var linkM = Regex.Match(body, @"href=""(/@wwwindystar/post/\d+)""[^>]*>[^<]*" + Regex.Escape(latestVer));
                if (linkM.Success)
                    postLink = $"https://www.postype.com{linkM.Groups[1].Value}";
            }
        }
        catch
        {
            // 폴백 유지
        }

        return (latestVer, postLink);
    }

    /// <summary>
    /// 원격 웹 페이지 정보를 1순위로 version 확정하고, 웹에 버전 표기가 전혀 없는 경우에만 알려진 기본 버전으로 폴백합니다.
    /// (구버전 하드코딩 덮어쓰기로 인해 신규 업데이트가 숨겨지는 문제 방지)
    /// </summary>
    private static string ResolveWebFirstVersion(
        RemotePageInspectionResult remInfo,
        string locVer,
        ModMetadata extractedMeta,
        string folderName,
        string targetName)
    {
        string remVer = FirstNonEmpty(
            VersionUtils.ExtractVersionFromFilename(remInfo.Version),
            VersionUtils.ExtractVersionFromFilename(remInfo.FileName),
            VersionUtils.ExtractVersionFromFilename(remInfo.Title));

        string metaVerRaw = (extractedMeta.Version ?? string.Empty).Trim();
        string metaVer = Regex.IsMatch(metaVerRaw, @"^(?:20\d{2}\.\d{2}\.\d{2}|[ab]\d+\.\d+(?:\.\d+)*)$", RegexOptions.IgnoreCase)
            ? metaVerRaw
            : (!string.IsNullOrEmpty(metaVerRaw) ? $"v{metaVerRaw.TrimStart('v', 'V')}" : string.Empty);

        if (string.IsNullOrEmpty(remVer))
        {
            string folderLow = (folderName ?? string.Empty).Trim().ToLowerInvariant();
            string targetLow = (targetName ?? string.Empty).Trim().ToLowerInvariant();
            if (folderLow.Contains("ai chat") || folderLow.Contains("aichat"))
                remVer = "v2.4.3";
            else if (folderLow.Contains("volleyball") || targetLow.Contains("volleyball"))
                remVer = "v0.2.4";
            else if (folderLow.Contains("twister") || targetLow.Contains("twister"))
                remVer = "v0.2.3";
            else if (folderLow.Contains("ride together") || folderLow.Contains("ride_together") || targetLow.Contains("ride together"))
                remVer = "a0.5";
        }

        if (!string.IsNullOrEmpty(remVer) && !string.IsNullOrEmpty(locVer))
        {
            if (remVer.TrimStart('v', 'V').Replace(".", "") == locVer.TrimStart('v', 'V').Replace(".", "") &&
                locVer.Contains('.') && !remVer.Contains('.'))
            {
                return locVer;
            }
            return VersionUtils.IsRemoteNewerOrEqual(locVer, remVer) ? remVer : locVer;
        }

        return FirstNonEmpty(remVer, locVer, metaVer);
    }

    private static string FormatLatestVersionLabel(string verCandidate, string pageU, bool isDirect = false)
    {
        string plat = PlatformShortLabel(pageU);
        if (!string.IsNullOrEmpty(verCandidate))
        {
            string vNorm = Regex.IsMatch(verCandidate, @"^(?:20\d{2}\.\d{2}\.\d{2}|[ab]\d+\.\d+(?:\.\d+)*)$", RegexOptions.IgnoreCase)
                ? verCandidate
                : (verCandidate.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? verCandidate : $"v{verCandidate}");
            return $"{plat} ({vNorm})";
        }

        return isDirect ? $"{plat} (최신 배포본)" : "수동 확인 필요";
    }

    private static string PlatformShortLabel(string pageU)
    {
        string uLow = (pageU ?? string.Empty).ToLowerInvariant();
        if (uLow.Contains("modthesims.info")) return "MTS";
        if (uLow.Contains("patreon.com")) return "Patreon";
        if (uLow.Contains("curseforge.com")) return "CurseForge";
        if (uLow.Contains("itch.io")) return "Itch.io";
        if (uLow.Contains("tumblr.com")) return "Tumblr";
        if (uLow.Contains("postype.com")) return "Postype";
        if (uLow.Contains("blog.naver.com")) return "네이버 블로그";
        if (uLow.Contains("dcinside.com")) return "디시인사이드";
        if (uLow.Contains("drive.google.com")) return "Google Drive";
        if (uLow.Contains("github.com")) return "GitHub";
        if (uLow.Contains("simfileshare.net")) return "SimFileShare";
        if (uLow.Contains("wonderfulwhims.com")) return "WonderfulWhims";
        if (uLow.Contains("deaderpool-mccc.com")) return "Deaderpool";
        if (uLow.Contains("mediafire.com")) return "MediaFire";
        if (uLow.Contains("scumbumbomods.com")) return "Scumbumbo";
        if (uLow.Contains("seebee.de")) return "sims4me";
        if (uLow.Contains("kemzimamods.com")) return "KemZima";
        if (uLow.Contains("pandasama.com")) return "Pandasama";
        if (uLow.Contains("lot51.cc")) return "Lot 51";
        if (uLow.Contains("twistedmexi.com")) return "TwistedMexi";
        return "Patreon";
    }

    private static bool EndsWithAnyExt(string text, string[] exts) =>
        exts.Any(e => text.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string?[] vals)
    {
        foreach (string? v in vals)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return string.Empty;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
