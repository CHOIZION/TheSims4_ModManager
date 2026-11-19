using System.IO;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 심즈 4 Mods 폴더 스캔·그룹화·중복 파일 감지·1단계 깊이 제한 검사·진단 수집 서비스
/// </summary>
public sealed class ModScannerService
{
    private static readonly Regex CcFolderTokenPattern = new(
        @"(?:^|[\s_\-\[\(0-9\.])(CAS|BUILD|PRESETS?|SLIDERS?|POSES?|DEFAULTS?|CC|WW\s*CC|HAIR|CLOTH(?:ES|ING)?|MAKEUP|SKIN(?:DETAILS?)?|SHOES|ACC(?:ESSORIES)?|FURNITURE|BUY|TATTOOS?|EYES?|EYEBROWS?|LASH(?:ES)?|NAILS?)(?:$|[\s_\-\]\)])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] CcFolderKoreanKeywords =
    [
        "헤어", "의상", "메이크업", "스킨", "포즈", "슬라이더", "건축", "가구",
        "신발", "악세", "프리셋", "디폴트", "눈썹", "렌즈", "타투", "네일",
        "속눈썹", "화장", "모자", "상의", "하의", "전신", "벽지", "바닥재"
    ];

    private static readonly string[] TranslationFolderKeywords =
    [
        "PRIO", "한글", "번역", "한패", "KOR", "KOREAN", "TRANSLAT", "OVERRIDE", "오버라이드"
    ];

    private static readonly HashSet<string> AuxSubfolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cc", "optional", "options", "addons", "add-ons", "addon",
        "extras", "extras & overrides", "presets",
        "core", "merged", "modules", "backup", "backups", "old",
        "옵션", "선택", "선택사항", "추가", "백업", "구버전"
    };

    private static readonly string[] AuxSubfolderContains =
    [
        "preset", "slider", "pose",
        "optional", "addon", "add-on", "backup", "옵션", "구버전", "백업"
    ];

    private static readonly HashSet<string> KnownCreatorsTwoPart = new(StringComparer.OrdinalIgnoreCase)
    {
        "littlemssam", "lumpinou", "natethel0ser", "polarbearsims",
        "pandasama", "spinningplumbobs", "turbodriver", "weerbesu", "roburky", "lotharihoe",
        "simmattically", "simkatu", "simplyanjuta", "sims4me",
        "chingyu", "kiarasims", "adeepindigo", "lot51"
    };

    private static readonly string[] OptionalSecondaryKeywords =
    [
        "optional", "addon", "add-on", "updater", "ringer",
        "sounds", "test_users", "nocooking", "cannotdie",
        "costfix", "gotosleep", "hiremore", "lockedstats",
        "noautocooking", "noautoputaway", "nogardening",
        "secondnanny"
    ];

    private static readonly EnumerationOptions RecursiveEnumOpts = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System
    };

    private static readonly EnumerationOptions ShallowEnumOpts = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true
    };

    private readonly string _modsFolder;
    private readonly CatalogService _catalog;

    public ModScannerService(string modsFolder, CatalogService? catalog = null)
    {
        _modsFolder = modsFolder;
        _catalog = catalog ?? new CatalogService();
    }

    public string ModsFolder => _modsFolder;

    public static bool IsCcFolderName(string folderName)
    {
        string nameStrip = folderName.Trim();
        string nameUpper = nameStrip.ToUpperInvariant();

        if (Regex.IsMatch(nameUpper, @"^(?:[\d\-]+[\.\s_\-]*)?(?:MOD|GSM|GM)(?:[\s_\-]*\[?\d+\]?)?(?:[\s_\-]|$)") ||
            nameUpper.Contains("MCCC", StringComparison.Ordinal) ||
            nameUpper.Contains("COMMAND CENTER", StringComparison.Ordinal) ||
            nameUpper.Contains("OVERRIDE", StringComparison.Ordinal))
        {
            return false;
        }

        if (Regex.IsMatch(nameUpper, @"^(?:AA|BB|CC|DD)(?:[\s_\-]|$)"))
            return true;

        if (CcFolderTokenPattern.IsMatch(nameUpper))
            return true;

        return CcFolderKoreanKeywords.Any(kw => nameStrip.Contains(kw, StringComparison.Ordinal));
    }

    public static bool IsTranslationFolderOrFiles(string folderName, IReadOnlyList<string> packageFilePaths)
    {
        string nameUpper = folderName.ToUpperInvariant();
        if (TranslationFolderKeywords.Any(kw => nameUpper.Contains(kw, StringComparison.Ordinal)))
            return true;

        foreach (string pkgPath in packageFilePaths.Take(10))
        {
            string pLow = Path.GetFileName(pkgPath).ToLowerInvariant();
            if (pLow.Contains("!wamtik", StringComparison.Ordinal) ||
                pLow.Contains("_wamtik", StringComparison.Ordinal) ||
                pLow.Contains("personalko", StringComparison.Ordinal) ||
                pLow.Contains("한글", StringComparison.Ordinal) ||
                pLow.Contains("번역", StringComparison.Ordinal) ||
                pLow.Contains("한패", StringComparison.Ordinal) ||
                pLow.Contains("korean", StringComparison.Ordinal) ||
                pLow.Contains("_kor", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsCcOrAuxSubfolder(string dirName)
    {
        string nameLow = dirName.Trim().ToLowerInvariant();
        if (AuxSubfolderNames.Contains(nameLow))
            return true;

        return AuxSubfolderContains.Any(k => nameLow.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>
    /// Mods 폴더 전체 개요(.package/.ts4script 수, 깊이 초과 경고)와 폴더·유닛 목록을 스캔합니다.
    /// </summary>
    public ModsScanResult Scan()
    {
        if (string.IsNullOrWhiteSpace(_modsFolder) || !Directory.Exists(_modsFolder))
        {
            return new ModsScanResult
            {
                Exists = false,
                ModsFolder = _modsFolder,
                Warnings = ["Mods 폴더가 존재하지 않습니다."]
            };
        }

        int packageCount = 0;
        int scriptCount = 0;
        var warnings = new List<string>();

        foreach (string filePath in Directory.EnumerateFiles(_modsFolder, "*", RecursiveEnumOpts))
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".package")
            {
                packageCount++;
            }
            else if (ext == ".ts4script")
            {
                scriptCount++;
                string relPath = Path.GetRelativePath(_modsFolder, filePath);
                string[] parts = relPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 2)
                {
                    warnings.Add(
                        $"[주의] 스크립트 모드 깊이 초과: {relPath} " +
                        "(심즈 4는 .ts4script 파일을 최대 1단계 하위 폴더까지만 지원합니다)");
                }
            }
        }

        var (folderMods, standaloneFiles, modUnits) = ScanUserModsInternal();

        return new ModsScanResult
        {
            Exists = true,
            ModsFolder = _modsFolder,
            PackageCount = packageCount,
            ScriptCount = scriptCount,
            Warnings = warnings,
            FolderMods = folderMods,
            StandaloneFiles = standaloneFiles,
            ModUnits = modUnits
        };
    }

    public List<ModUnit> ScanUpdatableModUnits()
    {
        if (string.IsNullOrWhiteSpace(_modsFolder) || !Directory.Exists(_modsFolder))
            return new List<ModUnit>();

        var allUnits = new List<ModUnit>();

        // 1. 1단계 폴더 및 내부 비-CC 하위 폴더 스캔
        foreach (string topDir in GetSortedTopDirectories())
        {
            bool anyMod = Directory.EnumerateFiles(topDir, "*", RecursiveEnumOpts)
                .Any(f => IsModExt(Path.GetExtension(f)));
            if (!anyMod)
                continue;

            string topName = Path.GetFileName(topDir);
            if (IsCcFolderName(topName))
                continue;

            var dirsToScan = new List<string> { topDir };
            foreach (string subDir in Directory.EnumerateDirectories(topDir, "*", RecursiveEnumOpts)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string relToTop = Path.GetRelativePath(topDir, subDir);
                string[] relParts = relToTop.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                if (relParts.Any(pt => AuxSubfolderNames.Contains(pt) || IsCcFolderName(pt)))
                    continue;
                if (IsCcOrAuxSubfolder(Path.GetFileName(subDir)))
                    continue;

                dirsToScan.Add(subDir);
            }

            foreach (string d in dirsToScan)
            {
                var dFiles = Directory.EnumerateFiles(d, "*", ShallowEnumOpts)
                    .Where(f => IsModExt(Path.GetExtension(f)))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (dFiles.Count == 0)
                    continue;

                allUnits.AddRange(GroupDirectoryIntoUnits(d, dFiles, isStandaloneRoot: false));
            }
        }

        // 2. Mods 루트 직속 단일 파일 스캔
        var rootStandalone = Directory.EnumerateFiles(_modsFolder, "*", ShallowEnumOpts)
            .Where(f => !Path.GetFileName(f).Equals("resource.cfg", StringComparison.OrdinalIgnoreCase) &&
                        IsModExt(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (rootStandalone.Count > 0)
            allUnits.AddRange(GroupDirectoryIntoUnits(_modsFolder, rootStandalone, isStandaloneRoot: true));

        return allUnits;
    }

    /// <summary>
    /// 지정한 특정 폴더(예: Mods\test 또는 외부 폴더) 내부의 모든 .package/.ts4script 파일을 유닛 단위로 스캔합니다.
    /// CC 이름 규칙으로 하위 폴더를 건너뛰지 않으며 Resource.cfg는 제외합니다.
    /// </summary>
    public List<ModUnit> ScanFolderAsUnits(string targetFolder)
    {
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            return new List<ModUnit>();

        var units = new List<ModUnit>();
        var dirs = new List<string> { targetFolder };
        dirs.AddRange(Directory.EnumerateDirectories(targetFolder, "*", RecursiveEnumOpts)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));

        foreach (string d in dirs)
        {
            var dFiles = Directory.EnumerateFiles(d, "*", ShallowEnumOpts)
                .Where(f => !Path.GetFileName(f).Equals("resource.cfg", StringComparison.OrdinalIgnoreCase) &&
                            IsModExt(Path.GetExtension(f)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (dFiles.Count > 0)
                units.AddRange(GroupDirectoryIntoUnits(d, dFiles, isStandaloneRoot: false));
        }

        return units;
    }

    public ScanDiagnostics CollectScanDiagnostics()
    {
        if (string.IsNullOrWhiteSpace(_modsFolder) || !Directory.Exists(_modsFolder))
            return new ScanDiagnostics { Exists = false };

        var (folderMods, _, modUnits) = ScanUserModsInternal();
        var diag = new ScanDiagnostics
        {
            Exists = true,
            FolderMods = folderMods,
            ModUnits = modUnits
        };

        var accountedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string topDir in GetSortedTopDirectories())
        {
            string topName = Path.GetFileName(topDir);
            var allScripts = new List<string>();
            var allPkgs = new List<string>();
            var otherFiles = new List<string>();

            foreach (string f in Directory.EnumerateFiles(topDir, "*", RecursiveEnumOpts))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".ts4script") allScripts.Add(f);
                else if (ext == ".package") allPkgs.Add(f);
                else otherFiles.Add(Path.GetFileName(f));
            }

            if (allScripts.Count == 0 && allPkgs.Count == 0)
            {
                diag.ExcludedTopFolders.Add(new ExcludedTopFolderDiag
                {
                    FolderName = topName,
                    Path = topDir,
                    PackageCount = 0,
                    ScriptCount = 0,
                    Reason = $"내부에 .package 또는 .ts4script 모드 파일이 존재하지 않아 업데이트 목록에서 제외됨 (기타 파일 {otherFiles.Count}개)",
                    SampleFiles = otherFiles.Take(10).ToList()
                });
                continue;
            }

            if (IsCcFolderName(topName))
            {
                string nameUp = topName.Trim().ToUpperInvariant();
                string ruleDesc = Regex.IsMatch(nameUp, @"^(?:AA|BB|CC|DD)(?:[\s_\-]|$)")
                    ? "바람별 폴더 구조 CC 제외 규칙(AA/BB/CC/DD 접두어)"
                    : nameUp is "4 HIGH PRIORITY CC" or "6 SLIDER" or "7 CC"
                        ? "응애 스타터팩 CC/슬라이더 제외 폴더 규칙(4, 6, 7번)"
                        : "외형/건축/포즈/슬라이더 CC 전용 키워드 규칙";

                string reasonStr = $".package 전용(CC/외형·건축) 폴더로 분류되어 업데이트 목록에서 제외됨 [{ruleDesc}] (.package {allPkgs.Count}개, .ts4script {allScripts.Count}개)";
                if (allScripts.Count > 0)
                    reasonStr += $" ⚠️ [주의: CC 폴더 내에 .ts4script {allScripts.Count}개({string.Join(", ", allScripts.Select(Path.GetFileName))})가 포함되어 있습니다]";

                diag.ExcludedTopFolders.Add(new ExcludedTopFolderDiag
                {
                    FolderName = topName,
                    Path = topDir,
                    PackageCount = allPkgs.Count,
                    ScriptCount = allScripts.Count,
                    Reason = reasonStr,
                    SampleFiles = allScripts.Concat(allPkgs).Take(10).Select(Path.GetFileName).Where(n => n is not null).ToList()!
                });
                diag.PackageOnlyFolders.Add(new PackageOnlyFolderDiag
                {
                    FolderName = topName,
                    Status = "업데이트 목록에서 제외됨 (CC 전용 폴더)",
                    PackageCount = allPkgs.Count,
                    ScriptCount = allScripts.Count,
                    UnitCount = 0,
                    Reason = reasonStr
                });

                foreach (string f in allScripts.Concat(allPkgs))
                    accountedFilePaths.Add(Path.GetFullPath(f));
                continue;
            }

            var topUnits = modUnits
                .Where(u => !u.IsStandalone &&
                            (u.FolderName == topName ||
                             u.FolderName.StartsWith(topName + "\\", StringComparison.OrdinalIgnoreCase) ||
                             u.FolderName.StartsWith(topName + "/", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (allScripts.Count == 0 && allPkgs.Count > 0)
            {
                string modKind = IsTranslationFolderOrFiles(topName, allPkgs) ? "한글화 / 우선순위" : "패키지 모드";
                int omittedPkgCnt = Math.Max(0, allPkgs.Count - topUnits.Count);
                string pkgNote = $".package 전용 폴더(.ts4script 0개, .package {allPkgs.Count}개)로 분류됨 — [{modKind}]로 스캔에 포함되어 {topUnits.Count}개 개별 모드로 감지됨.";
                if (omittedPkgCnt > 0)
                    pkgNote += $" (단, 폴더 내 .package {allPkgs.Count}개 중 대표 파일 {topUnits.Count}개 외 [{omittedPkgCnt}]개는 동일 모드 부속/번들 패키지로 병합되어 개별 목록에서 생략됨)";

                diag.PackageOnlyFolders.Add(new PackageOnlyFolderDiag
                {
                    FolderName = topName,
                    Status = $"스캔 포함 ({modKind}, {topUnits.Count}개 모드)",
                    PackageCount = allPkgs.Count,
                    ScriptCount = 0,
                    UnitCount = topUnits.Count,
                    Reason = pkgNote
                });
            }

            var dirsToScan = new List<string> { topDir };
            foreach (string sub in Directory.EnumerateDirectories(topDir, "*", RecursiveEnumOpts)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string relPath = Path.GetRelativePath(_modsFolder, sub);
                string relToTop = Path.GetRelativePath(topDir, sub);
                string[] relParts = relToTop.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                var subFilesDirect = Directory.EnumerateFiles(sub, "*", ShallowEnumOpts)
                    .Where(f => IsModExt(Path.GetExtension(f)))
                    .ToList();
                var subPkgs = subFilesDirect.Where(f => Path.GetExtension(f).Equals(".package", StringComparison.OrdinalIgnoreCase)).ToList();
                var subScrs = subFilesDirect.Where(f => Path.GetExtension(f).Equals(".ts4script", StringComparison.OrdinalIgnoreCase)).ToList();

                bool isAux = relParts.Any(pt => AuxSubfolderNames.Contains(pt) || IsCcFolderName(pt)) ||
                             IsCcOrAuxSubfolder(Path.GetFileName(sub));

                if (isAux)
                {
                    if (subFilesDirect.Count > 0)
                    {
                        diag.ExcludedSubfolders.Add(new ExcludedSubfolderDiag
                        {
                            FolderName = relPath,
                            Path = sub,
                            PackageCount = subPkgs.Count,
                            ScriptCount = subScrs.Count,
                            Files = subFilesDirect.Select(Path.GetFileName).Where(n => n is not null).ToList()!,
                            Reason = $"모드 폴더 내부의 보조/옵션/백업/CC 전용 하위폴더('{Path.GetFileName(sub)}')로 분류되어 업데이트 스캔에서 제외됨 (.package {subPkgs.Count}개, .ts4script {subScrs.Count}개)"
                        });
                        foreach (string f in subFilesDirect)
                            accountedFilePaths.Add(Path.GetFullPath(f));
                    }
                    continue;
                }

                dirsToScan.Add(sub);
            }

            foreach (string d in dirsToScan)
            {
                var dScrs = Directory.EnumerateFiles(d, "*.ts4script", ShallowEnumOpts)
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (dScrs.Count >= 2)
                {
                    string relDStr = Path.GetRelativePath(_modsFolder, d);
                    string repScr = SelectPrimaryFile(dScrs);
                    string repName = Path.GetFileName(repScr);
                    var otherScrs = dScrs.Select(Path.GetFileName).Where(n => n != repName).ToList()!;
                    var dUnits = modUnits.Where(u => u.FolderName == relDStr && u.ScriptCount > 0).ToList();
                    var unitPrimaryScrs = dUnits.Select(u => u.PrimaryFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var stillOmittedScrs = dScrs.Select(Path.GetFileName).Where(n => n is not null && !unitPrimaryScrs.Contains(n)).ToList()!;

                    string unitDetail = stillOmittedScrs.Count > 0
                        ? $"개별 모드 유닛 스캔에서도 동일 모드 그룹으로 묶여 대표 {unitPrimaryScrs.Count}개 외 .ts4script [{stillOmittedScrs.Count}]개({string.Join(", ", stillOmittedScrs)})가 부속 파일로 병합·생략됨"
                        : $"폴더 단위 보기에서는 대표 1개('{repName}') 외 [{otherScrs.Count}]개가 가려지나, 개별 모드 유닛(mod_units) 스캔에서는 {dUnits.Count}개 모드로 모두 개별 분리됨";

                    diag.MultiScriptFolders.Add(new MultiScriptFolderDiag
                    {
                        FolderName = relDStr,
                        TotalScripts = dScrs.Count,
                        RepresentativeScript = repName,
                        OtherScripts = otherScrs!,
                        OmittedInFolderViewCount = otherScrs.Count,
                        OmittedInUnitView = stillOmittedScrs!,
                        Reason = $"한 폴더('{relDStr}') 안에 {dScrs.Count}개의 .ts4script가 들어있어 폴더 대표 1개('{repName}') 외 [{otherScrs.Count}]개가 폴더 대표 뒤에 병합됨 ({unitDetail})"
                    });
                }
            }
        }

        // 2) 병합된 부속 파일 기록
        foreach (ModUnit u in modUnits)
        {
            foreach (string uf in u.UnitFiles)
            {
                if (File.Exists(uf))
                    accountedFilePaths.Add(Path.GetFullPath(uf));
            }

            if (u.UnitFiles.Count > 1)
            {
                string primaryName = u.PrimaryFileName;
                var omittedAll = u.UnitFiles.Select(Path.GetFileName).Where(n => n is not null && !string.Equals(n, primaryName, StringComparison.OrdinalIgnoreCase)).ToList();
                var omittedScrs = u.UnitFiles.Where(f => !string.Equals(Path.GetFileName(f), primaryName, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(f).Equals(".ts4script", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName).Where(n => n is not null).ToList()!;
                var omittedPkgs = u.UnitFiles.Where(f => !string.Equals(Path.GetFileName(f), primaryName, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(f).Equals(".package", StringComparison.OrdinalIgnoreCase))
                    .Select(Path.GetFileName).Where(n => n is not null).ToList()!;

                string reasonText = omittedScrs.Count > 0
                    ? $"한 폴더/모드 그룹('{u.FolderName}') 안에 총 {u.ScriptCount}개의 .ts4script가 들어있어 대표 1개('{primaryName}') 외 .ts4script [{omittedScrs.Count}]개({string.Join(", ", omittedScrs)})"
                      + (omittedPkgs.Count > 0 ? $" 및 부속 .package [{omittedPkgs.Count}]개" : "")
                      + "가 동일 모드로 묶여 개별 검색 목록에서 제외됨"
                    : $"모드 그룹('{u.FolderName}') 내 대표 파일 1개('{primaryName}') 외 부속 .package [{omittedPkgs.Count}]개가 동일 모드 구성 파일로 병합되어 개별 검색 목록에서 제외됨";

                diag.MergedUnitFiles.Add(new MergedUnitFilesDiag
                {
                    FolderName = u.FolderName,
                    PrimaryFile = primaryName,
                    TotalUnitFiles = u.UnitFiles.Count,
                    OmittedCount = omittedAll.Count,
                    OmittedScripts = omittedScrs!,
                    OmittedPackages = omittedPkgs!,
                    Reason = reasonText
                });
            }
        }

        // 3) 루트 단일 파일 진단
        var standaloneUnits = modUnits.Where(u => u.IsStandalone).ToList();
        foreach (string item in Directory.EnumerateFiles(_modsFolder, "*", ShallowEnumOpts)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(item);
            string fLow = name.ToLowerInvariant();
            string ext = Path.GetExtension(item).ToLowerInvariant();
            if (fLow == "resource.cfg" || !IsModExt(ext))
            {
                diag.RootFilesDiagnostics.Add(new RootFileDiag
                {
                    FileName = name,
                    Status = "스캔 제외 (비모드/시스템 파일)",
                    Reason = "Mods 루트 폴더의 단일 설정/캐시/비모드 파일이라서 스캔에서 제외됨"
                });
                continue;
            }

            var matchedU = standaloneUnits.FirstOrDefault(u =>
                u.UnitFiles.Any(up => string.Equals(Path.GetFileName(up), fLow, StringComparison.OrdinalIgnoreCase)));
            if (matchedU is not null)
            {
                if (string.Equals(matchedU.PrimaryFileName, fLow, StringComparison.OrdinalIgnoreCase))
                {
                    diag.RootFilesDiagnostics.Add(new RootFileDiag
                    {
                        FileName = name,
                        Status = "개별 모드 유닛 스캔 포함 (폴더 목록에서는 제외)",
                        Reason = "루트 폴더(Mods/)의 단일 파일이라서 1단계 하위폴더 목록에서는 제외됨 → 개별 모드 유닛 스캔에서는 '(Mods 루트)' 소속 대표 모드로 정상 포함됨"
                    });
                }
                else
                {
                    int otherCnt = matchedU.UnitFiles.Count - 1;
                    diag.RootFilesDiagnostics.Add(new RootFileDiag
                    {
                        FileName = name,
                        Status = $"루트 대표 파일('{matchedU.PrimaryFileName}')에 병합됨",
                        Reason = $"루트 폴더(Mods/)의 단일 파일이며, 동일 모드 대표 파일 1개('{matchedU.PrimaryFileName}') 외 [{otherCnt}]개 부속 파일로 묶여 개별 검색에서 제외됨"
                    });
                }
            }
            else
            {
                diag.RootFilesDiagnostics.Add(new RootFileDiag
                {
                    FileName = name,
                    Status = "누락됨",
                    Reason = "루트 폴더의 단일 파일이나 스캔 유닛에 매핑되지 않음 (점검 필요)"
                });
            }
        }

        // 4) 전수 대조 (사각지대 검증)
        foreach (string f in Directory.EnumerateFiles(_modsFolder, "*", RecursiveEnumOpts)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(f);
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (name.Equals("resource.cfg", StringComparison.OrdinalIgnoreCase) || !IsModExt(ext))
                continue;

            string full = Path.GetFullPath(f);
            if (!accountedFilePaths.Contains(full))
            {
                diag.UnaccountedFiles.Add(new UnaccountedFileDiag
                {
                    RelativePath = Path.GetRelativePath(_modsFolder, f),
                    FullPath = f,
                    Reason = "스캔 포함 목록 및 제외 규칙 목록 어디에도 매핑되지 않은 미분류 파일"
                });
            }
        }

        return diag;
    }

    public ModInstalledInfo DetectModInstalled(ModQuery mod)
    {
        if (string.IsNullOrWhiteSpace(_modsFolder) || !Directory.Exists(_modsFolder))
            return new ModInstalledInfo();

        if (mod.IsStandalone || mod.UnitFiles.Count > 0)
        {
            var unitPaths = mod.UnitFiles.Where(File.Exists).ToList();
            if (unitPaths.Count == 0 && !string.IsNullOrEmpty(mod.FilePath) && File.Exists(mod.FilePath))
                unitPaths.Add(mod.FilePath);
            if (unitPaths.Count == 0 && mod.IsStandalone)
            {
                unitPaths = Directory.EnumerateFiles(_modsFolder, "*", ShallowEnumOpts)
                    .Where(f => IsModExt(Path.GetExtension(f)))
                    .ToList();
            }

            if (unitPaths.Count > 0)
            {
                var pkgs = unitPaths.Where(p => Path.GetExtension(p).Equals(".package", StringComparison.OrdinalIgnoreCase)).ToList();
                var scrs = unitPaths.Where(p => Path.GetExtension(p).Equals(".ts4script", StringComparison.OrdinalIgnoreCase)).ToList();
                var unitLocations = unitPaths
                    .Select(Path.GetDirectoryName)
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()!;
                if (unitLocations.Count == 0)
                    unitLocations.Add(_modsFolder);

                var duplicates = FindDuplicates(pkgs.Concat(scrs));

                return new ModInstalledInfo
                {
                    Installed = pkgs.Count > 0 || scrs.Count > 0,
                    HasPackage = pkgs.Count > 0,
                    HasScript = scrs.Count > 0,
                    Packages = pkgs,
                    Scripts = scrs,
                    Locations = unitLocations!,
                    TargetDirs = unitLocations!,
                    Duplicates = duplicates
                };
            }
        }

        var folderKeywords = mod.FolderKeywords.ToList();
        if (folderKeywords.Count == 0)
        {
            string modLow = mod.Name.ToLowerInvariant();
            if (modLow.Contains("command center", StringComparison.Ordinal) || modLow.Contains("mccc", StringComparison.Ordinal))
                folderKeywords = ["MOD MC Command Center", "MOD MCCC"];
            else if (modLow.Contains("wonderful", StringComparison.Ordinal))
                folderKeywords = ["MOD WonderfulWhims", "WonderfulWhims"];
            else if (modLow.Contains("한글", StringComparison.Ordinal) || modLow.Contains("바람별", StringComparison.Ordinal))
                folderKeywords = ["PRIO"];
            else if (!string.IsNullOrWhiteSpace(mod.Name))
                folderKeywords = [mod.Name];
        }

        var cleanFileKeywords = mod.FileKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.ToLowerInvariant())
            .ToList();

        var cleanKeywords = folderKeywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => Regex.Replace(k.ToLowerInvariant(), @"[^a-zA-Z0-9가-힣]", ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var targetDirs = new List<string>();
        string specificFolder = !string.IsNullOrEmpty(mod.InstallFolder) ? mod.InstallFolder : mod.FolderName;
        if (!string.IsNullOrEmpty(specificFolder) && specificFolder != "(Mods 루트)")
        {
            string candidate = Path.Combine(_modsFolder, specificFolder);
            if (Directory.Exists(candidate))
                targetDirs.Add(candidate);
        }

        if (targetDirs.Count == 0 && !string.IsNullOrEmpty(mod.DirectoryPath) && Directory.Exists(mod.DirectoryPath))
        {
            targetDirs.Add(mod.DirectoryPath);
        }

        if (targetDirs.Count == 0 && cleanKeywords.Count > 0)
        {
            foreach (string d in Directory.EnumerateDirectories(_modsFolder, "*", RecursiveEnumOpts))
            {
                string cleanDirName = Regex.Replace(Path.GetFileName(d).ToLowerInvariant(), @"[^a-zA-Z0-9가-힣]", "");
                if (cleanKeywords.Contains(cleanDirName))
                    targetDirs.Add(d);
            }
        }

        var matchedPackages = new List<string>();
        var matchedScripts = new List<string>();
        var locations = new List<string>();

        foreach (string targetDir in targetDirs)
        {
            locations.Add(targetDir);
            var sortedItems = Directory.EnumerateFiles(targetDir, "*", RecursiveEnumOpts)
                .OrderBy(p => Path.GetRelativePath(targetDir, p).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length)
                .ThenBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);

            foreach (string item in sortedItems)
            {
                string ext = Path.GetExtension(item).ToLowerInvariant();
                if (!IsModExt(ext))
                    continue;

                if (cleanFileKeywords.Count > 0)
                {
                    string itemNameLow = Path.GetFileName(item).ToLowerInvariant();
                    if (!cleanFileKeywords.Any(fkw => itemNameLow.StartsWith(fkw, StringComparison.Ordinal) || itemNameLow.Contains(fkw, StringComparison.Ordinal)))
                        continue;
                }

                if (ext == ".package") matchedPackages.Add(item);
                else if (ext == ".ts4script") matchedScripts.Add(item);
            }
        }

        bool hasPkg = matchedPackages.Count > 0;
        bool hasScr = matchedScripts.Count > 0;
        var allDuplicates = FindDuplicates(matchedPackages.Concat(matchedScripts));

        return new ModInstalledInfo
        {
            Installed = hasPkg || hasScr,
            HasPackage = hasPkg,
            HasScript = hasScr,
            Packages = matchedPackages,
            Scripts = matchedScripts,
            Locations = locations,
            TargetDirs = targetDirs,
            Duplicates = allDuplicates
        };
    }

    public (List<ModFileItem> Packages, List<ModFileItem> Scripts, List<ModFileItem> AllFiles, List<string> Warnings) ScanAllModFiles(
        string targetFolder,
        bool rootOnly = false)
    {
        var packages = new List<ModFileItem>();
        var scripts = new List<ModFileItem>();
        var allFiles = new List<ModFileItem>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            return (packages, scripts, allFiles, warnings);

        var opts = rootOnly ? ShallowEnumOpts : RecursiveEnumOpts;
        foreach (string item in Directory.EnumerateFiles(targetFolder, "*", opts).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(item);
            if (name.Equals("resource.cfg", StringComparison.OrdinalIgnoreCase))
                continue;

            string ext = Path.GetExtension(item).ToLowerInvariant();
            if (!IsModExt(ext))
                continue;

            string relPath = Path.GetRelativePath(targetFolder, item);
            string? parentRel = Path.GetDirectoryName(relPath);
            string locationStr = string.IsNullOrEmpty(parentRel) || parentRel == "." ? "." : parentRel;
            long sizeBytes = new FileInfo(item).Length;

            var fileInfo = new ModFileItem
            {
                Name = name,
                Extension = ext,
                Location = locationStr,
                RelativePath = relPath,
                FullPath = item,
                SizeBytes = sizeBytes
            };

            allFiles.Add(fileInfo);
            if (ext == ".package")
            {
                packages.Add(fileInfo);
            }
            else if (ext == ".ts4script")
            {
                scripts.Add(fileInfo);
                if (locationStr != ".")
                {
                    warnings.Add(
                        $"스크립트 파일 '{name}'이(가) 하위 폴더({locationStr})에 있습니다. " +
                        "심즈 4는 서브폴더 깊이가 1단계를 초과하면 스크립트를 인식하지 못할 수 있습니다.");
                }
            }
        }

        return (packages, scripts, allFiles, warnings);
    }

    public List<SubfolderInfo> ScanSubfolders(string targetFolder)
    {
        var results = new List<SubfolderInfo>();
        if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            return results;

        var rootFiles = Directory.EnumerateFiles(targetFolder, "*", ShallowEnumOpts)
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;

        results.Add(new SubfolderInfo
        {
            FolderName = Path.GetFileName(targetFolder) + " (루트)",
            RelativePath = ".",
            FullPath = targetFolder,
            Files = rootFiles!
        });

        foreach (string subDir in Directory.EnumerateDirectories(targetFolder, "*", RecursiveEnumOpts)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var subFiles = Directory.EnumerateFiles(subDir, "*", ShallowEnumOpts)
                .Select(Path.GetFileName)
                .Where(n => n is not null)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList()!;

            results.Add(new SubfolderInfo
            {
                FolderName = Path.GetFileName(subDir),
                RelativePath = Path.GetRelativePath(targetFolder, subDir),
                FullPath = subDir,
                Files = subFiles!
            });
        }

        return results;
    }

    private (List<ModFolderSummary> FolderMods, List<StandaloneModFile> StandaloneFiles, List<ModUnit> ModUnits) ScanUserModsInternal()
    {
        var folderMods = new List<ModFolderSummary>();
        var standaloneFiles = new List<StandaloneModFile>();
        var allUnits = ScanUpdatableModUnits();

        foreach (string dir in GetSortedTopDirectories())
        {
            string dirName = Path.GetFileName(dir);
            var allPackages = new List<string>();
            var allScripts = new List<string>();
            foreach (string f in Directory.EnumerateFiles(dir, "*", RecursiveEnumOpts))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".package") allPackages.Add(f);
                else if (ext == ".ts4script") allScripts.Add(f);
            }

            int subdirCount = Directory.EnumerateDirectories(dir, "*", ShallowEnumOpts).Count();
            string modType;
            if (IsCcFolderName(dirName))
                modType = "CC / 외형·건축";
            else if (allScripts.Count > 0)
                modType = "스크립트 모드";
            else if (IsTranslationFolderOrFiles(dirName, allPackages))
                modType = "한글화 / 우선순위";
            else if (allPackages.Count > 0)
                modType = "패키지 모드";
            else
                modType = "폴더 / 기타";

            bool isUpdatable = modType is "스크립트 모드" or "패키지 모드" or "한글화 / 우선순위";
            int unitCount = allUnits.Count(u =>
                !u.IsStandalone &&
                (u.FolderName == dirName ||
                 u.FolderName.StartsWith(dirName + "\\", StringComparison.OrdinalIgnoreCase) ||
                 u.FolderName.StartsWith(dirName + "/", StringComparison.OrdinalIgnoreCase)));

            folderMods.Add(new ModFolderSummary
            {
                Name = dirName,
                Path = dir,
                PackageCount = allPackages.Count,
                ScriptCount = allScripts.Count,
                SubdirCount = subdirCount,
                ModType = modType,
                IsUpdatable = isUpdatable,
                UnitCount = unitCount
            });
        }

        foreach (string file in Directory.EnumerateFiles(_modsFolder, "*", ShallowEnumOpts)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!name.Equals("resource.cfg", StringComparison.OrdinalIgnoreCase) && IsModExt(ext))
            {
                standaloneFiles.Add(new StandaloneModFile
                {
                    Name = name,
                    Extension = ext,
                    FullPath = file,
                    SizeBytes = new FileInfo(file).Length
                });
            }
        }

        return (folderMods, standaloneFiles, allUnits);
    }

    private List<ModUnit> GroupDirectoryIntoUnits(string targetDir, List<string> files, bool isStandaloneRoot)
    {
        var baseStems = new List<string>();
        foreach (string f in files)
        {
            string s = NormalizeGroupStem(Path.GetFileNameWithoutExtension(f));
            if (!string.IsNullOrEmpty(s))
                baseStems.Add(s);
        }

        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            string gk = GetFileGroupKey(f, baseStems);
            if (!groups.TryGetValue(gk, out var list))
            {
                list = new List<string>();
                groups[gk] = list;
            }
            list.Add(f);
        }

        string relDirStr = isStandaloneRoot || PathsEqual(targetDir, _modsFolder)
            ? string.Empty
            : Path.GetRelativePath(_modsFolder, targetDir);
        if (relDirStr.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relDirStr))
            relDirStr = Path.GetFileName(targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string folderDisplay = string.IsNullOrEmpty(relDirStr) ? "(Mods 루트)" : relDirStr;
        bool multiGroupInDir = groups.Count > 1 || isStandaloneRoot ||
                               relDirStr.Contains('\\') || relDirStr.Contains('/');

        var units = new List<ModUnit>();
        foreach (var (gk, gFiles) in groups)
        {
            var pkgs = gFiles.Where(f => Path.GetExtension(f).Equals(".package", StringComparison.OrdinalIgnoreCase)).ToList();
            var scrs = gFiles.Where(f => Path.GetExtension(f).Equals(".ts4script", StringComparison.OrdinalIgnoreCase)).ToList();
            string primary = SelectPrimaryFile(gFiles);
            string primaryName = Path.GetFileName(primary);

            string uType = scrs.Count > 0
                ? "스크립트 모드"
                : (IsTranslationFolderOrFiles(Path.GetFileName(targetDir), pkgs) ? "한글화 / 우선순위" : "패키지 모드");

            string displayName = multiGroupInDir ? $"{folderDisplay} › {primaryName}" : folderDisplay;

            units.Add(new ModUnit
            {
                Name = displayName,
                DisplayName = displayName,
                FolderName = folderDisplay,
                InstallFolder = relDirStr,
                DirectoryPath = targetDir,
                PrimaryFileName = primaryName,
                PrimaryFilePath = primary,
                UnitFiles = gFiles,
                PackageCount = pkgs.Count,
                ScriptCount = scrs.Count,
                SubdirCount = 0,
                ModType = uType,
                IsStandalone = isStandaloneRoot,
                GroupKey = gk
            });
        }

        return units;
    }

    private string GetFileGroupKey(string filePath, List<string> allStemsInDir)
    {
        string fileName = Path.GetFileName(filePath);
        string fLow = fileName.ToLowerInvariant();
        if (fLow.StartsWith("mc_woohoo", StringComparison.Ordinal))
            return "mccc:mc_woohoo";
        if (fLow.StartsWith("mc_", StringComparison.Ordinal))
            return "mccc:mc_cmd_center";

        var cat = _catalog.CheckKnownCatalog(fileName);
        if (cat is not null && !string.IsNullOrEmpty(cat.MatchedCatalog))
            return $"cat:{cat.MatchedCatalog}";

        string cleanLow = NormalizeGroupStem(Path.GetFileNameWithoutExtension(filePath));
        string[] parts = cleanLow.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            string p0 = parts[0];
            string p1 = parts[1];
            if (p0 == "littlemssam" && p1 == "rsm" && parts.Length >= 4)
                return $"lms_rsm:{p0}_{p1}_{parts[2]}_{parts[3]}";
            if (KnownCreatorsTwoPart.Contains(p0))
                return $"creator_mod:{p0}_{p1}";
        }

        foreach (string baseCand in allStemsInDir.OrderBy(s => s.Length))
        {
            if (baseCand.Length >= 8 &&
                (cleanLow == baseCand || cleanLow.StartsWith(baseCand + "_", StringComparison.Ordinal)) &&
                allStemsInDir.Any(other => other != baseCand && other.StartsWith(baseCand + "_", StringComparison.Ordinal)))
            {
                return $"base:{baseCand}";
            }
        }

        return $"stem:{cleanLow}";
    }

    private static string SelectPrimaryFile(List<string> gFiles)
    {
        if (gFiles.Count == 1)
            return gFiles[0];

        static string NormBaseStem(string p)
        {
            string s = Regex.Replace(Path.GetFileNameWithoutExtension(p), @"^[!_~\-\s]+", "");
            return Regex.Replace(s, @"[_\-\s]+(scripts?|package|core|main|base|required)$", "", RegexOptions.IgnoreCase);
        }

        int minStemLen = gFiles.Min(f => NormBaseStem(f).Length);

        return gFiles
            .OrderByDescending(f =>
            {
                string fLow = Path.GetFileName(f).ToLowerInvariant();
                string stemClean = NormBaseStem(f);
                int score = 0;

                if (fLow.Contains("mc_cmd_center.ts4script", StringComparison.Ordinal) ||
                    fLow.Contains("wonderfulwhims_scripts.ts4script", StringComparison.Ordinal) ||
                    fLow.Contains("!wamtik", StringComparison.Ordinal) ||
                    fLow.Contains("_wamtik", StringComparison.Ordinal) ||
                    fLow.Contains("m00_corelibrary_required", StringComparison.Ordinal) ||
                    fLow == "sims4_ai_chat.ts4script")
                {
                    score += 250;
                }

                if (fLow.Contains("required", StringComparison.Ordinal) ||
                    fLow.Contains("core", StringComparison.Ordinal) ||
                    fLow.Contains("base", StringComparison.Ordinal))
                {
                    score += 80;
                }

                if (OptionalSecondaryKeywords.Any(k => fLow.Contains(k, StringComparison.Ordinal)))
                    score -= 90;

                if (stemClean.Length <= minStemLen + 2)
                    score += 65;

                if (Path.GetExtension(f).Equals(".ts4script", StringComparison.OrdinalIgnoreCase))
                    score += 40;

                return score;
            })
            .ThenBy(f => NormBaseStem(f).Length)
            .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static string NormalizeGroupStem(string stem)
    {
        string s = Regex.Replace(stem, @"^[!_~\-\s]+", "").ToLowerInvariant();
        s = Regex.Replace(s, @"[\s_-]*\((?:required|optional|[^\)]*)\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[\s_-]+(?:v|ver|version)?[0-9]+(?:\.[0-9]+)*[a-z]?\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(
            s,
            @"[_\-\s]+(scripts?|tuning|package|core|main|base|required|optional|addon|add-on|test_users)$",
            "",
            RegexOptions.IgnoreCase);
        return s.Trim(' ', '_', '-');
    }

    /// <summary>
    /// Mods 폴더 전체에서 동일 파일명이 2개 이상 설치된 모든 모드 파일의 전체 경로를 반환합니다.
    /// </summary>
    public HashSet<string> CollectAllDuplicateFilePaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_modsFolder) || !Directory.Exists(_modsFolder))
            return result;

        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string item in Directory.EnumerateFiles(_modsFolder, "*", RecursiveEnumOpts))
        {
            string ext = Path.GetExtension(item);
            if (!IsModExt(ext))
                continue;

            string name = Path.GetFileName(item);
            if (name.Equals("resource.cfg", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!byName.TryGetValue(name, out List<string>? paths))
            {
                paths = new List<string>(2);
                byName[name] = paths;
            }

            paths.Add(item);
        }

        foreach (List<string> paths in byName.Values)
        {
            if (paths.Count < 2)
                continue;

            foreach (string path in paths)
                result.Add(path);
        }

        return result;
    }

    private List<DuplicateFileInfo> FindDuplicates(IEnumerable<string> targetFiles)
    {
        var duplicates = new List<DuplicateFileInfo>();
        var targetMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in targetFiles)
        {
            string name = Path.GetFileName(f);
            targetMap.TryAdd(name, f);
        }

        if (targetMap.Count == 0)
            return duplicates;

        foreach (string item in Directory.EnumerateFiles(_modsFolder, "*", RecursiveEnumOpts))
        {
            string ext = Path.GetExtension(item).ToLowerInvariant();
            if (!IsModExt(ext))
                continue;

            string name = Path.GetFileName(item);
            if (targetMap.TryGetValue(name, out string? originalFile) && !PathsEqual(item, originalFile))
            {
                duplicates.Add(new DuplicateFileInfo
                {
                    FileName = name,
                    Extension = ext,
                    OriginalPath = originalFile,
                    DuplicatePath = item
                });
            }
        }

        return duplicates;
    }

    private IEnumerable<string> GetSortedTopDirectories()
    {
        return Directory.EnumerateDirectories(_modsFolder, "*", ShallowEnumOpts)
            .OrderBy(d => GetFolderSortPriority(Path.GetFileName(d)))
            .ThenBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
    }

    private static int GetFolderSortPriority(string name)
    {
        string nameUp = name.Trim().ToUpperInvariant();
        if (Regex.IsMatch(nameUp, @"^(?:[\d\-]+[\.\s_\-]*)?(?:GSM|GM)(?:[\s_\-]*\[?\d+\]?)?(?:[\s_\-]|$)"))
            return 0;
        if (Regex.IsMatch(nameUp, @"^(?:[\d\-]+[\.\s_\-]*)?MOD(?:[\s_\-]*\[?\d+\]?)?(?:[\s_\-]|$)") ||
            nameUp.Contains("MCCC", StringComparison.Ordinal) ||
            nameUp.Contains("WONDERFUL", StringComparison.Ordinal))
            return 1;
        if (nameUp.Contains("OVERRIDE", StringComparison.Ordinal) ||
            nameUp.Contains("PRIO", StringComparison.Ordinal) ||
            nameUp.Contains("한글", StringComparison.Ordinal))
            return 2;
        if (IsCcFolderName(name))
            return 9;
        return 3;
    }

    private static bool IsModExt(string ext) =>
        ext.Equals(".package", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".ts4script", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
