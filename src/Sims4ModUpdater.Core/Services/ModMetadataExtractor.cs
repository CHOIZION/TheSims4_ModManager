using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;
using YamlDotNet.Serialization;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// .package(DBPF 2.1 바이너리·XML Tuning·CASP·NameMap) 및 .ts4script(ZIP·ModFileManifest·.pyc 상수)
/// 내부 메타데이터(제작자명, 모드명, 버전, 내장 공식 배포 URL, 루트 튜닝/모듈) 추출기
/// </summary>
public sealed class ModMetadataExtractor
{
    private static readonly Regex StripModExtRegex = new(
        @"\.(?:pyc|py|txt|yml|yaml|json|cfg|log)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AsciiUrlBytesRegex = new(
        @"https?://[a-zA-Z0-9./_\-@%&?=]+",
        RegexOptions.Compiled);

    private static readonly Regex RpoRegex = new(
        @"^Lumpinou_RPO_Collection_(M\d+)_(.+?)(?:_REQUIRED)?(?:_v[0-9.]+[a-z]?)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HexKeyRegex = new(
        @"^0x([0-9A-Fa-f]{1,8})$",
        RegexOptions.Compiled);

    private static readonly Regex PureHexIdRegex = new(
        @"^(?:0x[0-9A-Fa-f]+|[0-9A-Fa-f]{8,}|\d+)$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> NonCreatorTuningWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "buff", "loot", "trait", "mixer", "si", "sim", "sims", "sims4",
        "tyae", "broadcaster", "client", "service", "servicenpc", "desk",
        "situation", "template", "phone", "cheat", "idle", "object",
        "objects", "autonomous", "interaction", "interactions", "career",
        "relbit", "moodlet", "statistic", "stat", "affordance", "recipe",
        "skill", "aspiration", "whim", "reward", "snippet", "pose", "posepack",
        "anim", "animation", "prop", "commodity", "social", "superinteraction",
        "tooltip", "tuning", "ea", "maxis", "s4s", "sims4studio"
    };

    private static readonly HashSet<string> NonCreatorModuleWords = new(
        CatalogService.GenericInternalNames.Concat([
            "sims4", "sims", "ts4", "mod", "simulation", "career", "regenerate",
            "interaction", "interactions", "ui", "cas", "autonomy", "story"
        ]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NonCreatorFilenamePrefixes = new(
        CatalogService.GenericInternalNames.Concat([
            "more", "less", "ui", "cas", "no", "better", "auto", "custom",
            "extra", "new", "all", "fix", "fixed", "higher", "harder", "improved",
            "personality", "sims", "restricted",
            "bed", "chrome", "double", "fake", "free",
            "high", "lay", "long", "love", "mini", "oll", "personal",
            "real", "remera", "remastered", "simple", "small", "spot",
            "short", "vertical", "wide", "xframe", "super", "ultra", "mega",
            "mystery", "random", "unknown", "untitled", "sample", "example", "generic",
            "temp", "misc", "other", "item", "items", "object", "objects", "prop", "props",
            "part", "pack", "collection", "starter", "startingpack", "override", "default",
            "recolor", "mesh", "texture", "skin", "hair", "pose", "poses", "anim",
            "animation", "animations", "relbit", "moodlet", "objektbeschreibung", "by"
        ]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AnimationAddonTags =
    [
        "CustomCASPartsPackage",
        "CustomAnimationPackage",
        "PosePackSnippet",
        "custom_cas_parts",
        "custom_animations",
        "dance_animations"
    ];

    private readonly CatalogService _catalog;

    public ModMetadataExtractor(CatalogService? catalog = null)
    {
        _catalog = catalog ?? new CatalogService();
    }

    public static string SplitCamelCase(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string s = Regex.Replace(text, @"(?<=[a-zA-Z])(?:v|ver)[0-9]+(?:\.[0-9]+)*", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"([a-z0-9])([A-Z])", "$1 $2");
        s = Regex.Replace(s, @"([A-Z]+)([A-Z][a-z])", "$1 $2");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    public ModMetadata Extract(string? filePath)
    {
        var result = new ModMetadata();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return result;

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        string fileName = Path.GetFileName(filePath);
        string stem = Path.GetFileNameWithoutExtension(filePath);
        string parentDir = Path.GetDirectoryName(filePath) ?? string.Empty;

        SingleArchiveInfo pkgInfo = new();
        SingleArchiveInfo scrInfo = new();
        string? siblingPkgPath = null;

        if (ext == ".package")
        {
            pkgInfo = ExtractFromSinglePackage(filePath);

            string siblingScr = Path.ChangeExtension(filePath, ".ts4script");
            if (!File.Exists(siblingScr) && !string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                string altStem = Regex.Replace(stem, @"[_\-\s]*(tuning|package)$", "", RegexOptions.IgnoreCase);
                string prefix = altStem.Length >= 8 ? altStem[..8] : altStem;
                if (!string.IsNullOrEmpty(prefix))
                {
                    foreach (string cand in Directory.EnumerateFiles(parentDir, "*.ts4script").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        if (Path.GetFileNameWithoutExtension(cand).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            siblingScr = cand;
                            break;
                        }
                    }
                }
            }

            if (File.Exists(siblingScr))
                scrInfo = ExtractFromSingleTs4Script(siblingScr);

            // 메쉬 전용 .package fallback: 동일 10글자 이상 접두사를 가진 형제 .package 검사
            if (string.IsNullOrEmpty(pkgInfo.ManifestName) &&
                pkgInfo.Creators.Count == 0 &&
                pkgInfo.RootTunings.Count == 0 &&
                pkgInfo.Urls.Count == 0 &&
                stem.Length >= 10 &&
                Directory.Exists(parentDir))
            {
                string prefixMatch = stem[..10];
                var siblingPkgs = Directory.EnumerateFiles(parentDir, "*.package")
                    .Where(sp => !string.Equals(sp, filePath, StringComparison.OrdinalIgnoreCase) &&
                                 Path.GetFileNameWithoutExtension(sp).StartsWith(prefixMatch, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(2);

                foreach (string sp in siblingPkgs)
                {
                    var spInfo = ExtractFromSinglePackage(sp);
                    if (spInfo.Creators.Count > 0 || spInfo.Urls.Count > 0 || spInfo.RootTunings.Count > 0)
                    {
                        pkgInfo = spInfo;
                        break;
                    }
                }
            }
        }
        else if (ext == ".ts4script")
        {
            scrInfo = ExtractFromSingleTs4Script(filePath);

            siblingPkgPath = Path.ChangeExtension(filePath, ".package");
            if (!File.Exists(siblingPkgPath) && !string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                string altStem = Regex.Replace(stem, @"[_\-\s]*(scripts|script)$", "", RegexOptions.IgnoreCase);
                string prefix = altStem.Length >= 8 ? altStem[..8] : altStem;
                if (!string.IsNullOrEmpty(prefix))
                {
                    foreach (string cand in Directory.EnumerateFiles(parentDir, "*.package").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        if (Path.GetFileNameWithoutExtension(cand).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            siblingPkgPath = cand;
                            break;
                        }
                    }
                }
            }

            if (File.Exists(siblingPkgPath))
                pkgInfo = ExtractFromSinglePackage(siblingPkgPath);

            if (pkgInfo.Urls.Count == 0 && scrInfo.Urls.Count == 0 && stem.Length >= 12 && Directory.Exists(parentDir))
            {
                string prefixMatch = stem[..12];
                var siblingPkgs = Directory.EnumerateFiles(parentDir, "*.package")
                    .Where(sp => !string.Equals(sp, siblingPkgPath, StringComparison.OrdinalIgnoreCase) &&
                                 Path.GetFileNameWithoutExtension(sp).StartsWith(prefixMatch, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => Path.GetFileName(x).Length)
                    .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(3);

                foreach (string sp in siblingPkgs)
                {
                    var spInfo = ExtractFromSinglePackage(sp);
                    if (spInfo.Urls.Count > 0 || !string.IsNullOrEmpty(spInfo.ManifestName))
                    {
                        pkgInfo = spInfo;
                        break;
                    }
                }
            }
        }

        // 추출 정보 병합
        result.ManifestName = !string.IsNullOrEmpty(pkgInfo.ManifestName) ? pkgInfo.ManifestName : scrInfo.ManifestName;
        string rawMergedVer = (!string.IsNullOrEmpty(pkgInfo.Version) ? pkgInfo.Version : scrInfo.Version).Trim();
        result.Version = VersionUtils.BarambyulPattern.IsMatch(rawMergedVer)
            ? rawMergedVer
            : rawMergedVer.TrimStart('v', 'V');
        result.IsAnimationAddon = pkgInfo.IsAnimationAddon;

        foreach (string c in pkgInfo.Creators.Concat(scrInfo.Creators))
        {
            string cleanC = c.Trim();
            if (!string.IsNullOrWhiteSpace(cleanC) &&
                !IsHexOrNumericId(cleanC) &&
                !result.Creators.Contains(cleanC, StringComparer.Ordinal))
            {
                result.Creators.Add(cleanC);
            }
        }

        foreach (string u in pkgInfo.Urls.Concat(scrInfo.Urls))
        {
            if (!string.IsNullOrWhiteSpace(u) && !result.Urls.Contains(u, StringComparer.Ordinal))
                result.Urls.Add(u);
        }

        result.DisplayNames = pkgInfo.DisplayNames.Take(3).ToList();
        result.RootTunings = pkgInfo.RootTunings.Take(5).ToList();
        result.Modules = scrInfo.Modules.Take(5).ToList();

        var (infCreator, infMod) = ParseTuningAndModuleClues(result.RootTunings, result.Modules);

        // 파일명 버전 보완
        string fnVer = VersionUtils.ExtractVersionFromFilename(fileName);
        if (string.IsNullOrEmpty(result.Version) && !string.IsNullOrEmpty(fnVer))
            result.Version = VersionUtils.BarambyulPattern.IsMatch(fnVer) ? fnVer : fnVer.TrimStart('v', 'V');

        if (string.IsNullOrEmpty(result.Version) && ext == ".ts4script" && !string.IsNullOrEmpty(siblingPkgPath) && File.Exists(siblingPkgPath))
        {
            string sibVer = VersionUtils.ExtractVersionFromFilename(Path.GetFileName(siblingPkgPath));
            if (!string.IsNullOrEmpty(sibVer))
                result.Version = sibVer.TrimStart('v', 'V');
        }

        if (string.IsNullOrEmpty(result.Version) &&
            fileName.StartsWith("mc_", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(parentDir))
        {
            string mcCorePath = Path.Combine(parentDir, "mc_cmd_center.ts4script");
            if (File.Exists(mcCorePath))
            {
                var mcInfo = ExtractFromSingleTs4Script(mcCorePath);
                if (!string.IsNullOrEmpty(mcInfo.Version))
                    result.Version = mcInfo.Version.TrimStart('v', 'V');
            }
        }

        string fnCreator = string.Empty;
        bool hasValidBracket = false;
        foreach (Match mBr in Regex.Matches(stem, @"\[([^\]]+)\]"))
        {
            string cand = mBr.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(cand) && !IsIgnoredFilenameBracketTag(cand) && !IsHexOrNumericId(cand))
            {
                fnCreator = cand;
                hasValidBracket = true;
                break;
            }
        }

        string? animCreator = string.IsNullOrEmpty(fnCreator) ? ExtractPrefixedPackCreatorFromStem(stem) : null;
        if (!string.IsNullOrEmpty(animCreator) && !IsHexOrNumericId(animCreator))
            fnCreator = animCreator;

        Match? mDashWrapped = null;
        if (string.IsNullOrEmpty(fnCreator))
        {
            mDashWrapped = Regex.Match(stem, @"^\-([A-Za-z0-9][A-Za-z0-9. ]{1,30}?)\-(?:[\s_\-]|$|[A-Za-z])");
            if (mDashWrapped.Success)
            {
                string cand = mDashWrapped.Groups[1].Value.Trim();
                if (!NonCreatorFilenamePrefixes.Contains(cand) && !IsHexOrNumericId(cand))
                    fnCreator = cand;
            }
        }

        if (string.IsNullOrEmpty(fnCreator))
        {
            Match mTsrDash = Regex.Match(stem, @"^([A-Za-z0-9][A-Za-z0-9_]{1,30})-(?:ts4|tsr|sims4)-", RegexOptions.IgnoreCase);
            if (mTsrDash.Success)
            {
                string cand = mTsrDash.Groups[1].Value.Trim();
                if (!NonCreatorFilenamePrefixes.Contains(cand) && !IsHexOrNumericId(cand))
                    fnCreator = cand;
            }
        }

        string stemNoVersion = Regex.Replace(
            stem,
            @"(?:[\s_\-]+(?:v|ver|version)\s*[0-9]+(?:\.[0-9]+)*[a-z0-9]*|\s*\(\d+\))\s*$",
            "",
            RegexOptions.IgnoreCase).Trim();
        Match? mByFn = null;
        if (string.IsNullOrEmpty(fnCreator))
        {
            mByFn = Regex.Match(stemNoVersion, @"(?:^|[\s_\-])by[\s_]+(?!the\b|me\b|you\b|us\b)([A-Za-z0-9][A-Za-z0-9_.-]{1,30})$", RegexOptions.IgnoreCase);
            if (!mByFn.Success)
                mByFn = Regex.Match(stemNoVersion, @"(?<=\d)by[\s_]*([A-Za-z][A-Za-z0-9_.-]{1,30})$", RegexOptions.IgnoreCase);
            if (!mByFn.Success)
                mByFn = Regex.Match(stemNoVersion, @"(?<=[a-z])by([A-Z][A-Za-z0-9_.-]{1,30})$");
            if (!mByFn.Success)
                mByFn = Regex.Match(stemNoVersion, @"(?<=[a-z])By([A-Z][A-Za-z0-9_.-]{1,30})$");
            if (mByFn.Success)
            {
                string cand = mByFn.Groups[1].Value.Trim();
                if (!NonCreatorFilenamePrefixes.Contains(cand) && !IsHexOrNumericId(cand))
                    fnCreator = cand;
            }
        }

        bool hasDashAuthor = false;
        if (string.IsNullOrEmpty(fnCreator))
        {
            Match mAuthorDash = Regex.Match(stem, @"^([A-Za-z0-9][A-Za-z0-9_.\s]{1,28}?)\s+-\s+(.+)$");
            if (mAuthorDash.Success)
            {
                string cand = mAuthorDash.Groups[1].Value.Trim();
                string[] cWords = cand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cWords.Length <= 2 &&
                    cand.Length >= 2 &&
                    !Regex.IsMatch(cand, @"^(?:ts4|sims\s*4|part\s*\d+|\d+)$", RegexOptions.IgnoreCase) &&
                    !NonCreatorFilenamePrefixes.Contains(cand) &&
                    !IsHexOrNumericId(cand))
                {
                    fnCreator = cand;
                    hasDashAuthor = true;
                }
            }
        }

        bool explicitFnCreator = !string.IsNullOrEmpty(fnCreator) &&
            (hasValidBracket || !string.IsNullOrEmpty(animCreator) || mDashWrapped?.Success == true || mByFn?.Success == true || hasDashAuthor);

        string fnTitleRaw;
        if (mByFn?.Success == true && !hasValidBracket)
        {
            fnTitleRaw = Regex.Replace(stem.Replace("_", " "), @"\s+", " ").Trim();
        }
        else
        {
            fnTitleRaw = Regex.Replace(stem, @"\[[^\]]+\]", " ");
            fnTitleRaw = Regex.Replace(
                fnTitleRaw,
                @"(?:[\s_\-]|^)(?:v|ver|version)\s*[0-9]+(?:\.[0-9]+)*[a-z0-9]*\b",
                " ",
                RegexOptions.IgnoreCase);
            fnTitleRaw = Regex.Replace(fnTitleRaw, @"\s+", " ").Trim(' ', '_', '-', '!');
        }

        if (string.IsNullOrEmpty(fnCreator) && stem.Contains('_', StringComparison.Ordinal))
        {
            string[] uParts = stem.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (uParts.Length >= 2 &&
                uParts[0].Length >= 2 &&
                !uParts[0].All(char.IsDigit) &&
                !Regex.IsMatch(uParts[0], @"^\d+(?:\.\d+)*$") &&
                !NonCreatorFilenamePrefixes.Contains(uParts[0]) &&
                !IsHexOrNumericId(uParts[0]))
            {
                fnCreator = string.Equals(uParts[0], "NV", StringComparison.OrdinalIgnoreCase) ? "NV Games" : uParts[0];
                fnTitleRaw = string.Join(" ", uParts.Skip(1));
            }
        }

        var mRpo = RpoRegex.Match(stem);
        if (mRpo.Success)
        {
            string rpoSub = SplitCamelCase(mRpo.Groups[2].Value.Replace("_", " "));
            result.ManifestName = $"RPO {mRpo.Groups[1].Value.ToUpperInvariant()} {rpoSub}";
        }

        string parentName = Path.GetFileName(parentDir) ?? string.Empty;
        CatalogMatchResult? catMatch = _catalog.CheckKnownCatalog(fileName) ??
            (parentName.Contains("mccc", StringComparison.OrdinalIgnoreCase) ? _catalog.CheckKnownCatalog(parentName) : null);

        if (catMatch is not null)
        {
            if (!string.IsNullOrEmpty(catMatch.Creator))
                result.Creators = [catMatch.Creator];
            if (!string.IsNullOrEmpty(catMatch.ModTitle) && (string.IsNullOrEmpty(result.ManifestName) || catMatch.OverrideModTitle))
                result.ManifestName = catMatch.ModTitle;
            if (!string.IsNullOrEmpty(catMatch.Version) && (string.IsNullOrEmpty(result.Version) || result.Version == "4.04"))
                result.Version = catMatch.Version;
            if (catMatch.CandidateLinks.Count > 0)
                result.Urls = catMatch.CandidateLinks.Select(l => l.Url).ToList();
        }

        string? firstMetaCreator = result.Creators.FirstOrDefault(c => !IsHexOrNumericId(c));
        string? safeFnCreator = !string.IsNullOrEmpty(fnCreator) && !IsHexOrNumericId(fnCreator) ? fnCreator : null;
        string? safeInfCreator = !string.IsNullOrEmpty(infCreator) && !IsHexOrNumericId(infCreator) ? infCreator : null;
        result.InferredCreator = firstMetaCreator
            ?? (explicitFnCreator ? safeFnCreator : safeInfCreator)
            ?? safeFnCreator
            ?? safeInfCreator
            ?? string.Empty;

        result.InferredMod = FirstNonEmpty(
            result.ManifestName,
            result.DisplayNames.FirstOrDefault(),
            explicitFnCreator && !string.IsNullOrEmpty(fnTitleRaw) ? fnTitleRaw : null,
            infMod,
            fnTitleRaw);

        result.HasMetadata =
            !string.IsNullOrEmpty(result.ManifestName) ||
            result.Creators.Count > 0 ||
            !string.IsNullOrEmpty(result.InferredCreator) ||
            !string.IsNullOrEmpty(result.InferredMod) ||
            result.Urls.Count > 0 ||
            result.RootTunings.Count > 0 ||
            result.Modules.Count > 0;

        return result;
    }

    internal static SingleArchiveInfo ExtractFromSingleTs4Script(string path)
    {
        var info = new SingleArchiveInfo();
        if (!File.Exists(path))
            return info;

        string fileName = Path.GetFileName(path);
        string parentDir = Path.GetDirectoryName(path) ?? string.Empty;

        try
        {
            using var fs = File.OpenRead(path);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string entryName = entry.FullName;
                string[] parts = entryName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    string topStem = StripModExtRegex.Replace(parts[0], "");
                    if (!string.IsNullOrEmpty(topStem) &&
                        !CatalogService.GenericInternalNames.Contains(topStem) &&
                        !topStem.StartsWith('_') &&
                        !info.Modules.Contains(topStem, StringComparer.Ordinal))
                    {
                        info.Modules.Add(topStem);
                    }
                }

                if (entry.Length <= 0 || entry.Length > 8 * 1024 * 1024)
                    continue;

                byte[] data;
                try
                {
                    using Stream es = entry.Open();
                    using var ms = new MemoryStream((int)entry.Length);
                    es.CopyTo(ms);
                    data = ms.ToArray();
                }
                catch
                {
                    continue;
                }

                // 1. LlamaLogic ModFileManifest YAML 파싱
                if (entryName.Contains("modfilemanifest", StringComparison.OrdinalIgnoreCase))
                {
                    string text = Encoding.UTF8.GetString(data);
                    ParseModFileManifestYaml(text, fileName, info);
                    continue;
                }

                // 2. 일반 .pyc / .py 내부 URL 스캔
                string eLow = entryName.ToLowerInvariant();
                if ((eLow.EndsWith(".txt", StringComparison.Ordinal) || eLow.EndsWith(".md", StringComparison.Ordinal)) &&
                    eLow.Contains("license", StringComparison.Ordinal))
                {
                    continue;
                }

                string asciiText = Encoding.Latin1.GetString(data);
                foreach (Match m in AsciiUrlBytesRegex.Matches(asciiText))
                {
                    string uVal = CleanPycUrl(data, m);
                    if (CatalogService.IsValidInternalUrl(uVal, fileName) && !info.Urls.Contains(uVal, StringComparer.Ordinal))
                        info.Urls.Add(uVal);
                }

                // 3. ModFileManifest 없는 핵심 스크립트 내부 버전 상수 보조 추출
                if (string.IsNullOrEmpty(info.Version))
                {
                    if (eLow.EndsWith("mc_cmd_version.pyc", StringComparison.Ordinal) ||
                        eLow.EndsWith("mc_utils.pyc", StringComparison.Ordinal) ||
                        eLow.EndsWith("mc_cmd_center.pyc", StringComparison.Ordinal) ||
                        eLow.EndsWith("mc_woohoo.pyc", StringComparison.Ordinal))
                    {
                        var mMc = Regex.Match(asciiText, @"(?<![0-9.])(202[0-9]\.[0-9]+\.[0-9]+)(?![0-9.])");
                        if (mMc.Success) info.Version = mMc.Groups[1].Value;
                    }
                    else if (eLow.Contains("wonderfulwhims", StringComparison.Ordinal) &&
                             (eLow.Contains("version", StringComparison.Ordinal) || eLow.Contains("main", StringComparison.Ordinal) || eLow.Contains("init", StringComparison.Ordinal)))
                    {
                        var mWfw = Regex.Match(asciiText, @"(?<![a-zA-Z0-9.])v([456789][0-9][a-z]?)(?![a-zA-Z0-9.])", RegexOptions.IgnoreCase);
                        if (mWfw.Success) info.Version = $"v{mWfw.Groups[1].Value}";
                    }
                    else if (eLow.EndsWith("sims4_ai_chat/config.pyc", StringComparison.Ordinal) ||
                             eLow.EndsWith(@"sims4_ai_chat\config.pyc", StringComparison.Ordinal))
                    {
                        var mAi = Regex.Match(asciiText, @"(?<![0-9.])([0-9]+\.[0-9]+\.[0-9]+)(?![0-9.])");
                        if (mAi.Success) info.Version = mAi.Groups[1].Value;
                    }
                    else if (eLow.EndsWith("ui_cheats/cheat_commands.pyc", StringComparison.Ordinal) ||
                             eLow.EndsWith(@"ui_cheats\cheat_commands.pyc", StringComparison.Ordinal))
                    {
                        var mUi = Regex.Match(asciiText, @"(?<![0-9.])(1\.[3-9][0-9](?:\.[0-9]+)?)(?![0-9.])");
                        if (mUi.Success)
                        {
                            string uiVer = mUi.Groups[1].Value;
                            if (uiVer == "1.59" && !string.IsNullOrEmpty(parentDir))
                            {
                                string pkgSibling = Path.Combine(parentDir, "UI_Cheats_Extension.package");
                                if (File.Exists(pkgSibling) && new FileInfo(pkgSibling).Length == 15388976)
                                    uiVer = "1.59.1";
                            }
                            info.Version = uiVer;
                        }
                    }
                    else if (eLow.Contains("tmex_belibrary.pyc", StringComparison.Ordinal))
                    {
                        var mBe = Regex.Match(asciiText, @"(?<![0-9.])([234]\.[0-9]{1,2}(?:\.[0-9]+)?)(?![0-9.])");
                        if (mBe.Success) info.Version = mBe.Groups[1].Value;
                    }
                }
            }
        }
        catch
        {
            // 손상되었거나 비ZIP 파일 무시
        }

        return info;
    }

    internal static SingleArchiveInfo ExtractFromSinglePackage(string path)
    {
        var info = new SingleArchiveInfo();
        if (!File.Exists(path))
            return info;

        string fileName = Path.GetFileName(path);

        try
        {
            using var fs = File.OpenRead(path);
            byte[] header = new byte[96];
            if (fs.Read(header, 0, 96) < 96)
                return info;

            if (header[0] != 'D' || header[1] != 'B' || header[2] != 'P' || header[3] != 'F')
                return info;

            uint idxCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36, 4));
            uint idxOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64, 4));
            if (idxOffset == 0)
                idxOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40, 4));

            if (idxCount == 0 || idxOffset == 0 || idxOffset >= fs.Length)
                return info;

            fs.Position = idxOffset;
            int idxBytesToRead = (int)Math.Min(idxCount * 32UL + 32UL, 262144UL);
            byte[] idxData = new byte[idxBytesToRead];
            int idxRead = fs.Read(idxData, 0, idxBytesToRead);
            if (idxRead < 24)
                return info;

            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(0, 4));
            int hdrPos = 4;
            bool hasConstType = (flags & 1u) != 0;
            uint constType = hasConstType && hdrPos + 4 <= idxRead ? BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(hdrPos, 4)) : 0;
            if (hasConstType) hdrPos += 4;

            bool hasConstGroup = (flags & 2u) != 0;
            if (hasConstGroup) hdrPos += 4;

            bool hasConstInstEx = (flags & 4u) != 0;
            uint constInstEx = hasConstInstEx && hdrPos + 4 <= idxRead ? BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(hdrPos, 4)) : 0;
            if (hasConstInstEx) hdrPos += 4;

            int entryConstSize = 20 + (hasConstType ? 0 : 4) + (hasConstGroup ? 0 : 4) + (hasConstInstEx ? 0 : 4);
            int pos = hdrPos;

            // STBL String Table (0x220557DA) 엔트리 수집 (언어 우선순위: English 0x00 -> Korean 0x0F -> 기타)
            var stblEntries = new List<(uint Offset, uint Size, int Priority)>();
            int scanPos = pos;
            uint stblScanMax = Math.Min(idxCount, 4096u);
            for (uint si = 0; si < stblScanMax; si++)
            {
                if (scanPos + entryConstSize > idxRead)
                    break;

                int ep = scanPos;
                uint sType = hasConstType ? constType : BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(ep, 4));
                if (!hasConstType) ep += 4;
                if (!hasConstGroup) ep += 4;
                uint sInstEx = hasConstInstEx ? constInstEx : BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(ep, 4));

                if (sType == 0x220557DAu)
                {
                    int sTailPos = scanPos + entryConstSize - 20;
                    uint sOffset = BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(sTailPos + 4, 4));
                    uint sFsize = BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(sTailPos + 8, 4)) & 0x7FFFFFFFu;
                    if (sOffset > 0 && sFsize >= 21 && sFsize <= 2_000_000)
                    {
                        byte lang = (byte)(sInstEx >> 24);
                        int prio = lang == 0x00 ? 0 : (lang == 0x0F ? 1 : 2);
                        stblEntries.Add((sOffset, sFsize, prio));
                    }
                }

                scanPos += entryConstSize;
            }
            if (stblEntries.Count > 1)
                stblEntries.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            var stblMap = new Dictionary<uint, string>();
            int nextStblIdx = 0;

            int inspectedXml = 0;
            uint maxEntries = Math.Min(idxCount, 350u);
            for (uint i = 0; i < maxEntries; i++)
            {
                if (pos + entryConstSize > idxRead || inspectedXml >= 60)
                    break;

                uint entryType = hasConstType
                    ? constType
                    : BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(pos, 4));
                int tailPos = pos + entryConstSize - 20;
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(tailPos + 4, 4));
                uint fsizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(tailPos + 8, 4));
                uint fsize = fsizeRaw & 0x7FFFFFFFu;
                pos += entryConstSize;
                if (entryType == 0x220557DAu)
                    continue;

                if (fsize < 16 || fsize > 524288 || offset + fsize > fs.Length)
                    continue;

                fs.Position = offset;
                byte[] chunk = new byte[fsize];
                int chunkRead = fs.Read(chunk, 0, (int)fsize);
                if (chunkRead < 16)
                    continue;

                string text = string.Empty;
                if (chunk[0] == 0x78 && chunk[1] is 0x01 or 0x9C or 0xDA)
                {
                    if (TryDecompressZlib(chunk, out byte[] decompressed))
                    {
                        if (StartsWithXmlMarker(decompressed))
                        {
                            text = Encoding.UTF8.GetString(decompressed);
                        }
                        else if (info.RootTunings.Count == 0)
                        {
                            ExtractBinaryCaspOrNameMap(decompressed, info);
                        }
                    }
                }
                else if (StartsWithXmlMarker(chunk))
                {
                    text = Encoding.UTF8.GetString(chunk);
                }

                if (string.IsNullOrEmpty(text))
                    continue;

                inspectedXml++;

                // 1. LlamaLogic ModFileManifest XML 스니펫
                if (text.Contains("ModFileManifest", StringComparison.Ordinal) ||
                    text.Contains("llamalogic:manifest", StringComparison.Ordinal))
                {
                    string mainXml = text.Split("<L n=\"required_mods\">")[0];

                    var mName = Regex.Match(mainXml, @"<T n=""name"">([^<]+)</T>");
                    if (mName.Success && string.IsNullOrEmpty(info.ManifestName))
                        info.ManifestName = WebUtility.HtmlDecode(mName.Groups[1].Value.Trim());

                    var mVer = Regex.Match(mainXml, @"<T n=""version"">([^<]+)</T>");
                    if (mVer.Success && string.IsNullOrEmpty(info.Version))
                    {
                        string vCand = WebUtility.HtmlDecode(mVer.Groups[1].Value.Trim()).TrimStart('v', 'V');
                        if (!Regex.IsMatch(vCand, @"^[0-9]+(?:\.[0-9]+)?[xX]$"))
                            info.Version = vCand;
                    }

                    var mCreatorsBlock = Regex.Match(mainXml, @"<L n=""creators"">(.*?)</L>", RegexOptions.Singleline);
                    if (mCreatorsBlock.Success)
                    {
                        foreach (Match mc in Regex.Matches(mCreatorsBlock.Groups[1].Value, @"<T>([^<]+)</T>"))
                        {
                            string cClean = WebUtility.HtmlDecode(mc.Groups[1].Value.Trim());
                            if (!string.IsNullOrEmpty(cClean) && !info.Creators.Contains(cClean, StringComparer.Ordinal))
                                info.Creators.Add(cClean);
                        }
                    }

                    foreach (Match mu in Regex.Matches(mainXml, @"<T n=""(?:url|funding_url)"">(https?://[^<]+)</T>"))
                    {
                        string uClean = CatalogService.NormalizeDistributionUrl(WebUtility.HtmlDecode(mu.Groups[1].Value.Trim()));
                        if (CatalogService.IsValidInternalUrl(uClean, fileName) && !info.Urls.Contains(uClean, StringComparer.Ordinal))
                            info.Urls.Add(uClean);
                    }
                    continue;
                }

                // 2. CAS / Animation 애드온 스니펫 감지
                if (AnimationAddonTags.Any(tag => text.Contains(tag, StringComparison.Ordinal)))
                    info.IsAnimationAddon = true;

                // 3. 루트 <I ... n="..."> 식별자
                foreach (Match mRoot in Regex.Matches(text, @"<I\b[^>]*\bn=""([^""]+)"""))
                {
                    string rt = WebUtility.HtmlDecode(mRoot.Groups[1].Value.Trim());
                    if (!string.IsNullOrEmpty(rt) && !info.RootTunings.Contains(rt, StringComparer.Ordinal))
                        info.RootTunings.Add(rt);
                }

                // 4. 제작자 태그 (STBL 16진수 키 지연 디코딩 포함)
                foreach (Match mAuth in Regex.Matches(
                    text,
                    @"<T n=""(?:cas_part_author|animation_author|author_name|creator_name)"">([^<]+)(?:<!--[^>]*-->)?</T>"))
                {
                    string rawVal = WebUtility.HtmlDecode(mAuth.Groups[1].Value.Trim());
                    string? aVal = ResolveDbpfStringValue(fs, stblEntries, stblMap, ref nextStblIdx, rawVal);
                    if (!string.IsNullOrEmpty(aVal) && !info.Creators.Contains(aVal, StringComparer.Ordinal))
                        info.Creators.Add(aVal);
                }

                // 5. 표시 이름 태그 (STBL 16진수 키 지연 디코딩 포함)
                foreach (Match mDisp in Regex.Matches(
                    text,
                    @"<T n=""(?:cas_part_raw_display_name|cas_part_group|animation_raw_display_name|raw_display_name|display_name)"">([^<]+)(?:<!--[^>]*-->)?</T>"))
                {
                    string rawVal = WebUtility.HtmlDecode(mDisp.Groups[1].Value.Trim());
                    string? dVal = ResolveDbpfStringValue(fs, stblEntries, stblMap, ref nextStblIdx, rawVal);
                    if (!string.IsNullOrEmpty(dVal) && !info.DisplayNames.Contains(dVal, StringComparer.Ordinal))
                        info.DisplayNames.Add(dVal);
                }

                // 6. XML 내부 배포처 URL
                foreach (Match mUrl in AsciiUrlBytesRegex.Matches(text))
                {
                    string uVal = CatalogService.NormalizeDistributionUrl(mUrl.Value);
                    if (CatalogService.IsValidInternalUrl(uVal, fileName) && !info.Urls.Contains(uVal, StringComparer.Ordinal))
                        info.Urls.Add(uVal);
                }
            }
        }
        catch
        {
            // 손상되었거나 접근 불가한 DBPF 무시
        }

        return info;
    }

    internal static (string Creator, string Mod) ParseTuningAndModuleClues(
        IReadOnlyList<string> rootTunings,
        IReadOnlyList<string> modules)
    {
        string inferredCreator = string.Empty;
        string inferredMod = string.Empty;

        foreach (string rt in rootTunings)
        {
            int colonIdx = rt.IndexOf(':');
            if (colonIdx >= 0)
            {
                string prefix = rt[..colonIdx].Trim();
                string rest = rt[(colonIdx + 1)..];
                if (!prefix.Equals("llamalogic", StringComparison.OrdinalIgnoreCase) &&
                    !prefix.Equals("ww", StringComparison.OrdinalIgnoreCase) &&
                    prefix.Length >= 2 &&
                    !NonCreatorTuningWords.Contains(prefix) &&
                    !IsHexOrNumericId(prefix))
                {
                    if (string.IsNullOrEmpty(inferredCreator))
                        inferredCreator = prefix;

                    string restFirst = rest.Split(':')[0].Split('_')[0].Trim();
                    if (string.IsNullOrEmpty(inferredMod) &&
                        !string.IsNullOrEmpty(restFirst) &&
                        !IsIgnoredTuningPart(restFirst))
                    {
                        inferredMod = SplitCamelCase(restFirst);
                    }
                    break;
                }
            }

            var byMatch = Regex.Match(rt, @"_by_?([a-zA-Z0-9]+)$", RegexOptions.IgnoreCase);
            if (byMatch.Success && string.IsNullOrEmpty(inferredCreator))
                inferredCreator = byMatch.Groups[1].Value;

            if (rt.Contains('_', StringComparison.Ordinal))
            {
                string[] parts = rt.Split('_');
                string first = parts[0].Trim();
                if (first.Length >= 3 && !NonCreatorTuningWords.Contains(first) && !IsHexOrNumericId(first))
                {
                    if (string.IsNullOrEmpty(inferredCreator))
                        inferredCreator = first;

                    if (parts.Length >= 2 && string.IsNullOrEmpty(inferredMod))
                    {
                        string second = parts[1].Trim();
                        if (first.Equals("lumpinou", StringComparison.OrdinalIgnoreCase) && second.Equals("MP", StringComparison.OrdinalIgnoreCase))
                            inferredMod = "Mood Pack Mod";
                        else if (first.Equals("lumpinou", StringComparison.OrdinalIgnoreCase) && second.Equals("RTR", StringComparison.OrdinalIgnoreCase))
                            inferredMod = "Road To Romance";
                        else if (!IsIgnoredSecondaryTuningPart(second))
                            inferredMod = SplitCamelCase(second);
                    }
                }
            }
        }

        foreach (string modName in modules)
        {
            if (modName.Contains('_', StringComparison.Ordinal))
            {
                string[] rawParts = modName.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (rawParts.Length > 0 && NonCreatorModuleWords.Contains(rawParts[0]))
                {
                    var restParts = rawParts.Where(p => !CatalogService.GenericInternalNames.Contains(p)).ToArray();
                    if (restParts.Length > 0 && string.IsNullOrEmpty(inferredMod))
                        inferredMod = string.Join(" ", restParts.Select(SplitCamelCase));
                    continue;
                }

                var parts = rawParts.Where(p => !CatalogService.GenericInternalNames.Contains(p)).ToArray();
                if (parts.Length >= 2)
                {
                    if (string.IsNullOrEmpty(inferredCreator))
                        inferredCreator = parts[0];

                    string modFromModule = string.Join(" ", parts.Skip(1).Select(SplitCamelCase));
                    if (!string.IsNullOrEmpty(modFromModule) &&
                        (string.IsNullOrEmpty(inferredMod) ||
                         !inferredMod.Contains(modFromModule, StringComparison.OrdinalIgnoreCase)))
                    {
                        inferredMod = modFromModule;
                    }
                    break;
                }
            }
            else if (string.IsNullOrEmpty(inferredMod) && !CatalogService.GenericInternalNames.Contains(modName))
            {
                inferredMod = SplitCamelCase(modName);
            }
        }

        return (inferredCreator, inferredMod);
    }

    private static void ParseModFileManifestYaml(string text, string fileName, SingleArchiveInfo info)
    {
        string mainPart = Regex.Split(text, @"^required_mods\s*:", RegexOptions.Multiline)[0];

        // YamlDotNet 파싱 우선 시도
        try
        {
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            if (deserializer.Deserialize<Dictionary<object, object>>(mainPart) is { } yamlMap)
            {
                if (string.IsNullOrEmpty(info.ManifestName) &&
                    yamlMap.TryGetValue("name", out object? nameObj) &&
                    nameObj is not null)
                {
                    info.ManifestName = WebUtility.HtmlDecode(nameObj.ToString()!.Trim().Trim('\'', '"'));
                }

                if (string.IsNullOrEmpty(info.Version) &&
                    yamlMap.TryGetValue("version", out object? verObj) &&
                    verObj is not null)
                {
                    info.Version = verObj.ToString()!.Trim().Trim('\'', '"').TrimStart('v', 'V');
                }

                if (yamlMap.TryGetValue("creators", out object? creatorsObj) && creatorsObj is IEnumerable<object> cList)
                {
                    foreach (object cItem in cList)
                    {
                        string cVal = cItem?.ToString()?.Trim().Trim('\'', '"') ?? string.Empty;
                        if (!string.IsNullOrEmpty(cVal) && !info.Creators.Contains(cVal, StringComparer.Ordinal))
                            info.Creators.Add(cVal);
                    }
                }

                foreach (string key in new[] { "url", "funding_url" })
                {
                    if (yamlMap.TryGetValue(key, out object? urlObj) && urlObj is not null)
                    {
                        string uVal = CatalogService.NormalizeDistributionUrl(urlObj.ToString()!.Trim());
                        if (CatalogService.IsValidInternalUrl(uVal, fileName) && !info.Urls.Contains(uVal, StringComparer.Ordinal))
                            info.Urls.Add(uVal);
                    }
                }
            }
        }
        catch
        {
            // 정규식 폴백 처리
        }

        // 정규식 보완
        if (string.IsNullOrEmpty(info.ManifestName))
        {
            var mName = Regex.Match(mainPart, @"^name\s*:\s*(.+)$", RegexOptions.Multiline);
            if (mName.Success)
                info.ManifestName = WebUtility.HtmlDecode(mName.Groups[1].Value.Trim().Trim('\'', '"'));
        }

        if (string.IsNullOrEmpty(info.Version))
        {
            var mVer = Regex.Match(mainPart, @"^version\s*:\s*(.+)$", RegexOptions.Multiline);
            if (mVer.Success)
                info.Version = mVer.Groups[1].Value.Trim().Trim('\'', '"').TrimStart('v', 'V');
        }

        var cBlock = Regex.Match(mainPart, @"^creators\s*:\s*\r?\n((?:\s*-\s*.+\r?\n?)+)", RegexOptions.Multiline);
        if (cBlock.Success)
        {
            foreach (Match mc in Regex.Matches(cBlock.Groups[1].Value, @"^\s*-\s*(.+)$", RegexOptions.Multiline))
            {
                string cVal = mc.Groups[1].Value.Trim().Trim('\'', '"');
                if (!string.IsNullOrEmpty(cVal) && !info.Creators.Contains(cVal, StringComparer.Ordinal))
                    info.Creators.Add(cVal);
            }
        }

        foreach (Match mu in Regex.Matches(mainPart, @"^(?:url|funding_url)\s*:\s*(https?://\S+)", RegexOptions.Multiline))
        {
            string uVal = CatalogService.NormalizeDistributionUrl(mu.Groups[1].Value.Trim());
            if (CatalogService.IsValidInternalUrl(uVal, fileName) && !info.Urls.Contains(uVal, StringComparer.Ordinal))
                info.Urls.Add(uVal);
        }
    }

    private static string CleanPycUrl(byte[] rawBytes, Match match)
    {
        string url = match.Value;
        int start = match.Index;
        if (start >= 2 && rawBytes[start - 2] is 0x7A or 0x5A or 0xDA or 0xFA or 0x74)
        {
            int expectedLen = rawBytes[start - 1];
            if (expectedLen >= 10 && expectedLen < url.Length)
                url = url[..expectedLen];
        }
        else
        {
            url = Regex.Replace(url, @"[zTrg]$", "");
        }

        return CatalogService.NormalizeDistributionUrl(url);
    }

    private static bool TryDecompressZlib(byte[] compressed, out byte[] decompressed)
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            decompressed = output.ToArray();
            return true;
        }
        catch
        {
            decompressed = Array.Empty<byte>();
            return false;
        }
    }

    private static bool StartsWithXmlMarker(ReadOnlySpan<byte> data)
    {
        int i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            i = 3;

        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            i++;

        ReadOnlySpan<byte> slice = data[i..];
        return slice.StartsWith("<?xml"u8) || slice.StartsWith("<I "u8) || slice.StartsWith("<M "u8);
    }

    private static void ExtractBinaryCaspOrNameMap(byte[] decompressed, SingleArchiveInfo info)
    {
        if (decompressed.Length >= 30 &&
            decompressed[0] is >= 0x18 and <= 0x45 &&
            decompressed[1] == 0 && decompressed[2] == 0 && decompressed[3] == 0 &&
            decompressed[15] == 0)
        {
            try
            {
                int len = Math.Min(100, decompressed.Length - 14);
                string u16 = Encoding.Unicode.GetString(decompressed, 14, len).Split('\0')[0];
                var mCasp = Regex.Match(u16, @"^([A-Za-z0-9_\-\[\]\s]{4,60})");
                if (mCasp.Success && (mCasp.Groups[1].Value.Contains('_') || mCasp.Groups[1].Value.Contains('[')))
                    info.RootTunings.Add(mCasp.Groups[1].Value.Trim());
            }
            catch
            {
                // 무시
            }
        }
        else if (decompressed.Length >= 4 &&
                 decompressed[0] == 0x01 && decompressed[1] == 0x00 &&
                 decompressed[2] == 0x00 && decompressed[3] == 0x00)
        {
            int sliceLen = Math.Min(512, decompressed.Length);
            string nmText = Encoding.UTF8.GetString(decompressed, 0, sliceLen);
            foreach (Match mNm in Regex.Matches(nmText, @"(\[[A-Za-z0-9_\-\s]{2,25}\]\s*[A-Za-z0-9_\-\s]{3,40})"))
            {
                string nmStr = mNm.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(nmStr) && !info.RootTunings.Contains(nmStr, StringComparer.Ordinal))
                    info.RootTunings.Add(nmStr);
            }
        }
    }

    private static bool IsIgnoredTuningPart(string token) =>
        token.ToLowerInvariant() is "body" or "top" or "bottom" or "female" or "male" or "buff" or "loot" or "testset" or "animations";

    private static bool IsIgnoredSecondaryTuningPart(string token) =>
        token.ToLowerInvariant() is "buff" or "loot" or "trait" or "shared" or "testset" or "simpicker" or "piemenu" or "manageworkoutfit" or "chatting";

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return string.Empty;
    }

    private static bool IsHexOrNumericId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.Trim();
        if (HexKeyRegex.IsMatch(trimmed) || PureHexIdRegex.IsMatch(trimmed))
            return true;

        return trimmed.All(char.IsDigit);
    }

    private static bool IsIgnoredFilenameBracketTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return true;

        string trimmed = tag.Trim();
        if (IsHexOrNumericId(trimmed) || NonCreatorFilenamePrefixes.Contains(trimmed))
            return true;

        if (Regex.IsMatch(trimmed, @"^\d+(?:\.\d+)*(?:\s*[xX]|\s*scaled?.*)?$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(trimmed, @"^(?:scaled|scale|version|wip|update|updated|fixed|fix|merged|hq|nocc|bgc|base\s*game|test|beta|demo|patreon|free|public|ts4|sims\s*4|s4s|patch|mod|mods|cc|cas|pack)$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(trimmed, @"^v(?:er|ersion)?\s*\d+(?:\.\d+)*[a-z0-9]*$", RegexOptions.IgnoreCase))
        {
            return true;
        }

        return trimmed.Length < 2;
    }

    private static string? ExtractPrefixedPackCreatorFromStem(string stem)
    {
        Match mShort = Regex.Match(stem, @"^(?:Anim|Pose)[_\-\s]+([A-Za-z0-9]_[A-Za-z0-9.\-]{1,20})(?:[_\-\s]+|$)", RegexOptions.IgnoreCase);
        if (mShort.Success)
            return mShort.Groups[1].Value.Trim();

        Match m = Regex.Match(stem, @"^(?:Anim|Pose)[_\-\s]+([A-Za-z0-9][A-Za-z0-9.\-]{1,40}?)(?:[_\-\s]+|$)", RegexOptions.IgnoreCase);
        if (!m.Success)
            return null;

        string cand = m.Groups[1].Value.Trim();
        if (cand.Length < 2 ||
            NonCreatorFilenamePrefixes.Contains(cand) ||
            Regex.IsMatch(cand, @"^(?:anim|anims|animations|custom|sounds|sound|pack|packs|props|prop|objects|obj|override|overrides|default|settings|patch|tuning|test|v\d+.*)$", RegexOptions.IgnoreCase))
        {
            return null;
        }

        return cand;
    }

    private static string? ResolveDbpfStringValue(
        FileStream fs,
        List<(uint Offset, uint Size, int Priority)> stblEntries,
        Dictionary<uint, string> stblMap,
        ref int nextStblIdx,
        string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        string trimmed = rawValue.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            string hexPart = trimmed[2..].Trim();
            if (!uint.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint key) || key == 0)
                return null;

            int loadedChunks = 0;
            while (!stblMap.ContainsKey(key) && nextStblIdx < stblEntries.Count && loadedChunks < 16)
            {
                TryLoadMoreStbl(fs, stblEntries, ref nextStblIdx, stblMap);
                loadedChunks++;
            }

            if (stblMap.TryGetValue(key, out string? resolved) &&
                !string.IsNullOrWhiteSpace(resolved) &&
                !IsHexOrNumericId(resolved))
            {
                return resolved.Trim();
            }

            return null;
        }

        return IsHexOrNumericId(trimmed) ? null : trimmed;
    }

    private static void TryLoadMoreStbl(
        FileStream fs,
        List<(uint Offset, uint Size, int Priority)> stblEntries,
        ref int nextStblIdx,
        Dictionary<uint, string> stblMap)
    {
        if (nextStblIdx >= stblEntries.Count)
            return;

        (uint offset, uint size, int _) = stblEntries[nextStblIdx];
        nextStblIdx++;

        try
        {
            if (offset == 0 || size < 21 || size > 2_000_000)
                return;

            long prevPos = fs.Position;
            byte[] chunk = new byte[size];
            fs.Position = offset;
            int read = fs.Read(chunk, 0, chunk.Length);
            fs.Position = prevPos;
            if (read < 2)
                return;

            byte[] raw;
            if (chunk[0] == 0x78)
            {
                using var ms = new MemoryStream(chunk, 0, read);
                using var zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                zs.CopyTo(outMs);
                raw = outMs.ToArray();
            }
            else
            {
                raw = read == chunk.Length ? chunk : chunk[..read];
            }

            if (raw.Length < 21 ||
                raw[0] != (byte)'S' || raw[1] != (byte)'T' || raw[2] != (byte)'B' || raw[3] != (byte)'L')
            {
                return;
            }

            ulong count = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(7, 8));
            ulong maxCount = Math.Min(count, 200_000UL);
            int sp = 21;
            for (ulong i = 0; i < maxCount; i++)
            {
                if (sp + 7 > raw.Length)
                    break;

                uint key = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(sp, 4));
                ushort strLen = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(sp + 5, 2));
                sp += 7;
                if (sp + strLen > raw.Length)
                    break;

                if (key != 0 && strLen > 0 && !stblMap.ContainsKey(key))
                {
                    string decoded = Encoding.UTF8.GetString(raw, sp, strLen).Trim('\0').Trim();
                    if (!string.IsNullOrEmpty(decoded))
                        stblMap[key] = decoded;
                }

                sp += strLen;
            }
        }
        catch
        {
            // 손상된 STBL 청크는 건너뜀
        }
    }

    /// <summary>
    /// ModUnit 대표 파일(또는 포함된 .package 파일)에서 Sims 4 DBPF 내장 썸네일(JPEG/PNG) 바이트를 추출합니다.
    /// 썸네일이 없으면 null을 반환합니다.
    /// </summary>
    public byte[]? ExtractThumbnailBytesForUnit(ModUnit unit)
    {
        if (unit is null)
            return null;

        if (!string.IsNullOrWhiteSpace(unit.PrimaryFilePath))
        {
            byte[]? primaryThumb = ExtractThumbnailBytes(unit.PrimaryFilePath);
            if (primaryThumb is not null)
                return primaryThumb;
        }

        int checkedCount = 0;
        foreach (string file in unit.UnitFiles)
        {
            if (checkedCount >= 6)
                break;

            if (string.Equals(file, unit.PrimaryFilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!file.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
                continue;

            checkedCount++;
            byte[]? thumb = ExtractThumbnailBytes(file);
            if (thumb is not null)
                return thumb;
        }

        return null;
    }

    /// <summary>
    /// 단일 .package(DBPF) 파일에서 내장 썸네일(THUM JPEG/PNG) 바이트를 추출합니다.
    /// </summary>
    public byte[]? ExtractThumbnailBytes(string? filePath)
    {
        if (!TryOpenDbpfAndIndex(filePath, maxIndexBytes: 524288UL, out FileStream? fs, out DbpfIndexTable index))
            return null;

        try
        {
            using FileStream stream = fs!;
            int pos = index.StartPos;
            uint scanLimit = Math.Min(index.EntryCount, 8192u);
            var candidates = new List<(uint Offset, uint Size, uint MemSize, bool IsPrimaryThumb)>();

            for (uint i = 0; i < scanLimit; i++)
            {
                if (!TryReadDbpfEntry(in index, ref pos, out DbpfEntryHeader entry))
                    break;

                bool isPrimary = IsPrimaryThumbnailResourceType(entry.TypeId);
                bool isFallbackImage = entry.TypeId == 0x2F7D0004u;
                if (!isPrimary && !isFallbackImage)
                    continue;

                if (entry.Offset < 96 || entry.FileSize < 16 || entry.FileSize > 1_048_576 || entry.MemSize > 2_097_152)
                    continue;

                if ((ulong)entry.Offset + entry.FileSize > (ulong)stream.Length)
                    continue;

                candidates.Add((entry.Offset, entry.FileSize, entry.MemSize, isPrimary));
            }

            if (candidates.Count == 0)
                return null;

            candidates.Sort((a, b) =>
            {
                int prioCmp = b.IsPrimaryThumb.CompareTo(a.IsPrimaryThumb);
                if (prioCmp != 0)
                    return prioCmp;
                uint aMetric = Math.Max(a.MemSize, a.Size);
                uint bMetric = Math.Max(b.MemSize, b.Size);
                return bMetric.CompareTo(aMetric);
            });

            int maxAttempts = Math.Min(candidates.Count, 8);
            for (int i = 0; i < maxAttempts; i++)
            {
                (uint offset, uint fsize, _, _) = candidates[i];
                byte[]? img = TryReadDecompressedDbpfChunk(stream, offset, fsize, minBytes: 16, maxBytes: 2_097_152);
                if (img is not null && IsValidThumbnailImageHeader(img))
                    return img;
            }
        }
        catch
        {
            // 손상되었거나 잠긴 DBPF 파일은 썸네일 없음 처리
        }

        return null;
    }

    /// <summary>
    /// DBPF 리소스 타입/스니펫 및 .ts4script 유무를 분석하여 4대 모드 유형을 자동 분류합니다.
    /// </summary>
    public ModCategoryClassification ClassifyFunctionalCategory(ModUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        List<string> files = unit.UnitFiles.Count > 0
            ? unit.UnitFiles
            : (!string.IsNullOrWhiteSpace(unit.PrimaryFilePath) ? [unit.PrimaryFilePath] : []);

        bool hasScript = files.Any(f => f.EndsWith(".ts4script", StringComparison.OrdinalIgnoreCase));
        if (hasScript)
        {
            bool hasPackageToo = files.Any(f => f.EndsWith(".package", StringComparison.OrdinalIgnoreCase));
            return new ModCategoryClassification
            {
                Category = ModFunctionalCategory.ScriptCoreMod,
                SubCategory = hasPackageToo ? "스크립트 + 패키지 코어" : "스크립트 코어 (.ts4script)",
                Reason = hasPackageToo
                    ? ".ts4script 스크립트 엔진과 .package 리소스가 결합된 핵심 모드"
                    : "Python .ts4script 아카이브를 실행하는 핵심 스크립트 모드"
            };
        }

        int caspCount = 0;
        int cobjCount = 0;
        int clipCount = 0;
        int stblCount = 0;
        int meshTexAudioCount = 0;
        int gameplayXmlCount = 0;
        bool hasAnimManifest = false;
        bool hasPoseManifest = false;
        int snippetInspected = 0;
        int scannedPackages = 0;

        foreach (string pkgPath in files)
        {
            if (scannedPackages >= 24)
                break;

            if (!pkgPath.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
                continue;

            scannedPackages++;
            if (!TryOpenDbpfAndIndex(pkgPath, maxIndexBytes: 524288UL, out FileStream? fs, out DbpfIndexTable index))
                continue;

            try
            {
                using FileStream stream = fs!;
                int pos = index.StartPos;
                uint scanLimit = Math.Min(index.EntryCount, 16384u);
                var snippetEntries = new List<(uint Offset, uint FileSize)>();

                for (uint i = 0; i < scanLimit; i++)
                {
                    if (!TryReadDbpfEntry(in index, ref pos, out DbpfEntryHeader entry))
                        break;

                    uint t = entry.TypeId;
                    if (t == 0x034AEECBu)
                        caspCount++;
                    else if (IsObjectResourceType(t))
                        cobjCount++;
                    else if (t == 0x6B20C4F3u)
                        clipCount++;
                    else if (t == 0x220557DAu)
                        stblCount++;
                    else if (IsMeshTextureOrAudioType(t))
                        meshTexAudioCount++;
                    else if (IsGameplayXmlResourceType(t))
                        gameplayXmlCount++;
                    else if (t == 0x7DF2169Cu && snippetInspected + snippetEntries.Count < 10)
                    {
                        if (entry.Offset >= 96 && entry.FileSize > 0 && entry.FileSize <= 524_288 &&
                            (ulong)entry.Offset + entry.FileSize <= (ulong)stream.Length)
                        {
                            snippetEntries.Add((entry.Offset, entry.FileSize));
                        }
                    }
                }

                foreach (var (offset, fsize) in snippetEntries)
                {
                    if (snippetInspected >= 10)
                        break;

                    snippetInspected++;
                    byte[]? raw = TryReadDecompressedDbpfChunk(stream, offset, fsize, minBytes: 8, maxBytes: 1_048_576);
                    if (raw is null || !StartsWithXmlMarker(raw))
                        continue;

                    int prefixLen = Math.Min(raw.Length, 1024);
                    string xmlHead = Encoding.UTF8.GetString(raw, 0, prefixLen);
                    if (xmlHead.Contains("CustomCASPartsPackage", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (xmlHead.Contains("AnimationPackage", StringComparison.OrdinalIgnoreCase) ||
                        xmlHead.Contains("CustomAnimation", StringComparison.OrdinalIgnoreCase) ||
                        xmlHead.Contains("DanceAnimation", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAnimManifest = true;
                    }
                    else if (xmlHead.Contains("PosePack", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPoseManifest = true;
                    }
                    else if (xmlHead.Contains("<I ", StringComparison.OrdinalIgnoreCase) ||
                             xmlHead.Contains("<M ", StringComparison.OrdinalIgnoreCase))
                    {
                        gameplayXmlCount++;
                    }
                }
            }
            catch
            {
                // 잠겼거나 손상된 DBPF 패키지는 부분 통계만 활용
            }
        }

        bool hasCasp = caspCount > 0;
        bool hasCobj = cobjCount > 0;
        bool hasGameplayXml = gameplayXmlCount > 0;
        bool hasStbl = stblCount > 0;
        bool hasMeshTexAudio = meshTexAudioCount > 0;
        string summaryReason = BuildClassificationReasonSummary(
            caspCount, cobjCount, gameplayXmlCount, clipCount, stblCount, meshTexAudioCount, hasAnimManifest, hasPoseManifest);

        // 3번 버킷(작동 기능 오브젝트)을 애니 매니페스트보다 우선 판정하여 모션 포함 오브젝트를 구분
        if (hasCobj && hasGameplayXml)
        {
            return new ModCategoryClassification
            {
                Category = ModFunctionalCategory.ObjectModAndCc,
                SubCategory = (clipCount > 0 || hasAnimManifest) ? "작동 기능 오브젝트 (+모션)" : "작동 기능 오브젝트",
                Reason = summaryReason
            };
        }

        if ((hasAnimManifest && clipCount > 0) ||
            (hasPoseManifest && !hasCobj) ||
            (clipCount > 0 && !hasGameplayXml && !hasCasp && !hasCobj))
        {
            string sub = hasPoseManifest
                ? "포즈 팩"
                : (hasAnimManifest ? "애니메이션 팩" : "동작 / CLIP");
            return new ModCategoryClassification
            {
                Category = ModFunctionalCategory.AnimationAndPose,
                SubCategory = sub,
                Reason = summaryReason
            };
        }

        if (hasGameplayXml && !hasCasp && !hasCobj)
        {
            return new ModCategoryClassification
            {
                Category = ModFunctionalCategory.XmlGameplayMod,
                SubCategory = "XML 튜닝 모드",
                Reason = summaryReason
            };
        }

        if (hasCasp || hasCobj || hasMeshTexAudio)
        {
            string sub = hasCasp && hasGameplayXml
                ? "기능 동반 CAS"
                : (hasCasp ? "CAS CC" : (hasCobj ? "일반 오브젝트 · 가구 CC" : "사운드 · 텍스처 · 슬라이더"));
            return new ModCategoryClassification
            {
                Category = ModFunctionalCategory.ObjectModAndCc,
                SubCategory = sub,
                Reason = summaryReason
            };
        }

        if (hasGameplayXml || hasStbl)
        {
            return new ModCategoryClassification
            {
                Category = ModFunctionalCategory.XmlGameplayMod,
                SubCategory = hasGameplayXml ? "XML 튜닝 모드" : "번역 / 텍스트 팩 (STBL)",
                Reason = summaryReason
            };
        }

        return new ModCategoryClassification
        {
            Category = ModFunctionalCategory.ObjectModAndCc,
            SubCategory = "기타 CC / 리소스",
            Reason = string.IsNullOrWhiteSpace(summaryReason) ? "특정 튜닝·애니메이션 시그니처 없음 (일반 리소스)" : summaryReason
        };
    }

    private readonly record struct DbpfEntryHeader(uint TypeId, uint Offset, uint FileSize, uint MemSize);

    private readonly record struct DbpfIndexTable(
        byte[] Data,
        int Length,
        int StartPos,
        int EntrySize,
        uint EntryCount,
        bool HasConstType,
        uint ConstType);

    private static bool TryOpenDbpfAndIndex(
        string? filePath,
        ulong maxIndexBytes,
        out FileStream? fs,
        out DbpfIndexTable index)
    {
        fs = null;
        index = default;

        if (string.IsNullOrWhiteSpace(filePath) ||
            !filePath.EndsWith(".package", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(filePath))
        {
            return false;
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < 96)
            {
                stream.Dispose();
                return false;
            }

            byte[] header = new byte[96];
            if (stream.Read(header, 0, 96) < 96)
            {
                stream.Dispose();
                return false;
            }

            if (header[0] != (byte)'D' || header[1] != (byte)'B' || header[2] != (byte)'P' || header[3] != (byte)'F')
            {
                stream.Dispose();
                return false;
            }

            uint idxCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36, 4));
            uint idxOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(64, 4));
            if (idxOffset == 0)
                idxOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(40, 4));

            if (idxCount == 0 || idxOffset == 0 || idxOffset >= stream.Length)
            {
                stream.Dispose();
                return false;
            }

            stream.Position = idxOffset;
            int idxBytesToRead = (int)Math.Min(idxCount * 32UL + 32UL, maxIndexBytes);
            byte[] idxData = new byte[idxBytesToRead];
            int idxRead = stream.Read(idxData, 0, idxBytesToRead);
            if (idxRead < 24)
            {
                stream.Dispose();
                return false;
            }

            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(0, 4));
            int hdrPos = 4;
            bool hasConstType = (flags & 1u) != 0;
            uint constType = hasConstType && hdrPos + 4 <= idxRead
                ? BinaryPrimitives.ReadUInt32LittleEndian(idxData.AsSpan(hdrPos, 4))
                : 0;
            if (hasConstType) hdrPos += 4;

            bool hasConstGroup = (flags & 2u) != 0;
            if (hasConstGroup) hdrPos += 4;

            bool hasConstInstEx = (flags & 4u) != 0;
            if (hasConstInstEx) hdrPos += 4;

            int entryConstSize = 20 + (hasConstType ? 0 : 4) + (hasConstGroup ? 0 : 4) + (hasConstInstEx ? 0 : 4);
            if (entryConstSize < 20)
            {
                stream.Dispose();
                return false;
            }

            index = new DbpfIndexTable(idxData, idxRead, hdrPos, entryConstSize, idxCount, hasConstType, constType);
            fs = stream;
            return true;
        }
        catch
        {
            stream?.Dispose();
            return false;
        }
    }

    private static bool TryReadDbpfEntry(in DbpfIndexTable index, ref int pos, out DbpfEntryHeader entry)
    {
        if (pos + index.EntrySize > index.Length)
        {
            entry = default;
            return false;
        }

        uint entryType = index.HasConstType
            ? index.ConstType
            : BinaryPrimitives.ReadUInt32LittleEndian(index.Data.AsSpan(pos, 4));
        int tailPos = pos + index.EntrySize - 20;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(index.Data.AsSpan(tailPos + 4, 4));
        uint fsize = BinaryPrimitives.ReadUInt32LittleEndian(index.Data.AsSpan(tailPos + 8, 4)) & 0x7FFFFFFFu;
        uint memSize = BinaryPrimitives.ReadUInt32LittleEndian(index.Data.AsSpan(tailPos + 12, 4));
        pos += index.EntrySize;
        entry = new DbpfEntryHeader(entryType, offset, fsize, memSize);
        return true;
    }

    private static byte[]? TryReadDecompressedDbpfChunk(FileStream fs, uint offset, uint fsize, int minBytes, int maxBytes)
    {
        if (offset < 96 || fsize == 0 || fsize > maxBytes || (ulong)offset + fsize > (ulong)fs.Length)
            return null;

        try
        {
            byte[] chunk = new byte[fsize];
            fs.Position = offset;
            int read = fs.Read(chunk, 0, (int)fsize);
            if (read < minBytes)
                return null;

            byte[] raw;
            if (chunk[0] == 0x78)
            {
                using var ms = new MemoryStream(chunk, 0, read);
                using var zs = new ZLibStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                zs.CopyTo(outMs);
                if (outMs.Length < minBytes || outMs.Length > maxBytes)
                    return null;
                raw = outMs.ToArray();
            }
            else
            {
                raw = read == chunk.Length ? chunk : chunk[..read];
            }

            return raw;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsObjectResourceType(uint typeId) =>
        typeId is 0x319E4F1Du // COBJ
            or 0xC0DB5AE7u    // OBJD
            or 0xD5F0F921u    // RSLT
            or 0xB4F762C9u    // FTPT
            or 0xA057811Cu    // Slot / Object
            or 0x0418FE2Au;   // Wall / Floor Pattern

    private static bool IsMeshTextureOrAudioType(uint typeId) =>
        typeId is 0x015A1849u // GEOM
            or 0x067CAA11u    // BGEO / Blend
            or 0x0355E0A6u    // Bone / Rig
            or 0x01D10F34u    // MLOD
            or 0x00B2D882u    // DDS / DST
            or 0xBA856C78u    // RLE2 Texture
            or 0x01A527DBu;   // Audio / SNR

    private static bool IsGameplayXmlResourceType(uint typeId) =>
        typeId is 0x03B33DDFu
            or 0xE882D22Fu
            or 0x6017E896u
            or 0xCB5FDDC7u
            or 0x0C772E27u
            or 0x339BC5BDu
            or 0x73996BEBu
            or 0xB61DE6B4u
            or 0x02D5DF13u
            or 0x5B02819Eu
            or 0xFBC3AEEBu
            or 0x3C1D8799u
            or 0x0904DF10u
            or 0x03E9D964u
            or 0xEB97F823u
            or 0x6E0DDA9Fu
            or 0x0E4D15FBu
            or 0x2553F435u
            or 0xC020FCADu
            or 0xE350DBD8u
            or 0xDEBAFB73u
            or 0x9C07855Fu;

    private static string BuildClassificationReasonSummary(
        int caspCount,
        int cobjCount,
        int gameplayXmlCount,
        int clipCount,
        int stblCount,
        int meshTexAudioCount,
        bool hasAnimManifest,
        bool hasPoseManifest)
    {
        var parts = new List<string>();
        if (hasAnimManifest)
            parts.Add("WW/애니 스니펫");
        if (hasPoseManifest)
            parts.Add("포즈팩 스니펫");
        if (gameplayXmlCount > 0)
            parts.Add($"XML 튜닝 {gameplayXmlCount}");
        if (cobjCount > 0)
            parts.Add($"COBJ {cobjCount}");
        if (caspCount > 0)
            parts.Add($"CASP {caspCount}");
        if (clipCount > 0)
            parts.Add($"CLIP {clipCount}");
        if (stblCount > 0)
            parts.Add($"STBL {stblCount}");
        if (meshTexAudioCount > 0)
            parts.Add($"메시/텍스처/사운드 {meshTexAudioCount}");
        return string.Join(" · ", parts);
    }

    private static bool IsPrimaryThumbnailResourceType(uint typeId) =>
        typeId is 0x3C2A8647u // Object Catalog THUM
            or 0x3C1AF1F2u    // CAS Part THUM
            or 0x5B282D45u    // Build/Buy THUM
            or 0x0580A2B4u
            or 0x0580A2B5u
            or 0x0580A2B6u
            or 0x0D338A3Au
            or 0x2653E3C8u
            or 0x2653E3C9u
            or 0x2653E3CAu
            or 0xCD9DE247u;

    private static bool IsValidThumbnailImageHeader(byte[] data)
    {
        if (data.Length < 16)
            return false;

        // JPEG (SOI FF D8 FF)
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return true;

        // PNG (89 50 4E 47 0D 0A 1A 0A)
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return true;
        }

        return false;
    }

    internal sealed class SingleArchiveInfo
    {
        public string ManifestName { get; set; } = string.Empty;
        public List<string> Creators { get; } = new();
        public List<string> Urls { get; } = new();
        public List<string> DisplayNames { get; } = new();
        public List<string> RootTunings { get; } = new();
        public List<string> Modules { get; } = new();
        public string Version { get; set; } = string.Empty;
        public bool IsAnimationAddon { get; set; }
    }
}
