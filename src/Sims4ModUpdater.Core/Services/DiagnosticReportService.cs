using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 개인정보(절대 경로/계정명) 마스킹을 적용한 정밀 스캔 &amp; 누락 원인 진단 리포트(scan_report.txt) 생성 서비스
/// </summary>
public sealed class DiagnosticReportService
{
    public sealed class DiagnosticReportOutput
    {
        public string ReportPath { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public int TotalUnits { get; init; }
        public int MatchedUrlCount { get; init; }
        public int FailedOrManualCount { get; init; }
        public int ExcludedTopCount { get; init; }
        public int ExcludedSubCount { get; init; }
        public int MultiScriptCount { get; init; }
        public int MergedUnitCount { get; init; }
        public int UnaccountedCount { get; init; }
    }

    private readonly string _baseDirectory;
    private readonly ModMetadataExtractor _metadataExtractor;

    public DiagnosticReportService(string? baseDirectory = null, ModMetadataExtractor? metadataExtractor = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? Directory.GetCurrentDirectory() : baseDirectory;
        _metadataExtractor = metadataExtractor ?? new ModMetadataExtractor();
    }

    public static string ToRelativeModsPath(string? rawPath, string modsFolder)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return "-";

        try
        {
            string rel = Path.GetRelativePath(Path.GetFullPath(modsFolder), Path.GetFullPath(rawPath));
            if (rel.StartsWith("..", StringComparison.Ordinal))
                return Path.GetFileName(rawPath);
            return rel == "." ? "Mods" : $"Mods\\{rel}";
        }
        catch
        {
            return Path.GetFileName(rawPath);
        }
    }

    public static string SanitizeReportPrivacy(string text, string modsFolder, string? baseDir = null)
    {
        string outText = text;
        try
        {
            string mStr = Path.GetFullPath(modsFolder);
            if (!string.IsNullOrEmpty(mStr))
            {
                outText = outText.Replace(mStr, "Mods", StringComparison.OrdinalIgnoreCase);
                outText = outText.Replace(mStr.Replace('\\', '/'), "Mods", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // 무시
        }

        if (!string.IsNullOrEmpty(baseDir))
        {
            try
            {
                string bStr = Path.GetFullPath(baseDir);
                outText = outText.Replace(bStr, ".", StringComparison.OrdinalIgnoreCase);
                outText = outText.Replace(bStr.Replace('\\', '/'), ".", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 무시
            }
        }

        try
        {
            string homeStr = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            if (!string.IsNullOrEmpty(homeStr))
            {
                outText = outText.Replace(homeStr, "<USER_HOME>", StringComparison.OrdinalIgnoreCase);
                outText = outText.Replace(homeStr.Replace('\\', '/'), "<USER_HOME>", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // 무시
        }

        outText = Regex.Replace(outText, @"(?i)[a-z]:\\Users\\[^\\/\r\n]+", @"C:\Users\<USER>");
        outText = Regex.Replace(outText, @"(?i)[a-z]:/Users/[^\\/\r\n]+", "C:/Users/<USER>");
        return outText;
    }

    public DiagnosticReportOutput GenerateReport(
        ModScannerService scanner,
        IReadOnlyList<ModCheckResult>? preCheckedResults = null,
        string? outputPath = null)
    {
        string reportPath = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(_baseDirectory, "scan_report.txt")
            : outputPath;

        ModsScanResult scanSummary = scanner.Scan();
        ScanDiagnostics diag = scanner.CollectScanDiagnostics();
        var modUnits = diag.ModUnits;

        var evaluatedRows = preCheckedResults is { Count: > 0 }
            ? preCheckedResults.ToList()
            : modUnits.Select(u => new ModCheckResult
            {
                ModName = u.PrimaryFileName,
                FolderName = u.FolderName,
                FilePath = u.PrimaryFilePath,
                UnitFiles = u.UnitFiles.ToList(),
                ModType = u.ModType,
                LocalVersionDisplay = VersionUtils.ExtractVersionFromFilename(u.PrimaryFileName) is { Length: > 0 } lv ? lv : "버전 미표기",
                LatestVersion = "-",
                StatusCode = ModUpdateStatusCode.ManualCheck,
                StatusLabel = "스캔 완료 (업데이트 조회 전)",
                StatusDisplay = "스캔 완료 (업데이트 조회 전)"
            }).ToList();

        var matchedSuccessRows = new List<ModCheckResult>();
        var failedOrUnmatchedRows = new List<ModCheckResult>();

        foreach (ModCheckResult r in evaluatedRows)
        {
            string pageU = !string.IsNullOrEmpty(r.PageUrl) ? r.PageUrl : r.DownloadUrl;
            string remV = r.LatestVersion ?? string.Empty;
            bool isLinkMissing = r.LinkNotFound || string.IsNullOrEmpty(pageU) || remV == "링크를 찾을 수 없습니다.";
            bool isVerFailed =
                r.StatusCode is ModUpdateStatusCode.ManualCheck or ModUpdateStatusCode.Error or ModUpdateStatusCode.LinkNotFound ||
                remV is "-" or "수동 확인 필요" ||
                remV.Contains("수동 확인", StringComparison.Ordinal) ||
                (r.VerificationNote?.Contains("자동 다운로드 보류", StringComparison.Ordinal) ?? false);

            if (!isLinkMissing)
                matchedSuccessRows.Add(r);
            if (isLinkMissing || isVerFailed)
                failedOrUnmatchedRows.Add(r);
        }

        var sb = new StringBuilder();
        string nowStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        sb.AppendLine(new string('=', 90));
        sb.AppendLine("심즈 4 모드 자동 업데이터 (Sims 4 Mod Auto Updater .NET Edition) - 모드 스캔 & 누락 원인 진단 리포트");
        sb.AppendLine(new string('=', 90));
        sb.AppendLine($"- 생성 일시: {nowStr}");
        sb.AppendLine("- 대상 Mods 경로: ./Mods (개인정보 보호를 위해 상대 경로 표기)");
        sb.AppendLine($"- 전체 모드 파일 수: 총 {scanSummary.TotalCount}개 (.package: {scanSummary.PackageCount}개, .ts4script: {scanSummary.ScriptCount}개)");
        sb.AppendLine($"- 업데이트 점검 대상 개별 모드(Mod Unit): 총 {modUnits.Count}개");
        sb.AppendLine($"- 웹 배포처 URL 매칭 성공 모드: {matchedSuccessRows.Count}개");
        sb.AppendLine($"- 웹 배포처 링크 미발견 또는 버전 매칭 실패/수동확인 모드: {failedOrUnmatchedRows.Count}개");
        sb.AppendLine(
            $"- 스캔 제외/병합 현황: 최상위 제외 폴더 {diag.ExcludedTopFolders.Count}개 | 제외 하위폴더 {diag.ExcludedSubfolders.Count}개 | " +
            $"다중 .ts4script 폴더 {diag.MultiScriptFolders.Count}개 | 부속 파일 병합 그룹 {diag.MergedUnitFiles.Count}개 | " +
            $"루트 단일 파일 {diag.RootFilesDiagnostics.Count}개 | 미분류 사각지대 파일 {diag.UnaccountedFiles.Count}개");
        sb.AppendLine(new string('=', 90));
        sb.AppendLine();

        // [섹션 1]
        sb.AppendLine(new string('━', 90));
        sb.AppendLine($"[1] 감지 및 배포처 매칭 성공 모드 목록 (총 {matchedSuccessRows.Count}개 / 전체 감지 유닛 {evaluatedRows.Count}개)");
        sb.AppendLine(new string('━', 90));
        if (matchedSuccessRows.Count == 0)
        {
            sb.AppendLine("  (감지 및 배포처 매칭에 성공한 모드가 없습니다.)");
        }
        else
        {
            for (int i = 0; i < matchedSuccessRows.Count; i++)
            {
                ModCheckResult r = matchedSuccessRows[i];
                ModMetadata metaObj = r.ExtractedMeta;
                string creatorName = FirstNonEmpty(r.MatchedCreator, metaObj.InferredCreator, metaObj.Creators.FirstOrDefault(), "제작자 자동 감지");
                string stage = !string.IsNullOrEmpty(r.FinderDiagnostics.MatchStage) ? r.FinderDiagnostics.MatchStage : "web_or_catalog";
                string bundleNote = r.UnitFiles.Count > 1 ? $" (그룹 내 총 {r.UnitFiles.Count}개 파일 묶임)" : string.Empty;

                sb.AppendLine($"  {i + 1:D2}. 폴더/파일명: [{r.FolderName}] › {r.ModName}{bundleNote}");
                sb.AppendLine($"      - 상대 경로: {ToRelativeModsPath(r.FilePath, scanner.ModsFolder)}");
                sb.AppendLine($"      - 분류: {(string.IsNullOrEmpty(r.ModType) ? "-" : r.ModType)} | 매칭 방식: {stage}");
                sb.AppendLine($"      - 매칭된 제작자: {creatorName}");
                sb.AppendLine($"      - 배포처 URL: {(string.IsNullOrEmpty(r.PageUrl) ? "-" : r.PageUrl)}");
                sb.AppendLine($"      - 버전 및 상태: 내 로컬 버전 [{r.LocalVersionDisplay}] / 웹 최신 버전 [{r.LatestVersion}] → {r.StatusLabel}");
                sb.AppendLine();
            }
        }

        // [섹션 2]
        sb.AppendLine(new string('━', 90));
        sb.AppendLine("[2] 스캔에서 제외되었거나 대표 파일로 병합/생략된 폴더 및 파일 상세 사유");
        sb.AppendLine(new string('━', 90));

        sb.AppendLine($"\n  ▶ 2-1. 루트 폴더(Mods/)의 단일 파일 진단 (총 {diag.RootFilesDiagnostics.Count}개)");
        if (diag.RootFilesDiagnostics.Count == 0)
            sb.AppendLine("    - Mods 루트 폴더에 직속 단일 파일이 없습니다.");
        else
        {
            foreach (RootFileDiag rd in diag.RootFilesDiagnostics)
            {
                sb.AppendLine($"    • 파일명: {rd.FileName} (상대 경로: Mods\\{rd.FileName})");
                sb.AppendLine($"      - 처리 상태: {rd.Status}");
                sb.AppendLine($"      - 사유: {rd.Reason}");
            }
        }

        sb.AppendLine($"\n  ▶ 2-2. .package 전용 폴더(.ts4script 없음) 및 CC 제외 폴더 분류 내역 (총 {diag.PackageOnlyFolders.Count}개)");
        if (diag.PackageOnlyFolders.Count == 0)
            sb.AppendLine("    - 해당되는 .package 전용 폴더가 없습니다.");
        else
        {
            foreach (PackageOnlyFolderDiag pf in diag.PackageOnlyFolders)
            {
                sb.AppendLine($"    • 폴더명: [{pf.FolderName}] (상대 경로: Mods\\{pf.FolderName} | .package {pf.PackageCount}개, .ts4script {pf.ScriptCount}개)");
                sb.AppendLine($"      - 처리 상태: {pf.Status}");
                sb.AppendLine($"      - 사유: {pf.Reason}");
            }
        }

        sb.AppendLine($"\n  ▶ 2-3. 한 폴더 안에 여러 개의 .ts4script가 들어있어 대표 1개 외 [N]개가 병합/생략된 폴더 (총 {diag.MultiScriptFolders.Count}개)");
        if (diag.MultiScriptFolders.Count == 0)
            sb.AppendLine("    - 한 폴더 안에 2개 이상의 .ts4script가 들어있는 폴더가 없습니다.");
        else
        {
            foreach (MultiScriptFolderDiag ms in diag.MultiScriptFolders)
            {
                sb.AppendLine($"    • 폴더명: [{ms.FolderName}] (상대 경로: Mods\\{ms.FolderName} | .ts4script 총 {ms.TotalScripts}개)");
                sb.AppendLine($"      - 폴더 대표 1개: {ms.RepresentativeScript} / 외 [{ms.OmittedInFolderViewCount}]개: {string.Join(", ", ms.OtherScripts)}");
                sb.AppendLine($"      - 사유: {ms.Reason}");
            }
        }

        sb.AppendLine($"\n  ▶ 2-4. 동일 모드 그룹 내 대표 파일 1개 외 부속 파일로 묶여 개별 검색에서 생략된 항목 (총 {diag.MergedUnitFiles.Count}개 그룹)");
        if (diag.MergedUnitFiles.Count == 0)
            sb.AppendLine("    - 다중 파일로 병합된 모드 그룹이 없습니다.");
        else
        {
            foreach (MergedUnitFilesDiag mu in diag.MergedUnitFiles)
            {
                sb.AppendLine($"    • 위치/대표 파일: [{mu.FolderName}] › 대표 1개: {mu.PrimaryFile} (외 [{mu.OmittedCount}]개 병합됨)");
                sb.AppendLine($"      - 사유: {mu.Reason}");
                if (mu.OmittedScripts.Count > 0)
                    sb.AppendLine($"      - 생략된 부속 .ts4script ({mu.OmittedScripts.Count}개): {string.Join(", ", mu.OmittedScripts)}");
                if (mu.OmittedPackages.Count > 0)
                {
                    string pkgPrev = string.Join(", ", mu.OmittedPackages.Take(15));
                    if (mu.OmittedPackages.Count > 15) pkgPrev += $" 외 {mu.OmittedPackages.Count - 15}개";
                    sb.AppendLine($"      - 생략된 부속 .package ({mu.OmittedPackages.Count}개): {pkgPrev}");
                }
            }
        }

        sb.AppendLine($"\n  ▶ 2-5. 모드 폴더 내부의 보조/옵션/백업/CC 하위폴더로 분류되어 스캔에서 제외된 하위폴더 (총 {diag.ExcludedSubfolders.Count}개)");
        if (diag.ExcludedSubfolders.Count == 0)
            sb.AppendLine("    - 제외된 내부 보조/CC 하위폴더가 없습니다.");
        else
        {
            foreach (ExcludedSubfolderDiag es in diag.ExcludedSubfolders)
            {
                string fPrev = string.Join(", ", es.Files.Take(10));
                if (es.Files.Count > 10) fPrev += $" 외 {es.Files.Count - 10}개";
                sb.AppendLine($"    • 하위폴더 상대 경로: [Mods\\{es.FolderName}] (.package {es.PackageCount}개, .ts4script {es.ScriptCount}개)");
                sb.AppendLine($"      - 사유: {es.Reason}");
                sb.AppendLine($"      - 포함 파일: {fPrev}");
            }
        }

        var emptyTop = diag.ExcludedTopFolders.Where(et => et.PackageCount == 0 && et.ScriptCount == 0).ToList();
        sb.AppendLine($"\n  ▶ 2-6. 모드 파일이 없는 빈 폴더 / 기타 제외 폴더 (총 {emptyTop.Count}개) 및 전수 대조 검증");
        if (emptyTop.Count == 0)
            sb.AppendLine("    - 모드 파일이 없는 빈 최상위 폴더가 없습니다.");
        else
        {
            foreach (ExcludedTopFolderDiag et in emptyTop)
                sb.AppendLine($"    • 폴더 상대 경로: [Mods\\{et.FolderName}] → 사유: {et.Reason}");
        }

        if (diag.UnaccountedFiles.Count > 0)
        {
            sb.AppendLine($"    ⚠️ [미분류 누락 파일 감지: {diag.UnaccountedFiles.Count}개]");
            foreach (UnaccountedFileDiag ua in diag.UnaccountedFiles)
                sb.AppendLine($"      • Mods\\{ua.RelativePath} → {ua.Reason}");
        }
        else
        {
            sb.AppendLine($"    ✅ [전수 대조 완료] Mods 폴더 내 모든 .package / .ts4script 파일(총 {scanSummary.TotalCount}개)이 단 1개의 미분류 누락도 없이 위 포함/제외/병합 항목에 100% 추적되었습니다.");
        }
        sb.AppendLine();

        // [섹션 3]
        sb.AppendLine(new string('━', 90));
        sb.AppendLine($"[3] 웹 배포처 링크 미발견 또는 버전 매칭 실패 모드 상세 진단 (총 {failedOrUnmatchedRows.Count}개)");
        sb.AppendLine(new string('━', 90));
        if (failedOrUnmatchedRows.Count == 0)
        {
            sb.AppendLine("  🎉 모든 감지 모드의 웹 배포처 링크 및 버전 매칭이 100% 성공했습니다!");
        }
        else
        {
            for (int i = 0; i < failedOrUnmatchedRows.Count; i++)
            {
                ModCheckResult r = failedOrUnmatchedRows[i];
                ModMetadata meta = r.ExtractedMeta;
                if (!meta.HasMetadata && !string.IsNullOrEmpty(r.FilePath) && File.Exists(r.FilePath))
                    meta = _metadataExtractor.Extract(r.FilePath);

                var uFileNames = r.UnitFiles.Select(Path.GetFileName).Where(n => n is not null).ToList();
                string relFpStr = ToRelativeModsPath(r.FilePath, scanner.ModsFolder);

                sb.AppendLine($"  [{i + 1:D2}] 로컬 폴더 / 파일명: [{r.FolderName}] › {r.ModName}");
                sb.AppendLine($"    • 로컬 파일 상대 경로: {relFpStr}");
                if (uFileNames.Count > 1)
                    sb.AppendLine($"    • 그룹 내 묶인 파일 목록 ({uFileNames.Count}개): {string.Join(", ", uFileNames)}");
                sb.AppendLine($"    • 버전/링크 상태: 로컬 버전='{r.LocalVersionDisplay}' | 웹 최신 버전='{r.LatestVersion}' | 판정='{r.StatusLabel}' | 배포처 URL='{(string.IsNullOrEmpty(r.PageUrl) ? "(미발견)" : r.PageUrl)}'");
                sb.AppendLine("    • [ModMetadataExtractor 추출 내부 메타데이터]:");
                foreach (string line in FormatMetadataForReport(meta))
                    sb.AppendLine(line);
                sb.AppendLine("    • [검색 실패 / 버전 매칭 실패 상세 사유]:");
                foreach (string reasonLine in BuildFailureReasonsForReport(r, meta))
                    sb.AppendLine($"      - {reasonLine}");
                sb.AppendLine();
            }
        }

        sb.AppendLine(new string('=', 90));
        sb.AppendLine("End of Diagnostic Report (개인정보/절대 경로 미포함)");
        sb.AppendLine(new string('=', 90));

        string sanitizedContent = SanitizeReportPrivacy(sb.ToString(), scanner.ModsFolder, _baseDirectory);
        string? outDir = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(outDir))
            Directory.CreateDirectory(outDir);
        File.WriteAllText(reportPath, sanitizedContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new DiagnosticReportOutput
        {
            ReportPath = reportPath,
            Content = sanitizedContent,
            TotalUnits = modUnits.Count,
            MatchedUrlCount = matchedSuccessRows.Count,
            FailedOrManualCount = failedOrUnmatchedRows.Count,
            ExcludedTopCount = diag.ExcludedTopFolders.Count,
            ExcludedSubCount = diag.ExcludedSubfolders.Count,
            MultiScriptCount = diag.MultiScriptFolders.Count,
            MergedUnitCount = diag.MergedUnitFiles.Count,
            UnaccountedCount = diag.UnaccountedFiles.Count
        };
    }

    private static IEnumerable<string> FormatMetadataForReport(ModMetadata? meta)
    {
        if (meta is null || (!meta.HasMetadata && string.IsNullOrEmpty(meta.InferredMod) && meta.Creators.Count == 0 && meta.RootTunings.Count == 0 && meta.Modules.Count == 0))
            return ["      - 내부 메타데이터: 추출 결과 없음"];

        return
        [
            $"      - has_metadata (내부 단서 존재 여부): {meta.HasMetadata}",
            $"      - manifest_name (ModFileManifest 모드명): {(string.IsNullOrEmpty(meta.ManifestName) ? "(없음)" : meta.ManifestName)}",
            $"      - creators (명시적 제작자 태그): {(meta.Creators.Count > 0 ? string.Join(", ", meta.Creators) : "(없음)")}",
            $"      - inferred_creator (추론된 제작자): {(string.IsNullOrEmpty(meta.InferredCreator) ? "(없음)" : meta.InferredCreator)}",
            $"      - inferred_mod (추론된 모드명): {(string.IsNullOrEmpty(meta.InferredMod) ? "(없음)" : meta.InferredMod)}",
            $"      - version (파일 내장 버전): {(string.IsNullOrEmpty(meta.Version) ? "(없음)" : meta.Version)}",
            $"      - urls (파일 내장 URL): {(meta.Urls.Count > 0 ? string.Join(", ", meta.Urls) : "(없음)")}",
            $"      - modules (.ts4script 모듈명): {(meta.Modules.Count > 0 ? string.Join(", ", meta.Modules.Take(10)) : "(없음)")}",
            $"      - root_tunings (.package 루트 튜닝 식별자): {(meta.RootTunings.Count > 0 ? string.Join(", ", meta.RootTunings.Take(10)) : "(없음)")}",
            $"      - display_names (내부 표시 이름): {(meta.DisplayNames.Count > 0 ? string.Join(", ", meta.DisplayNames.Take(6)) : "(없음)")}",
            $"      - is_animation_addon (애니메이션 애드온 여부): {(meta.IsAnimationAddon ? "예 (애니메이션/모션 애드온)" : "아니오")}"
        ];
    }

    private static List<string> BuildFailureReasonsForReport(ModCheckResult r, ModMetadata meta)
    {
        var reasons = new List<string>();
        FinderDiagnostics fDiag = r.FinderDiagnostics;
        string pageUrl = r.PageUrl ?? string.Empty;
        string localVer = r.LocalVersionDisplay ?? string.Empty;
        string remoteVer = r.LatestVersion ?? string.Empty;
        string verifNote = r.VerificationNote ?? string.Empty;

        if (r.StatusCode == ModUpdateStatusCode.Error || !string.IsNullOrEmpty(r.ErrorMessage))
        {
            reasons.Add($"[네트워크/스크래퍼 오류] 조회 중 예외가 발생했습니다: {(!string.IsNullOrEmpty(r.ErrorMessage) ? r.ErrorMessage : r.StatusLabel)}");
            return reasons;
        }

        if (r.LinkNotFound || string.IsNullOrEmpty(pageUrl) || remoteVer == "링크를 찾을 수 없습니다.")
        {
            reasons.Add($"[카탈로그 미일치] known_catalog에서 로컬 파일명('{r.ModName}'), 폴더명('{r.FolderName}'), 내부 모드명('{FirstNonEmpty(meta.ManifestName, meta.InferredMod, "-")}')과 일치하는 항목을 찾지 못함.");
            if (meta.Urls.Count == 0)
                reasons.Add("[파일 내장 URL 없음] 바이너리/XML/매니페스트를 검사했으나 제작자가 파일 내부에 심어둔 배포처 URL이 없음.");

            string tokC = string.IsNullOrEmpty(fDiag.TokenizedCreator) ? "(미식별)" : fDiag.TokenizedCreator;
            string tokM = string.IsNullOrEmpty(fDiag.TokenizedModTitle) ? "(미식별)" : fDiag.TokenizedModTitle;
            string tokKws = fDiag.TokenizedKeywords.Count > 0 ? string.Join(", ", fDiag.TokenizedKeywords) : "(없음)";
            reasons.Add($"[스마트 토큰화 분석] 추론 제작자='{tokC}', 추론 모드명='{tokM}', 검색 키워드=[{tokKws}]");

            reasons.Add(!string.IsNullOrEmpty(fDiag.FailureReason)
                ? $"[웹 검색 실패 사유] {fDiag.FailureReason}"
                : "[웹 검색 실패 사유] 신뢰 가능한 배포 도메인에서 오탐 방지 기준(유사도 85점 이상)을 충족하는 공식 배포 페이지를 찾지 못함.");

            foreach (RejectedSearchCandidate rej in fDiag.RejectedSamples.Take(3))
                reasons.Add($"  ↳ [기각된 후보 ({rej.Score}점)] [{rej.Label}] {rej.Title} ({rej.Url}) — 사유: {rej.Reason}");

            return reasons;
        }

        if (string.IsNullOrEmpty(localVer) || localVer == "버전 미표기")
        {
            reasons.Add($"[로컬 버전 미검출] 로컬 파일명('{r.ModName}'), 폴더명('{r.FolderName}'), 내부 메타데이터(version='{meta.Version}'), 매니페스트 어디에도 버전 숫자나 날짜가 표기되어 있지 않아 로컬 버전을 특정할 수 없음.");
        }

        if (r.IsPaid)
            reasons.Add($"[유료 상품 제한] 매칭된 배포처({pageUrl})는 유료 상품이어서 비로그인 상태에서 첨부파일 및 최신 버전 자동 확인/다운로드가 불가능함.");
        else if (r.IsEarlyAccess)
            reasons.Add($"[얼리 액세스/선배포 제한] 매칭된 배포처({pageUrl})가 현재 후원자 전용 선배포 상태여서 비로그인 상태에서 첨부파일/버전 접근이 제한됨.");
        else if (remoteVer is "-" or "수동 확인 필요" || remoteVer.Contains("수동 확인", StringComparison.Ordinal))
            reasons.Add($"[원격 버전 미검출] 배포처 URL({pageUrl})에는 정상 연결되었으나 웹페이지에서 버전/파일이 추출되지 않거나 보호(Cloudflare/로그인 등)가 적용되어 있음.");

        if (verifNote.Contains("자동 다운로드 보류", StringComparison.Ordinal))
            reasons.Add($"[원격 파일 유사도 안전 보류] {verifNote}");

        if (reasons.Count == 0)
            reasons.Add($"[버전 대조 미확정] 로컬 버전('{localVer}')과 웹 최신 버전('{remoteVer}') 간 자동 비교를 확정할 수 없어 배포처 수동 확인 권장 상태로 분류됨.");

        return reasons;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return string.Empty;
    }
}
