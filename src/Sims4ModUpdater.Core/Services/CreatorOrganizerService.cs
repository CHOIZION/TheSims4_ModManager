using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 제작자 자동 판별, 사용자 제작자 지정(creator_overrides.json) 저장,
/// 및 심즈 4 스크립트 깊이 규칙(1단계 이하)을 준수하는 Mods 폴더 자동 정리 서비스
/// </summary>
public sealed class CreatorOrganizerService
{
    public const string UnknownCreatorLabel = "미분류 (제작자 불명)";
    public const string UncategorizedFolderName = "Unknown";
    public const string OverridesFolderName = "Overrides";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> GenericTagTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "mod", "mods", "cc", "cas", "prio", "pack", "patch", "ko", "kor", "kr",
        "korean", "eng", "translation", "fixed", "fix", "update", "updated",
        "한글", "한글화", "번역", "수정", "업데이트", "필수", "코어", "메인",
        "선택", "옵션", "optional", "required", "addon", "addons", "unknown",
        "ea", "maxis", "sims4", "thesims4", "package", "ts4script", "script",
        "beta", "alpha", "wip", "test", "v1", "v2", "v3",
        "0x", "relbit", "moodlet", "statistic", "stat", "affordance", "recipe",
        "skill", "aspiration", "whim", "reward", "snippet", "pose", "posepack",
        "anim", "anims", "animation", "animations", "prop", "props", "commodity",
        "social", "superinteraction", "tooltip", "tuning", "s4s", "scaled",
        "scale", "default", "override", "overrides", "bgc", "basegame", "nocc",
        "mesh", "recolor", "texture", "ww", "custom", "mystery", "random",
        "untitled", "sample", "example", "item", "items", "object", "objects",
        "part", "collection", "starter", "startingpack", "merged", "bundle",
        "spot", "objektbeschreibung", "remera",
        "chrome", "double", "fake", "free", "high", "lay",
        "long", "love", "mini", "oll", "personal", "real", "remastered",
        "simple", "small", "short", "vertical", "wide",
        "xframe", "super", "ultra", "mega"
    };

    private static readonly Dictionary<string, string> CreatorAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deaderpool"] = "Deaderpool",
        ["mccc"] = "Deaderpool",
        ["turbodriver"] = "TURBODRIVER",
        ["turbrodriver"] = "TURBODRIVER",
        ["wonderfulwhims"] = "TURBODRIVER",
        ["weerbesu"] = "Weerbesu",
        ["twistedmexi"] = "TwistedMexi",
        ["tmex"] = "TwistedMexi",
        ["littlemssam"] = "LittleMsSam",
        ["littlmssam"] = "LittleMsSam",
        ["lms"] = "LittleMsSam",
        ["lumpinou"] = "Lumpinou",
        ["pandasama"] = "PandaSama",
        ["simrealist"] = "SimRealist",
        ["adeepindigo"] = "adeepindigo",
        ["a.deep.indigo"] = "adeepindigo",
        ["chingyu"] = "chingyu1023",
        ["chingyu1023"] = "chingyu1023",
        ["kuttoe"] = "Kuttoe",
        ["lot51"] = "Lot51",
        ["kiarasims4mods"] = "KiaraSims4Mods",
        ["kiarasims"] = "KiaraSims4Mods",
        ["midnitetech"] = "Midnitetech",
        ["zero"] = "Zero",
        ["kawaiistacie"] = "Kawaiistacie",
        ["scarlet"] = "Scarlet",
        ["andrew"] = "Sims 4 Studio",
        ["sims4studio"] = "Sims 4 Studio",
        ["scumbumbo"] = "Scumbumbo",
        ["frankk"] = "Frankk",
        ["carl"] = "Carl",
        ["ravasheen"] = "Ravasheen",
        ["rvsn"] = "Ravasheen",
        ["spinachy"] = "Spinachy",
        ["thepancake1"] = "thepancake1",
        ["mizoreyukii"] = "MizoreYukii",
        ["bienchen"] = "Bienchen",
        ["sims4me"] = "Bienchen",
        ["missyhissy"] = "MissyHissy",
        ["simmiller"] = "Simmiller",
        ["marlynsims"] = "MarlynSims",
        ["ilkaavelle"] = "ilkavelle",
        ["ilkavelle"] = "ilkavelle",
        ["leRoiDeTout"] = "LeRoiDeTout",
        ["leroidetout"] = "LeRoiDeTout",
        ["roburky"] = "roBurky",
        ["triplis"] = "Triplis",
        ["polarbearsims"] = "Polarbearsims",
        ["chingyu_1023"] = "chingyu1023",
        ["arazzi"] = "Arazzi",
        ["sulsul"] = "SulSul",
        ["aladdin"] = "Aladdin",
        ["motherlodesims"] = "MOTHERLODESIMS",
        ["motherlode"] = "MOTHERLODESIMS",
        ["ats4"] = "ATS4",
        ["aroundthesims4"] = "ATS4",
        ["aroundthesims"] = "ATS4",
        ["haneco"] = "haneco",
        ["haneco410"] = "haneco",
        ["ddaengsims"] = "ddaengsims",
        ["noiranddarksims4"] = "Noir Studio",
        ["noiranddarksims"] = "Noir Studio",
        ["noiranddark"] = "Noir Studio",
        ["noirdarksims"] = "Noir Studio",
        ["noir"] = "Noir Studio",
        ["simc"] = "SIMc",
        ["syb"] = "SYB",
        ["syboulette"] = "SYB",
        ["yrsa"] = "YrSa",
        ["cuupidon"] = "CUUPIDON",
        ["lorysims"] = "LorySims",
        ["myobi"] = "MYOBI",
        ["redheadsims"] = "redheadsims",
        ["necrodog"] = "necrodog",
        ["mike24"] = "mike24",
        ["beto"] = "Beto",
        ["bybeto"] = "Beto",
        ["channel4sims"] = "channel4sims",
        ["nv"] = "NV Games",
        ["nvgames"] = "NV Games"
    };

    private static readonly (string Prefix, string Creator)[] FilenameCreatorPrefixes =
    [
        ("mc_", "Deaderpool"),
        ("wonderfulwhims", "TURBODRIVER"),
        ("turbodriver", "TURBODRIVER"),
        ("tmex-", "TwistedMexi"),
        ("tmex_", "TwistedMexi"),
        ("twistedmexi", "TwistedMexi"),
        ("littlemssam", "LittleMsSam"),
        ("lms_", "LittleMsSam"),
        ("lumpinou", "Lumpinou"),
        ("pandasama", "PandaSama"),
        ("snb_", "SimRealist"),
        ("simrealist", "SimRealist"),
        ("adeepindigo", "adeepindigo"),
        ("chingyu", "chingyu1023"),
        ("kuttoe", "Kuttoe"),
        ("lot51", "Lot51"),
        ("kiarasims4mods", "KiaraSims4Mods"),
        ("midnitetech", "Midnitetech"),
        ("zero_", "Zero"),
        ("frankk_", "Frankk"),
        ("rvsn_", "Ravasheen"),
        ("ravasheen", "Ravasheen"),
        ("thepancake1", "thepancake1"),
        ("mizoreyukii", "MizoreYukii"),
        ("sims4me_", "Bienchen"),
        ("bienchen_", "Bienchen"),
        ("ilkavelle", "ilkavelle"),
        ("roburky", "roBurky"),
        ("ui_cheats", "Weerbesu"),
        ("barambyul", "바람별")
    ];

    private static readonly (string DomainHint, string Creator)[] UrlDomainCreatorHints =
    [
        ("deaderpool-mccc.com", "Deaderpool"),
        ("wonderfulwhims.com", "TURBODRIVER"),
        ("twistedmexi.com", "TwistedMexi"),
        ("littlemssam.tumblr.com", "LittleMsSam"),
        ("littlemssam.de", "LittleMsSam"),
        ("simrealist.com", "SimRealist"),
        ("kuttoe.itch.io", "Kuttoe"),
        ("lot51.cc", "Lot51"),
        ("lumpinoumods.com", "Lumpinou"),
        ("pandasama.com", "PandaSama"),
        ("adeepindigo.itch.io", "adeepindigo"),
        ("ravasheen.com", "Ravasheen"),
        ("scumbumbomods.com", "Scumbumbo"),
        ("sims4studio.com", "Sims 4 Studio"),
        ("kiarasims4mods.home.blog", "KiaraSims4Mods"),
        ("scarletsrealm.com", "Scarlet")
    ];

    private static readonly HashSet<string> CompanionSettingsExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cfg", ".ini", ".dat", ".txt", ".json", ".log"
    };

    private static readonly HashSet<string> FolderOrganizeDocExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cfg", ".ini", ".dat", ".txt", ".json", ".log", ".rtf", ".md", ".pdf", ".url"
    };

    private static readonly Regex StagingFolderRegex = new(
        @"^(?:test\d*|temp\d*|tmp\d*|new\d*|새\s*폴더(?:\s*\(\d+\))?|미분류|unknown|overrides|cc)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _overridesFilePath;
    private readonly CatalogService _catalogService;
    private readonly object _lock = new();

    public CreatorOrganizerService(string overridesFilePath, CatalogService? catalogService = null)
    {
        _overridesFilePath = overridesFilePath;
        _catalogService = catalogService ?? new CatalogService();
    }

    public string OverridesFilePath => _overridesFilePath;

    public List<CreatorOverrideEntry> LoadOverrides()
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(_overridesFilePath) || !File.Exists(_overridesFilePath))
                return new List<CreatorOverrideEntry>();

            try
            {
                string json = File.ReadAllText(_overridesFilePath, Encoding.UTF8);
                var doc = JsonSerializer.Deserialize<CreatorOverridesDocument>(json);
                return doc?.Overrides ?? new List<CreatorOverrideEntry>();
            }
            catch
            {
                return new List<CreatorOverrideEntry>();
            }
        }
    }

    public void SaveOverrides(List<CreatorOverrideEntry> entries)
    {
        lock (_lock)
        {
            string? dir = Path.GetDirectoryName(_overridesFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var doc = new CreatorOverridesDocument { Overrides = entries };
            string json = JsonSerializer.Serialize(doc, JsonOptions);
            File.WriteAllText(_overridesFilePath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public Dictionary<string, string> LoadOverrideLookup()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (CreatorOverrideEntry entry in LoadOverrides())
        {
            if (string.IsNullOrWhiteSpace(entry.CreatorName))
                continue;

            string cleanCreator = NormalizeCreatorName(entry.CreatorName);
            if (string.IsNullOrEmpty(cleanCreator))
                continue;

            if (!string.IsNullOrWhiteSpace(entry.ModKey))
                dict[entry.ModKey.Trim()] = cleanCreator;
            if (!string.IsNullOrWhiteSpace(entry.PrimaryFileName))
                dict[$"file::{entry.PrimaryFileName.Trim()}"] = cleanCreator;
        }
        return dict;
    }

    public void SetCreatorOverride(ModUnit unit, string? creatorName)
    {
        ArgumentNullException.ThrowIfNull(unit);

        string modKey = BuildOverrideKey(unit);
        lock (_lock)
        {
            var list = LoadOverrides();
            list.RemoveAll(e =>
                e.ModKey.Equals(modKey, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(unit.PrimaryFileName) &&
                 e.PrimaryFileName.Equals(unit.PrimaryFileName, StringComparison.OrdinalIgnoreCase)));

            string normalized = NormalizeCreatorName(creatorName);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !normalized.Equals(UnknownCreatorLabel, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new CreatorOverrideEntry
                {
                    ModKey = modKey,
                    PrimaryFileName = unit.PrimaryFileName,
                    FolderName = unit.FolderName,
                    CreatorName = normalized
                });
            }

            SaveOverrides(list);
        }
    }

    public void RenameOrMergeCreator(
        IEnumerable<ModUnit> affectedUnits,
        string newCreatorName)
    {
        string cleanNew = NormalizeCreatorName(newCreatorName);
        if (string.IsNullOrWhiteSpace(cleanNew))
            return;

        lock (_lock)
        {
            var list = LoadOverrides();
            var unitList = affectedUnits.ToList();
            var unitKeys = unitList.Select(BuildOverrideKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fileNames = unitList.Select(u => u.PrimaryFileName).Where(f => !string.IsNullOrEmpty(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            list.RemoveAll(e =>
                unitKeys.Contains(e.ModKey) ||
                (!string.IsNullOrEmpty(e.PrimaryFileName) && fileNames.Contains(e.PrimaryFileName)));

            if (!cleanNew.Equals(UnknownCreatorLabel, StringComparison.OrdinalIgnoreCase))
            {
                foreach (ModUnit unit in unitList)
                {
                    list.Add(new CreatorOverrideEntry
                    {
                        ModKey = BuildOverrideKey(unit),
                        PrimaryFileName = unit.PrimaryFileName,
                        FolderName = unit.FolderName,
                        CreatorName = cleanNew
                    });
                }
            }

            SaveOverrides(list);
        }
    }

    public static string BuildOverrideKey(ModUnit unit)
    {
        if (!string.IsNullOrWhiteSpace(unit.GroupKey))
            return unit.GroupKey;
        return $"{unit.FolderName}::{unit.PrimaryFileName}";
    }

    public static bool IsForcedOverridesFile(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
            return false;

        string fileName = Path.GetFileName(pathOrFileName).Trim();
        if (!fileName.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
            return false;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.StartsWith("!WAMTIK", StringComparison.OrdinalIgnoreCase) ||
               stem.StartsWith("_WAMTIK", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("WAMTIK", StringComparison.OrdinalIgnoreCase) ||
               stem.StartsWith("WAMTIK-", StringComparison.OrdinalIgnoreCase) ||
               stem.StartsWith("PersonalKO", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsResourceCfgFile(string? pathOrFileName)
    {
        if (string.IsNullOrWhiteSpace(pathOrFileName))
            return false;

        return string.Equals(Path.GetFileName(pathOrFileName).Trim(), "Resource.cfg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStagingOrSpecialFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return true;

        string trimmed = folderName.Trim();
        if (StagingFolderRegex.IsMatch(trimmed))
            return true;

        return trimmed.Equals("Compatibility_Fixes", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("zz_LibraryOnlyTownies", StringComparison.OrdinalIgnoreCase);
    }

    public static Dictionary<string, string> CollectExistingCreatorFolders(
        string modsFolder,
        string? excludeFolderFull = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder))
            return map;

        string? excludeNorm = string.IsNullOrWhiteSpace(excludeFolderFull)
            ? null
            : Path.GetFullPath(excludeFolderFull).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        foreach (string dir in Directory.EnumerateDirectories(modsFolder))
        {
            string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.IsNullOrEmpty(excludeNorm) && string.Equals(full, excludeNorm, StringComparison.OrdinalIgnoreCase))
                continue;

            string dirName = Path.GetFileName(full);
            if (IsStagingOrSpecialFolderName(dirName))
                continue;

            map.TryAdd(dirName, dirName);

            string cleanBracket = Regex.Replace(dirName, @"^\[([^\]]+)\]$", "$1").Trim();
            if (!string.IsNullOrWhiteSpace(cleanBracket))
                map.TryAdd(cleanBracket, dirName);

            string normalized = NormalizeCreatorName(dirName);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !dirName.Contains(" -MOD- ", StringComparison.OrdinalIgnoreCase) &&
                !dirName.Contains(" - ", StringComparison.Ordinal))
            {
                map.TryAdd(normalized, dirName);
            }
        }

        return map;
    }

    private static string? MatchExistingCreatorFolderFromFile(
        string? fileName,
        string? displayName,
        IReadOnlyDictionary<string, string>? existingCreatorFolders)
    {
        if (existingCreatorFolders is null || existingCreatorFolders.Count == 0)
            return null;

        string stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        string[] candidates = [stem, displayName ?? string.Empty];
        var uniqueFolders = existingCreatorFolders.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !f.Contains(" -MOD- ", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains(" - ", StringComparison.Ordinal) &&
                        IsAcceptableCreatorCandidate(f))
            .OrderByDescending(f => f.Length)
            .ToList();

        foreach (string folderName in uniqueFolders)
        {
            if (folderName.Length < 3)
                continue;

            string escaped = Regex.Escape(folderName);
            var rxBoundary = new Regex($@"^(?:\[[^\]]+\][\s_\-]*)?{escaped}(?:$|[\s_\-\.\]]|Animations?|Anims?|Poses?|Mods?)", RegexOptions.IgnoreCase);
            foreach (string cand in candidates)
            {
                if (!string.IsNullOrWhiteSpace(cand) && rxBoundary.IsMatch(cand.Trim()))
                    return folderName;
            }
        }

        return null;
    }

    private static string? TryResolveExistingCreatorFolder(
        string safeCreatorDir,
        IReadOnlyDictionary<string, string>? existingCreatorFolders)
    {
        if (existingCreatorFolders is null || existingCreatorFolders.Count == 0)
            return null;

        if (existingCreatorFolders.TryGetValue(safeCreatorDir, out string? matched))
            return matched;

        string normalized = NormalizeCreatorName(safeCreatorDir);
        if (!string.IsNullOrWhiteSpace(normalized) &&
            existingCreatorFolders.TryGetValue(normalized, out string? normMatched))
        {
            return normMatched;
        }

        return null;
    }

    public CreatorResolutionResult ResolveCreator(
        ModUnit unit,
        ModMetadata? metadata = null,
        string? knownUrl = null,
        IReadOnlyDictionary<string, string>? overrideLookup = null,
        IReadOnlySet<string>? knownBatchCreators = null,
        IReadOnlyDictionary<string, string>? existingCreatorFolders = null)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var (autoCreator, autoSource) = DetectAutoCreator(unit, metadata, knownUrl, knownBatchCreators, existingCreatorFolders);

        if (overrideLookup is not null)
        {
            string key = BuildOverrideKey(unit);
            if (overrideLookup.TryGetValue(key, out string? manual) ||
                (!string.IsNullOrWhiteSpace(unit.PrimaryFileName) &&
                 overrideLookup.TryGetValue($"file::{unit.PrimaryFileName}", out manual)))
            {
                return new CreatorResolutionResult
                {
                    Creator = manual,
                    IsKnown = true,
                    IsOverride = true,
                    Source = "manual_override",
                    AutoDetectedCreator = autoCreator
                };
            }
        }
        else
        {
            var lookup = LoadOverrideLookup();
            string key = BuildOverrideKey(unit);
            if (lookup.TryGetValue(key, out string? manual) ||
                (!string.IsNullOrWhiteSpace(unit.PrimaryFileName) &&
                 lookup.TryGetValue($"file::{unit.PrimaryFileName}", out manual)))
            {
                return new CreatorResolutionResult
                {
                    Creator = manual,
                    IsKnown = true,
                    IsOverride = true,
                    Source = "manual_override",
                    AutoDetectedCreator = autoCreator
                };
            }
        }

        if (!string.IsNullOrEmpty(autoCreator))
        {
            return new CreatorResolutionResult
            {
                Creator = autoCreator,
                IsKnown = true,
                IsOverride = false,
                Source = autoSource,
                AutoDetectedCreator = autoCreator
            };
        }

        return new CreatorResolutionResult
        {
            Creator = UnknownCreatorLabel,
            IsKnown = false,
            IsOverride = false,
            Source = "unknown",
            AutoDetectedCreator = string.Empty
        };
    }

    private (string Creator, string Source) DetectAutoCreator(
        ModUnit unit,
        ModMetadata? metadata,
        string? knownUrl,
        IReadOnlySet<string>? knownBatchCreators = null,
        IReadOnlyDictionary<string, string>? existingCreatorFolders = null)
    {
        // 1. Metadata creators (explicit DBPF XML / YAML creators — high confidence)
        if (metadata is not null)
        {
            foreach (string rawC in metadata.Creators)
            {
                string norm = NormalizeCreatorName(rawC);
                if (IsAcceptableCreatorCandidate(norm))
                    return (norm, "metadata");
            }

            // Colon-namespaced internal tuning IDs (e.g. AuthorA:PosePack_..., StudioB:Item_...)
            foreach (string rt in metadata.RootTunings)
            {
                int colonIdx = rt.IndexOf(':');
                if (colonIdx <= 0)
                    continue;

                string prefix = rt[..colonIdx].Trim();
                if (prefix.Equals("llamalogic", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string normRt = NormalizeCreatorName(prefix);
                if (IsAcceptableCreatorCandidate(normRt))
                    return (normRt, "metadata");
            }

            if (!string.IsNullOrWhiteSpace(metadata.InferredCreator))
            {
                string normInf = NormalizeCreatorName(metadata.InferredCreator);
                if (IsAcceptableCreatorCandidate(normInf))
                    return (normInf, "metadata");
            }
        }

        // 2. Built-in Known Catalog lookup
        var catalogQueries = new[]
        {
            metadata?.ManifestName,
            metadata?.InferredMod,
            unit.DisplayName,
            Path.GetFileNameWithoutExtension(unit.PrimaryFileName),
            unit.FolderName != "(Mods 루트)" ? unit.FolderName : null
        };

        foreach (string? q in catalogQueries)
        {
            if (string.IsNullOrWhiteSpace(q))
                continue;

            CatalogMatchResult? catMatch = _catalogService.CheckKnownCatalog(q);
            if (catMatch is { Found: true } && !string.IsNullOrWhiteSpace(catMatch.Creator))
            {
                string normCat = NormalizeCreatorName(catMatch.Creator);
                if (IsAcceptableCreatorCandidate(normCat))
                    return (normCat, "catalog");
            }
        }

        // 3. Embedded / pinned URL attribution
        var candidateUrls = new List<string>();
        if (!string.IsNullOrWhiteSpace(knownUrl))
            candidateUrls.Add(knownUrl);
        if (metadata is { Urls.Count: > 0 })
            candidateUrls.AddRange(metadata.Urls);

        foreach (string url in candidateUrls)
        {
            string? fromUrl = InferCreatorFromUrl(url);
            if (!string.IsNullOrEmpty(fromUrl) && IsAcceptableCreatorCandidate(fromUrl))
                return (fromUrl, "url_domain");
        }

        // 4. Filename and Folder heuristics
        string? fromFileOrFolder = InferCreatorFromNames(unit.FolderName, unit.PrimaryFileName, unit.DisplayName, knownBatchCreators);
        if (!string.IsNullOrEmpty(fromFileOrFolder) && IsAcceptableCreatorCandidate(fromFileOrFolder))
            return (fromFileOrFolder, "filename");

        // 5. Match against existing creator folders already organized in Mods root
        string? fromExistingFolder = MatchExistingCreatorFolderFromFile(unit.PrimaryFileName, unit.DisplayName, existingCreatorFolders);
        if (!string.IsNullOrEmpty(fromExistingFolder) && IsAcceptableCreatorCandidate(fromExistingFolder))
            return (fromExistingFolder, "filename");

        return (string.Empty, "unknown");
    }

    public static string? InferCreatorFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return null;

        string host = uri.Host.ToLowerInvariant();
        foreach (var (hint, creator) in UrlDomainCreatorHints)
        {
            if (host.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return creator;
        }

        if (host.EndsWith(".itch.io", StringComparison.OrdinalIgnoreCase))
        {
            string sub = host[..^".itch.io".Length].Trim('.');
            if (!string.IsNullOrWhiteSpace(sub) && sub != "www")
                return NormalizeCreatorName(sub);
        }

        if (host.Contains("patreon.com", StringComparison.OrdinalIgnoreCase))
        {
            string path = uri.AbsolutePath.Trim('/');
            string[] segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 2 && segs[0].Equals("c", StringComparison.OrdinalIgnoreCase))
                return NormalizeCreatorName(segs[1]);
            if (segs.Length >= 1 &&
                !segs[0].Equals("posts", StringComparison.OrdinalIgnoreCase) &&
                !segs[0].Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeCreatorName(segs[0]);
            }
        }

        if (host.EndsWith(".tumblr.com", StringComparison.OrdinalIgnoreCase))
        {
            string sub = host[..^".tumblr.com".Length].Trim('.');
            if (!string.IsNullOrWhiteSpace(sub) && sub != "www")
                return NormalizeCreatorName(sub);
        }

        return null;
    }

    public static string? InferCreatorFromNames(
        string? folderName,
        string? fileName,
        string? displayName,
        IReadOnlySet<string>? knownBatchCreators = null)
    {
        string fnLow = (fileName ?? string.Empty).ToLowerInvariant();
        string fldLow = (folderName ?? string.Empty).ToLowerInvariant();

        foreach (var (prefix, creator) in FilenameCreatorPrefixes)
        {
            if (fnLow.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                fldLow.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return creator;
            }
        }

        // If already stored inside a top-level folder matching a known creator alias (e.g. Mods\LittleMsSam\...)
        if (!string.IsNullOrWhiteSpace(folderName) && folderName != "(Mods 루트)")
        {
            string topSegment = folderName.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            string cleanFolder = Regex.Replace(topSegment, @"^\[([^\]]+)\]$", "$1").Trim();
            string compactFolder = Regex.Replace(cleanFolder, @"[^a-zA-Z0-9가-힣]", "").ToLowerInvariant();
            if (CreatorAliases.TryGetValue(cleanFolder, out string? fldAlias))
                return fldAlias;
            if (!string.IsNullOrEmpty(compactFolder) && CreatorAliases.TryGetValue(compactFolder, out string? compactAlias))
                return compactAlias;
        }

        string fileStem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        string?[] sources =
        [
            fileStem,
            folderName != "(Mods 루트)" ? folderName : null,
            displayName
        ];

        foreach (string? src in sources)
        {
            if (string.IsNullOrWhiteSpace(src))
                continue;

            // 1. [Creator] ModName (skip scale/version/hex/generic tags)
            foreach (Match m in Regex.Matches(src, @"\[([^\[\]]{2,32})\]"))
            {
                string tag = m.Groups[1].Value.Trim();
                if (IsIgnoredBracketOrScaleTag(tag))
                    continue;

                string norm = NormalizeCreatorName(tag);
                if (IsAcceptableCreatorCandidate(norm))
                    return norm;
            }

            // 2. Anim_<Creator>_... / Pose_<Creator>_...
            Match mPackShort = Regex.Match(src, @"^(?:Anim|Pose)[_\-\s]+([A-Za-z0-9]_[A-Za-z0-9.\-]{1,20})(?:[_\-\s]+|$)", RegexOptions.IgnoreCase);
            if (mPackShort.Success)
            {
                string normShort = NormalizeCreatorName(mPackShort.Groups[1].Value.Trim());
                if (IsAcceptableCreatorCandidate(normShort))
                    return normShort;
            }

            Match mPack = Regex.Match(src, @"^(?:Anim|Pose)[_\-\s]+([A-Za-z0-9][A-Za-z0-9.\-]{1,40}?)(?:[_\-\s]+|$)", RegexOptions.IgnoreCase);
            if (mPack.Success)
            {
                string cand = mPack.Groups[1].Value.Trim();
                if (!Regex.IsMatch(cand, @"^(?:anim|anims|animations|custom|sounds|sound|pack|packs|props|prop|objects|obj|override|overrides|default|settings|patch|tuning|test|v\d+.*)$", RegexOptions.IgnoreCase))
                {
                    string norm = NormalizeCreatorName(cand);
                    if (IsAcceptableCreatorCandidate(norm))
                        return norm;
                }
            }

            // 3. -Creator-... (e.g. -CUUPIDON-PosePack)
            Match mDash = Regex.Match(src, @"^\-([A-Za-z0-9][A-Za-z0-9. ]{1,30}?)\-(?:[\s_\-]|$|[A-Za-z])");
            if (mDash.Success)
            {
                string norm = NormalizeCreatorName(mDash.Groups[1].Value);
                if (IsAcceptableCreatorCandidate(norm))
                    return norm;
            }

            // 4. <Creator>-TSR/ts4-... (e.g. SIMc-TSR-..., redheadsims-ts4-...)
            Match mTsr = Regex.Match(src, @"^([A-Za-z0-9][A-Za-z0-9_]{1,30})-(?:ts4|tsr|sims4)-", RegexOptions.IgnoreCase);
            if (mTsr.Success)
            {
                string norm = NormalizeCreatorName(mTsr.Groups[1].Value);
                if (IsAcceptableCreatorCandidate(norm))
                    return norm;
            }

            // 5. ModName by Creator / 150byLorySims / MeshByCreator
            string srcNoVer = Regex.Replace(src, @"(?:[\s_\-]+(?:v|ver|version)\s*[0-9]+(?:\.[0-9]+)*[a-z0-9]*|\s*\(\d+\))\s*$", "", RegexOptions.IgnoreCase).Trim();
            Match mBy = Regex.Match(srcNoVer, @"(?:^|[\s_\-\.])by[\s_\-]+(?!the\b|me\b|you\b|us\b)([A-Za-z0-9가-힣][A-Za-z0-9가-힣_.\-]{1,31})$", RegexOptions.IgnoreCase);
            if (!mBy.Success)
                mBy = Regex.Match(srcNoVer, @"(?<=\d)by[\s_]*([A-Za-z][A-Za-z0-9_.\-]{1,31})$", RegexOptions.IgnoreCase);
            if (!mBy.Success)
                mBy = Regex.Match(srcNoVer, @"(?<=[a-z])by([A-Z][A-Za-z0-9_.\-]{1,31})$");
            if (!mBy.Success)
                mBy = Regex.Match(srcNoVer, @"(?<=[a-z])By([A-Z][A-Za-z0-9_.\-]{1,31})$");
            if (mBy.Success)
            {
                string norm = NormalizeCreatorName(mBy.Groups[1].Value);
                if (IsAcceptableCreatorCandidate(norm))
                    return norm;
            }

            // 6. <Creator> - <Title> (e.g. necrodog - ..., channel4sims - ..., Julie J - ...)
            Match mAuthorDash = Regex.Match(src, @"^([A-Za-z0-9][A-Za-z0-9_.\s]{1,28}?)\s+-\s+(.+)$");
            if (mAuthorDash.Success)
            {
                string cand = mAuthorDash.Groups[1].Value.Trim();
                string[] cWords = cand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (cWords.Length <= 2 &&
                    !Regex.IsMatch(cand, @"^(?:ts4|sims\s*4|part\s*\d+|\d+)$", RegexOptions.IgnoreCase))
                {
                    string norm = NormalizeCreatorName(cand);
                    if (IsAcceptableCreatorCandidate(norm))
                        return norm;
                }
            }
        }

        // 7. Safe <Creator>_... underscore prefix on primary file stem (e.g. AuthorA_Mod_..., StudioB_Object_..., MOTHERLODESIMS_...)
        if (!string.IsNullOrWhiteSpace(fileStem) && fileStem.Contains('_', StringComparison.Ordinal))
        {
            string[] uParts = fileStem.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (uParts.Length >= 2 &&
                uParts[0].Length >= 2 &&
                !Regex.IsMatch(uParts[0], @"^\d+(?:\.\d+)*$"))
            {
                string cand = string.Equals(uParts[0], "NV", StringComparison.OrdinalIgnoreCase) ? "NV Games" : uParts[0];
                string norm = NormalizeCreatorName(cand);
                if (IsAcceptableCreatorCandidate(norm))
                    return norm;
            }
        }

        // 8. Space/hyphen leading token (ignoring skipped [Tag]/generic prefixes like "Objektbeschreibung - ") matching known aliases or batch creators
        if (!string.IsNullOrWhiteSpace(fileStem))
        {
            string stripped = Regex.Replace(fileStem, @"^(?:\[[^\]]+\]\s*)+", string.Empty).Trim();
            stripped = Regex.Replace(stripped, @"^Objektbeschreibung\s*-\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (stripped.StartsWith("Noir and Dark Sims 4", StringComparison.OrdinalIgnoreCase))
                return "Noir and Dark Sims 4";

            string[] words = stripped.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length >= 2)
            {
                string w1 = NormalizeCreatorName(words[0]);
                if (CreatorAliases.TryGetValue(words[0], out string? w1Alias) && IsAcceptableCreatorCandidate(w1Alias))
                    return w1Alias;
                if (knownBatchCreators is not null && knownBatchCreators.Contains(w1) && IsAcceptableCreatorCandidate(w1))
                    return w1;
            }
        }

        return null;
    }

    public static HashSet<string> BuildKnownBatchCreators(IEnumerable<ModUnit> units)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ModUnit unit in units)
        {
            string stem = Path.GetFileNameWithoutExtension(unit.PrimaryFileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(stem))
                continue;

            var unitTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in Regex.Matches(stem, @"\[([^\[\]]{2,32})\]"))
            {
                string t = m.Groups[1].Value.Trim();
                if (!IsIgnoredBracketOrScaleTag(t))
                    unitTokens.Add(NormalizeCreatorName(t));
            }

            Match mWw = Regex.Match(stem, @"^WW[_\-\s]+([A-Za-z0-9][A-Za-z0-9.\-]{1,40}?)(?:[_\-\s]+|$)", RegexOptions.IgnoreCase);
            if (mWw.Success)
                unitTokens.Add(NormalizeCreatorName(mWw.Groups[1].Value));

            string[] tokens = stem.Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length >= 2 && tokens[0].Length >= 3 && !Regex.IsMatch(tokens[0], @"^\d+(?:\.\d+)*$"))
            {
                string norm0 = NormalizeCreatorName(tokens[0]);
                if (IsAcceptableCreatorCandidate(norm0))
                    unitTokens.Add(norm0);
            }

            foreach (string tok in unitTokens)
            {
                if (!IsAcceptableCreatorCandidate(tok))
                    continue;
                counts[tok] = counts.TryGetValue(tok, out int prev) ? prev + 1 : 1;
            }
        }

        return counts
            .Where(kv => kv.Value >= 2)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredBracketOrScaleTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return true;

        string trimmed = tag.Trim();
        if (Regex.IsMatch(trimmed, @"^0x[0-9A-Fa-f]+$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(trimmed, @"^[0-9A-Fa-f]{8,}$") ||
            Regex.IsMatch(trimmed, @"^\d+(?:\.\d+)*(?:\s*[xX]|\s*scaled?.*)?$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(trimmed, @"^(?:scaled|scale|version|wip|update|updated|fixed|fix|merged|hq|nocc|bgc|base\s*game|test|beta|demo|patreon|free|public|ts4|sims\s*4|s4s)$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(trimmed, @"^v?\d+(?:\.\d+)+", RegexOptions.IgnoreCase))
        {
            return true;
        }

        return GenericTagTokens.Contains(trimmed);
    }

    public static string NormalizeCreatorName(string? rawCreator)
    {
        if (string.IsNullOrWhiteSpace(rawCreator))
            return string.Empty;

        string trimmedRaw = rawCreator.Trim();
        if (trimmedRaw.StartsWith('[') && trimmedRaw.IndexOf(']') is int rb && rb > 1)
            trimmedRaw = trimmedRaw[1..rb];
        else if (!trimmedRaw.Contains('[', StringComparison.Ordinal) && trimmedRaw.IndexOf(']') is int rbOrphan && rbOrphan > 1)
            trimmedRaw = trimmedRaw[..rbOrphan];

        string cleaned = trimmedRaw.Trim('[', ']', '(', ')', '-', '_');
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"^by\s+", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (cleaned.Length > 20 && cleaned.Contains('_', StringComparison.Ordinal))
        {
            string firstSeg = cleaned.Split('_')[0];
            if (CreatorAliases.TryGetValue(firstSeg, out string? prefixAlias))
                return prefixAlias;
        }
        if (string.IsNullOrEmpty(cleaned))
            return string.Empty;

        string compactKey = Regex.Replace(cleaned, @"[^a-zA-Z0-9가-힣]", "").ToLowerInvariant();
        if (CreatorAliases.TryGetValue(cleaned, out string? exactAlias))
            return exactAlias;
        if (!string.IsNullOrEmpty(compactKey) && CreatorAliases.TryGetValue(compactKey, out string? compactAlias))
            return compactAlias;

        return cleaned;
    }

    private static bool IsAcceptableCreatorCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 2)
            return false;
        if (GenericTagTokens.Contains(candidate))
            return false;
        if (Regex.IsMatch(candidate, @"^0x[0-9A-Fa-f]+$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(candidate, @"^[0-9A-Fa-f]{8,}$"))
            return false;
        if (Regex.IsMatch(candidate, @"^\d+(?:\.\d+)*(?:\s*[xX]|\s*scaled?.*)?$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(candidate, @"^v(?:er|ersion)?\s*\d+(?:\.\d+)*[a-z0-9]*$", RegexOptions.IgnoreCase))
            return false;
        if (Regex.IsMatch(candidate, @"^\d+$"))
            return false;
        if (IsStagingOrSpecialFolderName(candidate))
            return false;
        if (candidate.Equals(UnknownCreatorLabel, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals(UncategorizedFolderName, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals(OverridesFolderName, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("미분류", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("Resource.cfg", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    public OrganizePlanSummary BuildOrganizePlan(
        string modsFolder,
        IReadOnlyList<ModUnit> units,
        CreatorOrganizeLayout layout,
        bool skipUnknown = true,
        IReadOnlyDictionary<string, ModMetadata>? metadataCache = null,
        IReadOnlyDictionary<string, string>? pinnedUrlCache = null,
        bool deleteIdenticalDuplicates = false)
    {
        if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder))
        {
            return new OrganizePlanSummary
            {
                Layout = layout,
                ModsFolder = modsFolder ?? string.Empty
            };
        }

        string modsFull = Path.GetFullPath(modsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existingFolders = CollectExistingCreatorFolders(modsFull);
        return BuildOrganizePlanCore(
            modsFull,
            sourceRootFull: modsFull,
            units,
            layout,
            skipUnknown,
            deleteIdenticalDuplicates,
            deleteEmptySourceRoot: false,
            metadataCache,
            pinnedUrlCache,
            existingFolders,
            extraDocExts: CompanionSettingsExts,
            includeNonUnitDocsInAllDirs: false);
    }

    /// <summary>
    /// 특정 지정 폴더(예: Mods\test 또는 외부 추가 폴더) 내부의 모드들을 기존 정리된 Mods 폴더 구조에 합쳐 정리하고,
    /// 제작자 불명 파일은 전용 'Unknown' 폴더로 이동한 뒤 비워진 대상 폴더를 삭제할 수 있는 계획을 생성합니다.
    /// </summary>
    public OrganizePlanSummary BuildFolderOrganizePlan(
        string modsFolder,
        string targetSourceFolder,
        CreatorOrganizeLayout layout,
        bool skipUnknown = false,
        bool deleteIdenticalDuplicates = true,
        bool deleteEmptyTargetFolder = true,
        ModMetadataExtractor? metadataExtractor = null,
        ModScannerService? fullModsScanner = null,
        IReadOnlyDictionary<string, string>? pinnedUrlCache = null)
    {
        if (string.IsNullOrWhiteSpace(modsFolder) || !Directory.Exists(modsFolder) ||
            string.IsNullOrWhiteSpace(targetSourceFolder) || !Directory.Exists(targetSourceFolder))
        {
            return new OrganizePlanSummary
            {
                Layout = layout,
                ModsFolder = modsFolder ?? string.Empty,
                SourceRootFolder = targetSourceFolder ?? string.Empty
            };
        }

        string modsFull = Path.GetFullPath(modsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string targetFull = Path.GetFullPath(targetSourceFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (PathsEqual(targetFull, modsFull))
            throw new InvalidOperationException("Mods 루트 폴더 전체는 '폴더 맞추기 (전체 정리)'를 사용해 주세요.");

        string overridesDir = Path.Combine(modsFull, OverridesFolderName);
        if (PathsEqual(targetFull, overridesDir))
            throw new InvalidOperationException("Overrides 폴더는 지정 폴더 정리 대상으로 선택할 수 없습니다.");

        var scanner = fullModsScanner ?? new ModScannerService(modsFull);
        List<ModUnit> targetUnits = scanner.ScanFolderAsUnits(targetFull);

        var extractor = metadataExtractor ?? new ModMetadataExtractor();
        var metaCache = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (ModUnit u in targetUnits)
        {
            if (!string.IsNullOrWhiteSpace(u.PrimaryFilePath) && File.Exists(u.PrimaryFilePath))
                metaCache[u.PrimaryFilePath] = extractor.Extract(u.PrimaryFilePath);
        }

        var existingFolders = CollectExistingCreatorFolders(modsFull, targetFull);
        return BuildOrganizePlanCore(
            modsFull,
            sourceRootFull: targetFull,
            targetUnits,
            layout,
            skipUnknown,
            deleteIdenticalDuplicates,
            deleteEmptySourceRoot: deleteEmptyTargetFolder,
            metaCache,
            pinnedUrlCache,
            existingFolders,
            extraDocExts: FolderOrganizeDocExts,
            includeNonUnitDocsInAllDirs: true);
    }

    private OrganizePlanSummary BuildOrganizePlanCore(
        string modsFull,
        string sourceRootFull,
        IReadOnlyList<ModUnit> units,
        CreatorOrganizeLayout layout,
        bool skipUnknown,
        bool deleteIdenticalDuplicates,
        bool deleteEmptySourceRoot,
        IReadOnlyDictionary<string, ModMetadata>? metadataCache,
        IReadOnlyDictionary<string, string>? pinnedUrlCache,
        IReadOnlyDictionary<string, string> existingFolders,
        HashSet<string> extraDocExts,
        bool includeNonUnitDocsInAllDirs)
    {
        var overrides = LoadOverrideLookup();
        HashSet<string> batchCreators = BuildKnownBatchCreators(units);

        var unitInfos = new List<(ModUnit Unit, CreatorResolutionResult Creator)>(units.Count);
        foreach (ModUnit unit in units)
        {
            ModMetadata? meta = null;
            if (metadataCache is not null && !string.IsNullOrEmpty(unit.PrimaryFilePath))
                metadataCache.TryGetValue(unit.PrimaryFilePath, out meta);

            string? url = null;
            if (pinnedUrlCache is not null)
            {
                pinnedUrlCache.TryGetValue(unit.PrimaryFileName, out url);
                if (string.IsNullOrEmpty(url))
                    pinnedUrlCache.TryGetValue(unit.DisplayName, out url);
            }

            CreatorResolutionResult creator = ResolveCreator(unit, meta, url, overrides, batchCreators, existingFolders);
            unitInfos.Add((unit, creator));
        }

        var dirCreators = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (unit, creator) in unitInfos)
        {
            if (string.IsNullOrWhiteSpace(unit.DirectoryPath) || PathsEqual(unit.DirectoryPath, modsFull))
                continue;

            if (!dirCreators.TryGetValue(unit.DirectoryPath, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                dirCreators[unit.DirectoryPath] = set;
            }
            set.Add(creator.Creator);
        }

        var pendingEntries = new List<(string ModName, string CreatorDisplay, string SourcePath, string TargetPath, bool IsDocFile)>();
        var plannedSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int alreadyOrganized = 0;
        int skippedUnknown = 0;

        foreach (var (unit, creatorInfo) in unitInfos)
        {
            foreach (string srcPath in unit.UnitFiles)
            {
                if (string.IsNullOrWhiteSpace(srcPath) || !File.Exists(srcPath))
                    continue;

                string srcFull = Path.GetFullPath(srcPath);
                if (!IsSubPath(srcFull, sourceRootFull))
                    continue;

                if (IsResourceCfgFile(srcFull))
                    continue;

                bool forcedOverride = IsForcedOverridesFile(srcFull);
                if (!forcedOverride && !creatorInfo.IsKnown && skipUnknown)
                {
                    skippedUnknown++;
                    continue;
                }

                string effectiveCreator;
                string destFolder;
                if (forcedOverride)
                {
                    effectiveCreator = creatorInfo.IsKnown
                        ? $"{creatorInfo.Creator} → {OverridesFolderName}"
                        : OverridesFolderName;
                    destFolder = Path.Combine(modsFull, OverridesFolderName);
                }
                else if (!creatorInfo.IsKnown)
                {
                    effectiveCreator = UncategorizedFolderName;
                    destFolder = Path.Combine(modsFull, UncategorizedFolderName);
                }
                else
                {
                    effectiveCreator = creatorInfo.Creator;
                    string safeCreatorDir = SanitizeFolderSegment(effectiveCreator);
                    string targetCreatorDir = TryResolveExistingCreatorFolder(safeCreatorDir, existingFolders) ?? safeCreatorDir;
                    destFolder = layout switch
                    {
                        CreatorOrganizeLayout.CreatorSingleFolder => Path.Combine(modsFull, targetCreatorDir),
                        CreatorOrganizeLayout.CreatorPrefixedModFolder => Path.Combine(
                            modsFull,
                            BuildPrefixedFolderName(targetCreatorDir, unit)),
                        _ => Path.Combine(modsFull, targetCreatorDir)
                    };
                }

                plannedSourcePaths.Add(srcFull);
                string dstFull = Path.GetFullPath(Path.Combine(destFolder, Path.GetFileName(srcFull)));

                if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyOrganized++;
                    continue;
                }

                pendingEntries.Add((unit.DisplayName, effectiveCreator, srcFull, dstFull, false));
            }
        }

        var dirsToInspectForDocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string d in dirCreators.Keys)
            dirsToInspectForDocs.Add(d);

        if (includeNonUnitDocsInAllDirs && Directory.Exists(sourceRootFull))
        {
            dirsToInspectForDocs.Add(sourceRootFull);
            foreach (string sub in Directory.EnumerateDirectories(sourceRootFull, "*", SearchOption.AllDirectories))
                dirsToInspectForDocs.Add(sub);
        }

        string sourceFolderTag = SanitizeFolderSegment(Path.GetFileName(sourceRootFull));
        foreach (string dirPath in dirsToInspectForDocs.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dirPath) || PathsEqual(dirPath, modsFull))
                continue;

            dirCreators.TryGetValue(dirPath, out var creatorsInDir);
            string? onlyCreator = creatorsInDir is { Count: 1 } ? creatorsInDir.First() : null;
            bool hasSingleKnownCreator = !string.IsNullOrEmpty(onlyCreator) &&
                                         !onlyCreator.Equals(UnknownCreatorLabel, StringComparison.OrdinalIgnoreCase);

            if (!includeNonUnitDocsInAllDirs && (!hasSingleKnownCreator && skipUnknown))
                continue;
            if (!includeNonUnitDocsInAllDirs && !hasSingleKnownCreator && onlyCreator is null)
                continue;
            if (!includeNonUnitDocsInAllDirs && creatorsInDir is { Count: > 1 })
                continue;

            var repUnit = unitInfos.FirstOrDefault(x => string.Equals(x.Unit.DirectoryPath, dirPath, StringComparison.OrdinalIgnoreCase)).Unit;
            string effectiveCreator = hasSingleKnownCreator ? onlyCreator! : UncategorizedFolderName;
            string destFolder;
            if (!hasSingleKnownCreator)
            {
                destFolder = Path.Combine(modsFull, UncategorizedFolderName);
            }
            else
            {
                string safeCreatorDir = SanitizeFolderSegment(effectiveCreator);
                string targetCreatorDir = TryResolveExistingCreatorFolder(safeCreatorDir, existingFolders) ?? safeCreatorDir;
                destFolder = (layout == CreatorOrganizeLayout.CreatorSingleFolder || repUnit is null)
                    ? Path.Combine(modsFull, targetCreatorDir)
                    : Path.Combine(modsFull, BuildPrefixedFolderName(targetCreatorDir, repUnit));
            }

            foreach (string extraFile in Directory.EnumerateFiles(dirPath))
            {
                string fn = Path.GetFileName(extraFile);
                string srcFull = Path.GetFullPath(extraFile);
                if (plannedSourcePaths.Contains(srcFull))
                    continue;

                if (IsResourceCfgFile(fn))
                {
                    if (includeNonUnitDocsInAllDirs && !PathsEqual(dirPath, modsFull))
                    {
                        string rootCfg = Path.GetFullPath(Path.Combine(modsFull, "Resource.cfg"));
                        if (!File.Exists(rootCfg))
                        {
                            plannedSourcePaths.Add(srcFull);
                            pendingEntries.Add(("Resource.cfg (Mods 루트 복원)", "Mods 루트", srcFull, rootCfg, true));
                        }
                        else if (deleteIdenticalDuplicates && AreFilesIdentical(srcFull, rootCfg))
                        {
                            plannedSourcePaths.Add(srcFull);
                            pendingEntries.Add(("Resource.cfg (중복 사본)", "Mods 루트 유지", srcFull, rootCfg, true));
                        }
                    }
                    continue;
                }

                if (!extraDocExts.Contains(Path.GetExtension(fn)))
                    continue;

                plannedSourcePaths.Add(srcFull);
                string dstFull = Path.GetFullPath(Path.Combine(destFolder, fn));
                if (string.Equals(srcFull, dstFull, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (includeNonUnitDocsInAllDirs && File.Exists(dstFull) && !AreFilesIdentical(srcFull, dstFull))
                {
                    string stem = Path.GetFileNameWithoutExtension(fn);
                    string ext = Path.GetExtension(fn);
                    int idx = 1;
                    string alt = Path.Combine(destFolder, $"{stem} ({sourceFolderTag}){ext}");
                    while (File.Exists(alt))
                    {
                        idx++;
                        alt = Path.Combine(destFolder, $"{stem} ({sourceFolderTag}_{idx}){ext}");
                    }
                    dstFull = Path.GetFullPath(alt);
                }

                string modTitle = repUnit is not null ? $"{repUnit.DisplayName} (설정/문서)" : $"{Path.GetFileName(dirPath)} (문서)";
                pendingEntries.Add((modTitle, effectiveCreator, srcFull, dstFull, true));
            }
        }

        var planItems = new List<OrganizeMovePlanItem>(pendingEntries.Count);
        var claimedTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var batchTargetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pendingEntries)
            batchTargetCounts[entry.TargetPath] = batchTargetCounts.GetValueOrDefault(entry.TargetPath) + 1;

        int conflictCount = 0;
        int duplicateCleanCount = 0;
        int depthWarningCount = 0;

        foreach (var mv in pendingEntries)
        {
            bool targetExistsOutsideMoves = File.Exists(mv.TargetPath) && !plannedSourcePaths.Contains(mv.TargetPath);
            bool alreadyClaimedInBatch = claimedTargets.TryGetValue(mv.TargetPath, out string? claimedSource);
            bool hasConflict = false;
            bool isIdenticalDup = false;
            bool deleteDupSource = false;
            string status;

            if (targetExistsOutsideMoves)
            {
                if (AreFilesIdentical(mv.SourcePath, mv.TargetPath))
                {
                    isIdenticalDup = true;
                    if (deleteIdenticalDuplicates)
                    {
                        deleteDupSource = true;
                        duplicateCleanCount++;
                        status = "중복 동일본 정리 (대상 폴더에 동일 파일 존재 → 원본 백업 후 삭제)";
                    }
                    else
                    {
                        hasConflict = true;
                        conflictCount++;
                        status = "충돌: 대상 경로에 동일 파일 존재 (건너뜀)";
                    }
                }
                else
                {
                    hasConflict = true;
                    conflictCount++;
                    status = mv.IsDocFile
                        ? "충돌: 대상 문서가 이미 존재함 (건너뜀)"
                        : "충돌: 대상 경로에 동일 파일명 존재 (내용 다름 · 건너뜀)";
                }
            }
            else if (alreadyClaimedInBatch && claimedSource is not null)
            {
                if (AreFilesIdentical(mv.SourcePath, claimedSource) && deleteIdenticalDuplicates)
                {
                    isIdenticalDup = true;
                    deleteDupSource = true;
                    duplicateCleanCount++;
                    status = "배치 내 중복 동일본 정리 (원본 백업 후 삭제)";
                }
                else
                {
                    hasConflict = true;
                    conflictCount++;
                    status = "충돌: 배치 내 대상 파일명 중복 (건너뜀)";
                }
            }
            else if (batchTargetCounts.GetValueOrDefault(mv.TargetPath) > 1 && !deleteIdenticalDuplicates)
            {
                hasConflict = true;
                conflictCount++;
                status = "충돌: 대상 파일명 중복 (건너뜀)";
            }
            else
            {
                bool hasDifferentSiblingInBatch = pendingEntries.Any(other =>
                    !string.Equals(other.SourcePath, mv.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(other.TargetPath, mv.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                    !AreFilesIdentical(mv.SourcePath, other.SourcePath));
                if (hasDifferentSiblingInBatch)
                {
                    hasConflict = true;
                    conflictCount++;
                    status = "충돌: 대상 파일명 중복 (내용 다름 · 건너뜀)";
                }
                else
                {
                    claimedTargets[mv.TargetPath] = mv.SourcePath;
                    status = "이동 가능";
                }
            }

            string relSrc = IsSubPath(mv.SourcePath, modsFull)
                ? Path.GetRelativePath(modsFull, mv.SourcePath)
                : $"{Path.GetFileName(sourceRootFull)}\\{Path.GetRelativePath(sourceRootFull, mv.SourcePath)}";
            string relDst = Path.GetRelativePath(modsFull, mv.TargetPath);
            string[] dstParts = relDst.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

            bool depthRisk = !isIdenticalDup &&
                             mv.TargetPath.EndsWith(".ts4script", StringComparison.OrdinalIgnoreCase) &&
                             dstParts.Length > 2;
            if (depthRisk)
            {
                depthWarningCount++;
                if (!hasConflict)
                    status = "주의: 스크립트 2단계 이상 깊이";
            }

            planItems.Add(new OrganizeMovePlanItem
            {
                ModName = mv.ModName,
                Creator = mv.CreatorDisplay,
                SourceFullPath = mv.SourcePath,
                TargetFullPath = mv.TargetPath,
                SourceRelativePath = relSrc,
                TargetRelativePath = relDst,
                HasConflict = hasConflict,
                HasScriptDepthRisk = depthRisk,
                IsIdenticalDuplicate = isIdenticalDup,
                DeleteDuplicateSource = deleteDupSource,
                StatusNote = status
            });
        }

        return new OrganizePlanSummary
        {
            Layout = layout,
            ModsFolder = modsFull,
            SourceRootFolder = sourceRootFull,
            DeleteEmptySourceRoot = deleteEmptySourceRoot,
            DeleteIdenticalDuplicates = deleteIdenticalDuplicates,
            TotalMoves = planItems.Count(p => !p.HasConflict && !p.IsIdenticalDuplicate),
            DuplicateCleanCount = duplicateCleanCount,
            AlreadyOrganizedCount = alreadyOrganized,
            SkippedUnknownCount = skippedUnknown,
            ConflictCount = conflictCount,
            DepthWarningCount = depthWarningCount,
            Items = planItems
        };
    }

    public OrganizeExecutionResult ExecuteOrganizePlan(
        OrganizePlanSummary plan,
        BackupService? backupService = null,
        bool createBackup = true)
    {
        ArgumentNullException.ThrowIfNull(plan);

        string modsFull = Path.GetFullPath(plan.ModsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string sourceRootFull = string.IsNullOrWhiteSpace(plan.SourceRootFolder)
            ? modsFull
            : Path.GetFullPath(plan.SourceRootFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var movable = plan.Items.Where(i => !i.HasConflict && !i.HasScriptDepthRisk && !i.IsIdenticalDuplicate).ToList();
        var deletableDups = plan.Items.Where(i => i.IsIdenticalDuplicate && i.DeleteDuplicateSource && !i.HasConflict).ToList();
        var warnings = new List<string>();
        int skippedConflicts = plan.Items.Count(i => i.HasConflict || i.HasScriptDepthRisk || (i.IsIdenticalDuplicate && !i.DeleteDuplicateSource));

        string? backupSessionDir = null;
        if ((movable.Count > 0 || deletableDups.Count > 0) && createBackup && backupService is not null)
        {
            backupSessionDir = backupService.CreateBackupSession(
                PathsEqual(sourceRootFull, modsFull) ? "organize_creator" : "organize_folder");
            foreach (OrganizeMovePlanItem item in movable.Concat(deletableDups))
            {
                if (File.Exists(item.SourceFullPath))
                    backupService.BackupItem(item.SourceFullPath, backupSessionDir);
            }
        }

        int movedCount = 0;
        int deletedDuplicateCount = 0;
        var touchedSourceDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string rootResourceCfg = Path.GetFullPath(Path.Combine(modsFull, "Resource.cfg"));

        foreach (OrganizeMovePlanItem item in movable)
        {
            if (!File.Exists(item.SourceFullPath))
                continue;
            if (!IsSubPath(item.SourceFullPath, sourceRootFull) || !IsSubPath(item.TargetFullPath, modsFull))
                continue;
            if (string.Equals(Path.GetFullPath(item.SourceFullPath), rootResourceCfg, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                string? srcParent = Path.GetDirectoryName(item.SourceFullPath);
                if (!string.IsNullOrEmpty(srcParent))
                    touchedSourceDirs.Add(srcParent);

                string? dstParent = Path.GetDirectoryName(item.TargetFullPath);
                if (!string.IsNullOrEmpty(dstParent))
                    Directory.CreateDirectory(dstParent);

                if (File.Exists(item.TargetFullPath))
                {
                    warnings.Add($"대상 파일이 이미 존재하여 건너뜀: {item.TargetRelativePath}");
                    skippedConflicts++;
                    continue;
                }

                File.Move(item.SourceFullPath, item.TargetFullPath);
                movedCount++;
            }
            catch (Exception ex)
            {
                warnings.Add($"이동 실패 ({item.SourceRelativePath}): {ex.Message}");
            }
        }

        foreach (OrganizeMovePlanItem dup in deletableDups)
        {
            if (!File.Exists(dup.SourceFullPath))
                continue;
            if (!IsSubPath(dup.SourceFullPath, sourceRootFull))
                continue;
            if (string.Equals(Path.GetFullPath(dup.SourceFullPath), rootResourceCfg, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!File.Exists(dup.TargetFullPath) || !AreFilesIdentical(dup.SourceFullPath, dup.TargetFullPath))
            {
                warnings.Add($"중복 정리 보류 (대상이 존재하지 않거나 내용이 다름): {dup.SourceRelativePath}");
                skippedConflicts++;
                continue;
            }

            try
            {
                string? srcParent = Path.GetDirectoryName(dup.SourceFullPath);
                if (!string.IsNullOrEmpty(srcParent))
                    touchedSourceDirs.Add(srcParent);

                File.Delete(dup.SourceFullPath);
                deletedDuplicateCount++;
            }
            catch (Exception ex)
            {
                warnings.Add($"중복 파일 삭제 실패 ({dup.SourceRelativePath}): {ex.Message}");
            }
        }

        int cleanedDirs = CleanupEmptyDirectories(modsFull, touchedSourceDirs);
        if (!PathsEqual(sourceRootFull, modsFull))
        {
            cleanedDirs += CleanupEmptyDirectories(sourceRootFull, touchedSourceDirs);
            if (plan.DeleteEmptySourceRoot && Directory.Exists(sourceRootFull))
            {
                string overridesDir = Path.Combine(modsFull, OverridesFolderName);
                string unknownDir = Path.Combine(modsFull, UncategorizedFolderName);
                if (!PathsEqual(sourceRootFull, overridesDir) &&
                    !PathsEqual(sourceRootFull, unknownDir) &&
                    !Directory.EnumerateFiles(sourceRootFull, "*", SearchOption.AllDirectories).Any())
                {
                    try
                    {
                        Directory.Delete(sourceRootFull, recursive: true);
                        cleanedDirs++;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"빈 대상 폴더 삭제 실패 ({Path.GetFileName(sourceRootFull)}): {ex.Message}");
                    }
                }
            }
        }

        return new OrganizeExecutionResult
        {
            MovedFileCount = movedCount,
            DeletedDuplicateCount = deletedDuplicateCount,
            SkippedConflictCount = skippedConflicts,
            CleanedEmptyFolderCount = cleanedDirs,
            BackupSessionDir = backupSessionDir,
            Warnings = warnings
        };
    }

    public static bool AreFilesIdentical(string pathA, string pathB)
    {
        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            return false;
        if (!File.Exists(pathA) || !File.Exists(pathB))
            return false;
        if (PathsEqual(pathA, pathB))
            return true;

        try
        {
            var infoA = new FileInfo(pathA);
            var infoB = new FileInfo(pathB);
            if (infoA.Length != infoB.Length)
                return false;

            byte[] hashA = ComputeFileSha256(pathA);
            byte[] hashB = ComputeFileSha256(pathB);
            return CryptographicOperations.FixedTimeEquals(hashA, hashB);
        }
        catch
        {
            return false;
        }
    }

    public static byte[] ComputeFileSha256(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(fs);
    }

    private static string BuildPrefixedFolderName(string safeCreatorDir, ModUnit unit)
    {
        string baseName;
        if (!unit.IsStandalone && !string.IsNullOrWhiteSpace(unit.InstallFolder) && unit.InstallFolder != "(Mods 루트)")
        {
            string topSegment = unit.InstallFolder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)[0];
            baseName = topSegment;
        }
        else if (!unit.IsStandalone && !string.IsNullOrWhiteSpace(unit.FolderName) && unit.FolderName != "(Mods 루트)")
        {
            string topSegment = unit.FolderName.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)[0];
            baseName = topSegment;
        }
        else
        {
            baseName = Path.GetFileNameWithoutExtension(unit.PrimaryFileName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = unit.DisplayName;
        }

        // Remove any previous "[Creator] " prefix matching creator to stay idempotent
        baseName = Regex.Replace(baseName, @"^\[[^\]]+\]\s*", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseName) || baseName.Equals(safeCreatorDir, StringComparison.OrdinalIgnoreCase))
        {
            string fileStem = Regex.Replace(
                Path.GetFileNameWithoutExtension(unit.PrimaryFileName) ?? string.Empty,
                @"^(?:\[[^\]]+\]\s*)+",
                string.Empty).Trim();
            baseName = string.IsNullOrWhiteSpace(fileStem) ? unit.DisplayName : fileStem;
        }

        if (baseName.Equals(safeCreatorDir, StringComparison.OrdinalIgnoreCase))
            return safeCreatorDir;

        string safeModDir = SanitizeFolderSegment(baseName);
        if (string.IsNullOrWhiteSpace(safeModDir))
            return safeCreatorDir;

        return SanitizeFolderSegment($"[{safeCreatorDir}] {safeModDir}");
    }

    private static int CleanupEmptyDirectories(string modsRootFull, IEnumerable<string> candidateDirs)
    {
        int removed = 0;
        var ordered = candidateDirs
            .Select(Path.GetFullPath)
            .OrderByDescending(p => p.Length)
            .ToList();

        foreach (string dir in ordered)
        {
            string? current = dir;
            while (!string.IsNullOrEmpty(current) &&
                   IsSubPath(current, modsRootFull) &&
                   !PathsEqual(current, modsRootFull))
            {
                try
                {
                    if (Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
                    {
                        Directory.Delete(current);
                        removed++;
                        current = Path.GetDirectoryName(current);
                        continue;
                    }
                }
                catch
                {
                    // Lock or permission -> stop walking upward
                }
                break;
            }
        }

        return removed;
    }

    public static string SanitizeFolderSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown";

        string cleaned = Regex.Replace(raw.Trim(), @"[<>:""/\\|?*]+", "_");
        cleaned = cleaned.Trim(' ', '.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    private static bool IsSubPath(string candidateFull, string rootFull)
    {
        string normalizedRoot = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedCand = Path.GetFullPath(candidateFull);
        return normalizedCand.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b)
    {
        string fa = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
    }
}
