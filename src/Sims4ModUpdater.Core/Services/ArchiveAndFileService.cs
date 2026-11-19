using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 모드 파일 다운로드, 바이너리 유효성 검증, ZIP/RAR/7Z 압축 해제, SHA256 및 원격 ZIP Tail(CRC32) 무결성 대조 서비스
/// </summary>
public sealed class ArchiveAndFileService
{
    private static readonly string[] ValidTrayAndModExts =
    [
        ".package", ".ts4script",
        ".trayitem", ".blueprint", ".bpi", ".householdbinary", ".hhi", ".sgi", ".rmi", ".room"
    ];

    private static readonly EnumerationOptions RecursiveEnumOpts = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true
    };

    private static readonly EnumerationOptions ShallowEnumOpts = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true
    };

    private readonly HttpClient _http;
    private readonly ModFinderService? _finder;

    public ArchiveAndFileService(HttpClient? httpClient = null, ModFinderService? finder = null)
    {
        _http = httpClient ?? ModFinderService.CreateDefaultHttpClient();
        _finder = finder;
    }

    public static async Task<string> CalculateSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<string> DownloadFileAsync(
        string url,
        string destPath,
        IProgress<(long DownloadedBytes, long TotalBytes)>? progress = null,
        string? fallbackSourceDir = null,
        string? pageUrl = null,
        string? targetModName = null,
        CancellationToken ct = default)
    {
        string? destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        // 1) #free_member_file=... (무료 공개 포스트 로컬 캐시 복사)
        if (url.Contains("#free_member_file=", StringComparison.Ordinal))
        {
            string frag = url.Split('#', 2)[1];
            var qs = CatalogService.ParseQueryString(frag);
            string targetFn = qs.TryGetValue("free_member_file", out string? fmf) && !string.IsNullOrEmpty(fmf)
                ? fmf
                : Path.GetFileName(destPath);

            string altFn = targetFn.StartsWith("_KOR_", StringComparison.OrdinalIgnoreCase)
                ? Regex.Replace(targetFn, "^_KOR_", "(KOR)", RegexOptions.IgnoreCase)
                : targetFn.StartsWith("(KOR)", StringComparison.OrdinalIgnoreCase)
                    ? "_KOR_" + targetFn[5..]
                    : targetFn;

            string downloadsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");

            var searchDirs = new[] { fallbackSourceDir, downloadsDir }
                .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string sDir in searchDirs)
            {
                foreach (string candName in new[] { targetFn, altFn })
                {
                    string candLocal = Path.Combine(sDir, Path.GetFileName(candName));
                    if (File.Exists(candLocal))
                    {
                        File.Copy(candLocal, destPath, overwrite: true);
                        return destPath;
                    }
                }
            }

            throw new InvalidOperationException(
                $"무료 회원 공개 파일('{targetFn}')을 PC의 다운로드/모드 폴더에서 찾지 못했습니다. 브라우저에서 먼저 다운로드 후 다시 시도해 주세요.");
        }

        // 2) #itchio_upload_id=... (만료 없는 지연 서명 URL 갱신)
        if (url.Contains("#itchio_upload_id=", StringComparison.Ordinal) && _finder is not null)
        {
            string[] split = url.Split('#', 2);
            string baseU = split[0];
            var qs = CatalogService.ParseQueryString(split[1]);
            string targetFn = qs.TryGetValue("file_name", out string? fn) && !string.IsNullOrEmpty(fn)
                ? fn
                : Path.GetFileName(destPath);

            var refreshed = await _finder.InspectRemoteModPageAsync(baseU, targetFn, timeoutSeconds: 15, ct: ct);
            if (!string.IsNullOrEmpty(refreshed.DownloadUrl))
                url = refreshed.DownloadUrl;
        }

        // 3) MediaFire 페이지 URL인 경우 직접 CDN URL 해석
        if (url.Contains("mediafire.com/file/", StringComparison.OrdinalIgnoreCase) && _finder is not null)
        {
            var mfRes = await _finder.InspectRemoteModPageAsync(url, targetModName ?? Path.GetFileName(destPath), timeoutSeconds: 15, ct: ct);
            if (!string.IsNullOrEmpty(mfRes.DownloadUrl))
                url = mfRes.DownloadUrl;
        }

        using HttpResponseMessage response = await SendDownloadRequestWithItchRetryAsync(url, pageUrl, targetModName ?? Path.GetFileName(destPath), ct);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? 0;
        long downloaded = 0;

        await using Stream netStream = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream outStream = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);

        byte[] buffer = new byte[65536];
        int bytesRead;
        while ((bytesRead = await netStream.ReadAsync(buffer, ct)) > 0)
        {
            await outStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            if (totalBytes > 0)
                progress?.Report((downloaded, totalBytes));
        }

        if (totalBytes <= 0)
            progress?.Report((downloaded, downloaded));

        return destPath;
    }

    public static void VerifyDownloadedModFile(string filePath)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length == 0)
            throw new InvalidOperationException("다운로드된 파일이 비어 있거나 존재하지 않습니다.");

        byte[] head = new byte[(int)Math.Min(512, fi.Length)];
        using (var fs = File.OpenRead(filePath))
        {
            int read = fs.Read(head, 0, head.Length);
            Array.Resize(ref head, read);
        }

        string headText = Encoding.UTF8.GetString(head).TrimStart().ToLowerInvariant();
        if (headText.StartsWith("<!doctype html", StringComparison.Ordinal) ||
            headText.StartsWith("<html", StringComparison.Ordinal) ||
            headText.StartsWith("<head", StringComparison.Ordinal) ||
            headText.StartsWith("<script", StringComparison.Ordinal) ||
            headText.StartsWith("<?xml", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "다운로드된 파일이 모드 파일이 아닌 웹 페이지(HTML) 응답입니다. 로그인이 필요하거나 직접 다운로드가 차단된 링크일 수 있으므로 브라우저에서 직접 다운로드해 주세요.");
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".package")
        {
            if (head.Length < 4 || head[0] != 'D' || head[1] != 'B' || head[2] != 'P' || head[3] != 'F')
                throw new InvalidOperationException($"'{fi.Name}' 파일이 유효한 심즈 4 .package(DBPF) 형식이 아닙니다.");
            return;
        }

        if (ext == ".ts4script")
        {
            if (!TryOpenZip(filePath, out ZipArchive? zf) || zf is null)
                throw new InvalidOperationException($"'{fi.Name}' 파일이 유효한 심즈 4 .ts4script 아카이브 형식이 아닙니다.");
            zf.Dispose();
            return;
        }

        if (ext == ".zip")
        {
            if (!TryOpenZip(filePath, out ZipArchive? zf) || zf is null)
                throw new InvalidOperationException($"'{fi.Name}' 파일이 손상되었거나 유효한 ZIP 압축 파일이 아닙니다.");

            using (zf)
            {
                bool hasValidModEntry = zf.Entries
                    .Where(e => !IsDirectoryEntry(e))
                    .Select(e => Path.GetFileName(e.FullName.Replace('\\', '/')))
                    .Where(name => !string.IsNullOrEmpty(name) &&
                                   !name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) &&
                                   !name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                    .Any(name => ValidTrayAndModExts.Any(v => name.EndsWith(v, StringComparison.OrdinalIgnoreCase)));

                if (!hasValidModEntry)
                {
                    throw new InvalidOperationException(
                        $"압축 파일('{fi.Name}') 내부에 심즈 4 모드/부지·심 파일(.package / .ts4script / Tray 파일)이 포함되어 있지 않아 설치를 중단했습니다.");
                }
            }
        }
    }

    public static List<string> ExtractArchive(
        string archivePath,
        string targetDir,
        string? targetFilename = null,
        OverwriteMode overwriteMode = OverwriteMode.All)
    {
        Directory.CreateDirectory(targetDir);
        string targetDirFull = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var extractedFiles = new List<string>();
        string ext = Path.GetExtension(archivePath).ToLowerInvariant();
        var existingNamesLower = Directory.EnumerateFiles(targetDir, "*", ShallowEnumOpts)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        string targetExt = !string.IsNullOrWhiteSpace(targetFilename) ? Path.GetExtension(targetFilename).ToLowerInvariant() : string.Empty;

        if (ext == ".zip" && TryOpenZip(archivePath, out ZipArchive? zf) && zf is not null)
        {
            using (zf)
            {
                var members = zf.Entries
                    .Where(m =>
                    {
                        if (IsDirectoryEntry(m)) return false;
                        string normalized = m.FullName.Replace('\\', '/');
                        if (normalized.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
                            normalized.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase))
                            return false;
                        string baseName = Path.GetFileName(normalized);
                        return !string.IsNullOrEmpty(baseName) &&
                               !baseName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) &&
                               !baseName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                var modMembers = members
                    .Where(m => IsPackageOrScriptExt(Path.GetExtension(m.FullName.Replace('\\', '/'))))
                    .ToList();
                if (modMembers.Count > 0)
                    members = modMembers;

                if (overwriteMode == OverwriteMode.Single && !string.IsNullOrWhiteSpace(targetFilename))
                {
                    var exactMatches = members
                        .Where(m => Path.GetFileName(m.FullName.Replace('\\', '/')).Equals(targetFilename, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (exactMatches.Count > 0)
                    {
                        members = [exactMatches[0]];
                    }
                    else if (!string.IsNullOrEmpty(targetExt))
                    {
                        var sameExt = members
                            .Where(m => Path.GetExtension(m.FullName.Replace('\\', '/')).Equals(targetExt, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (sameExt.Count > 0)
                            members = [sameExt[0]];
                    }
                }
                else if (overwriteMode == OverwriteMode.All)
                {
                    var installedMatches = members
                        .Where(m => existingNamesLower.Contains(Path.GetFileName(m.FullName.Replace('\\', '/'))))
                        .ToList();
                    bool hasChooseOne = members.Any(m => m.FullName.Contains("choose", StringComparison.OrdinalIgnoreCase));

                    if (installedMatches.Count > 0 && members.Count > installedMatches.Count && (members.Count > 4 || hasChooseOne))
                    {
                        members = installedMatches;
                    }
                    else if (installedMatches.Count == 0 && !string.IsNullOrWhiteSpace(targetFilename) && (members.Count > 4 || hasChooseOne))
                    {
                        var exactM = members
                            .Where(m => Path.GetFileName(m.FullName.Replace('\\', '/')).Equals(targetFilename, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (exactM.Count > 0)
                            members = exactM;
                    }
                }

                foreach (ZipArchiveEntry member in members)
                {
                    string baseName = Path.GetFileName(member.FullName.Replace('\\', '/'));
                    if (string.IsNullOrEmpty(baseName)) continue;

                    string destFile = Path.GetFullPath(Path.Combine(targetDir, baseName));
                    // Zip-Slip 방어
                    if (!destFile.StartsWith(targetDirFull, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"위험한 압축 경로가 감지되었습니다: {member.FullName}");

                    using Stream srcStream = member.Open();
                    using FileStream dstStream = File.Create(destFile);
                    srcStream.CopyTo(dstStream);
                    extractedFiles.Add(destFile);
                }
            }

            return extractedFiles;
        }

        // RAR / 7Z 등은 Windows 내장 bsdtar(tar.exe)로 임시 해제 후 복사
        string tmpExt = Path.Combine(Path.GetTempPath(), $"s4mu_ext_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpExt);
        try
        {
            var psi = new ProcessStartInfo("tar")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-xf");
            psi.ArgumentList.Add(archivePath);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(tmpExt);

            using Process? proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Windows tar.exe를 실행할 수 없습니다.");
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"압축 해제 실패 (tar exit {proc.ExitCode}): {stderr}");

            var allCandidates = Directory.EnumerateFiles(tmpExt, "*", RecursiveEnumOpts)
                .Where(f =>
                {
                    string fn = Path.GetFileName(f);
                    return !fn.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) &&
                           !fn.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var modOnly = allCandidates.Where(f => IsPackageOrScriptExt(Path.GetExtension(f))).ToList();
            if (modOnly.Count > 0)
                allCandidates = modOnly;

            if (overwriteMode == OverwriteMode.Single && !string.IsNullOrWhiteSpace(targetFilename))
            {
                var exactMatches = allCandidates
                    .Where(f => Path.GetFileName(f).Equals(targetFilename, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (exactMatches.Count > 0)
                {
                    allCandidates = [exactMatches[0]];
                }
                else if (!string.IsNullOrEmpty(targetExt))
                {
                    var sameExt = allCandidates
                        .Where(f => Path.GetExtension(f).Equals(targetExt, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (sameExt.Count > 0)
                        allCandidates = [sameExt[0]];
                }
            }
            else if (overwriteMode == OverwriteMode.All)
            {
                var installedMatches = allCandidates
                    .Where(f => existingNamesLower.Contains(Path.GetFileName(f)))
                    .ToList();
                if (installedMatches.Count > 0)
                    allCandidates = installedMatches;
            }

            foreach (string srcFile in allCandidates)
            {
                string baseName = Path.GetFileName(srcFile);
                string destFile = Path.GetFullPath(Path.Combine(targetDir, baseName));
                if (!destFile.StartsWith(targetDirFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"위험한 압축 경로가 감지되었습니다: {baseName}");

                File.Copy(srcFile, destFile, overwrite: true);
                extractedFiles.Add(destFile);
            }
        }
        finally
        {
            SafeRemove(tmpExt);
        }

        return extractedFiles;
    }

    /// <summary>
    /// 원격 배포 파일(.zip Central Directory CRC32/크기 또는 단일 파일 Content-Length/fragment size)과
    /// 로컬 파일을 메모리 상에서 비교합니다. (True=일치/최신, False=불일치/업데이트필요, null=비교불가)
    /// </summary>
    public async Task<bool?> VerifyLocalMatchesRemoteAsync(
        string? folderPath,
        string? downloadUrl,
        string? detectedFilename,
        string? assetName,
        string? targetFilePath = null,
        CancellationToken ct = default)
    {
        string dlUrl = (downloadUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(dlUrl) ||
            (!dlUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !dlUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (dlUrl.Contains("mediafire.com/file/", StringComparison.OrdinalIgnoreCase) ||
            dlUrl.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? searchDir = !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath)
            ? folderPath
            : (!string.IsNullOrWhiteSpace(targetFilePath) && File.Exists(targetFilePath) ? Path.GetDirectoryName(targetFilePath) : null);

        if (string.IsNullOrWhiteSpace(searchDir) || !Directory.Exists(searchDir))
            return null;

        try
        {
            var localFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in Directory.EnumerateFiles(searchDir, "*", RecursiveEnumOpts))
            {
                if (IsPackageOrScriptExt(Path.GetExtension(p)))
                    localFiles.TryAdd(Path.GetFileName(p), p);
            }

            if (!string.IsNullOrWhiteSpace(targetFilePath) && File.Exists(targetFilePath))
                localFiles[Path.GetFileName(targetFilePath)] = targetFilePath;

            // 1) Patreon #free_member_file=...&size=<bytes> 단일 파일 바이트 비교
            var mFragSz = Regex.Match(dlUrl, @"[#&]size=(\d+)");
            string resolvedAssetName = (!string.IsNullOrEmpty(assetName) ? assetName : detectedFilename ?? string.Empty).Trim();
            if (mFragSz.Success && IsPackageOrScriptExt(Path.GetExtension(resolvedAssetName)))
            {
                if (long.TryParse(mFragSz.Groups[1].Value, out long remoteSz) && remoteSz > 0)
                {
                    string assetBase = Path.GetFileName(resolvedAssetName);
                    string? locP = localFiles.TryGetValue(assetBase, out string? mapped) ? mapped : targetFilePath;
                    if (!string.IsNullOrEmpty(locP) && File.Exists(locP))
                        return new FileInfo(locP).Length == remoteSz;
                }
            }

            string cleanDlUrl = dlUrl.Split('#')[0];

            // 2) 단일 .package / .ts4script 직링크 -> HEAD Content-Length 비교
            bool looksLikeSingleFile =
                IsPackageOrScriptExt(Path.GetExtension(cleanDlUrl.Split('?')[0])) ||
                (IsPackageOrScriptExt(Path.GetExtension(resolvedAssetName)) && !EndsWithArchiveExt(cleanDlUrl.Split('?')[0]));

            if (looksLikeSingleFile)
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, cleanDlUrl);
                using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headCts.CancelAfter(TimeSpan.FromSeconds(8));
                using var headResp = await _http.SendAsync(headReq, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
                if (headResp.IsSuccessStatusCode && headResp.Content.Headers.ContentLength is long cl && cl > 0)
                {
                    string assetBase = Path.GetFileName(resolvedAssetName);
                    string? locP = (!string.IsNullOrEmpty(assetBase) && localFiles.TryGetValue(assetBase, out string? mapped)) ? mapped : targetFilePath;
                    if (!string.IsNullOrEmpty(locP) && File.Exists(locP))
                        return new FileInfo(locP).Length == cl;
                }
                return null;
            }

            // 3) .zip 아카이브 끝부분 16KB Range 조회 (실패 시 최대 15MB 폴백)
            byte[] buf = await FetchZipTailOrSmallArchiveAsync(cleanDlUrl, ct);
            var remoteEntries = ParseZipCentralDirectoryEntries(buf);
            if (remoteEntries.Count == 0)
                return null;

            int matchedCount = 0;
            int mismatchedCount = 0;

            foreach (var (baseFn, uncompSz, crc) in remoteEntries)
            {
                if (!localFiles.TryGetValue(baseFn, out string? locP) || !File.Exists(locP))
                    continue;

                if (new FileInfo(locP).Length != uncompSz)
                {
                    mismatchedCount++;
                    continue;
                }

                uint localCrc = Crc32Helper.ComputeFile(locP);
                if (localCrc != crc)
                {
                    mismatchedCount++;
                    continue;
                }

                matchedCount++;
            }

            if (mismatchedCount > 0) return false;
            if (matchedCount > 0) return true;
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 바이트 버퍼에서 ZIP Central Directory(PK\x01\x02) 엔트리를 파싱합니다.
    /// </summary>
    public static List<(string FileName, uint UncompressedSize, uint Crc32)> ParseZipCentralDirectoryEntries(ReadOnlySpan<byte> buf)
    {
        var remoteEntries = new List<(string FileName, uint UncompressedSize, uint Crc32)>();
        byte[] sig = [0x50, 0x4B, 0x01, 0x02]; // PK\x01\x02
        int pos = 0;

        while (pos < buf.Length)
        {
            int rel = buf[pos..].IndexOf(sig);
            if (rel < 0)
                break;

            int idx = pos + rel;
            if (idx + 46 > buf.Length)
                break;

            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(idx + 16, 4));
            uint uncompSz = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(idx + 24, 4));
            ushort fnLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(idx + 28, 2));
            ushort exLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(idx + 30, 2));
            ushort cmLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.Slice(idx + 32, 2));

            if (idx + 46 + fnLen > buf.Length)
                break;

            string fn = Encoding.UTF8.GetString(buf.Slice(idx + 46, fnLen));
            string baseFn = Path.GetFileName(fn.Replace('\\', '/'));
            if (IsPackageOrScriptExt(Path.GetExtension(baseFn)))
                remoteEntries.Add((baseFn, uncompSz, crc));

            pos = idx + 46 + fnLen + exLen + cmLen;
        }

        return remoteEntries;
    }

    public static void SafeRemove(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 무시
        }
    }

    private async Task<HttpResponseMessage> SendDownloadRequestWithItchRetryAsync(
        string url,
        string? pageUrl,
        string targetModName,
        CancellationToken ct)
    {
        HttpResponseMessage resp = await SendCustomDownloadGetAsync(url, ct);

        if ((resp.StatusCode == HttpStatusCode.BadRequest || resp.StatusCode == HttpStatusCode.Forbidden) &&
            !string.IsNullOrWhiteSpace(pageUrl) &&
            pageUrl.Contains(".itch.io", StringComparison.OrdinalIgnoreCase) &&
            _finder is not null)
        {
            resp.Dispose();
            var refreshed = await _finder.InspectRemoteModPageAsync(pageUrl, targetModName, timeoutSeconds: 15, ct: ct);
            if (!string.IsNullOrEmpty(refreshed.DownloadUrl))
                resp = await SendCustomDownloadGetAsync(refreshed.DownloadUrl, ct);
        }

        return resp;
    }

    private async Task<HttpResponseMessage> SendCustomDownloadGetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        string uLow = url.ToLowerInvariant();
        if (uLow.Contains("dcinside.com", StringComparison.Ordinal))
            req.Headers.TryAddWithoutValidation("Referer", "https://gall.dcinside.com/");
        else if (uLow.Contains("blog.naver.com", StringComparison.Ordinal))
            req.Headers.TryAddWithoutValidation("Referer", "https://blog.naver.com/");
        else if (uLow.Contains("kemzimamods.com", StringComparison.Ordinal))
        {
            req.Headers.Remove("User-Agent");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    private async Task<byte[]> FetchZipTailOrSmallArchiveAsync(string cleanDlUrl, CancellationToken ct)
    {
        byte[] sig = [0x50, 0x4B, 0x01, 0x02];
        try
        {
            using var rangeReq = new HttpRequestMessage(HttpMethod.Get, cleanDlUrl);
            rangeReq.Headers.TryAddWithoutValidation("Range", "bytes=-16384");
            using var rangeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rangeCts.CancelAfter(TimeSpan.FromSeconds(8));
            using var rangeResp = await _http.SendAsync(rangeReq, rangeCts.Token);
            if (rangeResp.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
            {
                byte[] buf = await rangeResp.Content.ReadAsByteArrayAsync(ct);
                if (buf.AsSpan().IndexOf(sig) >= 0)
                    return buf;
            }
        }
        catch
        {
            // Range 미지원 시 전체 GET 폴백
        }

        try
        {
            const long maxBytes = 15L * 1024L * 1024L;
            using var fullReq = new HttpRequestMessage(HttpMethod.Get, cleanDlUrl);
            using var fullCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            fullCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var fullResp = await _http.SendAsync(fullReq, HttpCompletionOption.ResponseHeadersRead, fullCts.Token);
            if (fullResp.IsSuccessStatusCode)
            {
                if (fullResp.Content.Headers.ContentLength is long cl && cl > maxBytes)
                    return Array.Empty<byte>();

                await using Stream s = await fullResp.Content.ReadAsStreamAsync(ct);
                using var ms = new MemoryStream();
                byte[] chunk = new byte[65536];
                int read;
                while ((read = await s.ReadAsync(chunk, ct)) > 0)
                {
                    if (ms.Length + read > maxBytes)
                        return Array.Empty<byte>();
                    ms.Write(chunk, 0, read);
                }
                return ms.ToArray();
            }
        }
        catch
        {
            // 무시
        }

        return Array.Empty<byte>();
    }

    private static bool TryOpenZip(string filePath, out ZipArchive? archive)
    {
        try
        {
            archive = ZipFile.OpenRead(filePath);
            return true;
        }
        catch
        {
            archive = null;
            return false;
        }
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        string normalized = entry.FullName.Replace('\\', '/');
        return normalized.EndsWith('/') || (entry.Length == 0 && string.IsNullOrEmpty(Path.GetFileName(normalized)));
    }

    private static bool IsPackageOrScriptExt(string ext) =>
        ext.Equals(".package", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ts4script", StringComparison.OrdinalIgnoreCase);

    private static bool EndsWithArchiveExt(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase);
}
