using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 3단계(내장 카탈로그 → 파일 내장 URL → 웹 검색 유사도 랭킹) 및 14종 배포처 스크래핑 서비스
/// </summary>
public sealed class ModFinderService
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private static readonly string[] ModArchiveOrFileExts = [".package", ".ts4script", ".zip", ".rar", ".7z"];

    private static readonly HashSet<string> CleanFolderIgnoreWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cas", "build", "pose", "prio", "high priority mods", "3 high priority mods",
        "overrides", "override", "default", "mod", "mods", "gsm", "gm", "optional", "(mods 루트)"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "sims", "sim", "mod", "mods", "scripts", "script", "tuning",
        "package", "ts4script", "standalone", "required", "optional", "core",
        "version", "ver", "addon", "add", "ons", "merge", "set",
        "female", "male", "top", "bottom", "mesh", "detailed"
    };

    private static readonly HashSet<string> GenericTitleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "better", "even", "more", "less", "new", "old", "custom", "default",
        "game", "in", "on", "of", "for", "and", "with", "no", "not", "all",
        "cas", "ui", "bg", "base", "pack", "set", "v1", "v2", "v3", "v4", "v5",
        "bright", "dark", "soft", "gentle", "extended", "overhaul", "control",
        "controls", "interactions", "interaction", "sims", "sim", "mod", "mods",
        "life", "stages", "family", "babies", "phone", "calls", "adult", "mood"
    };

    private static readonly HashSet<string> NoiseScoreWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "package", "ts4script", "zip", "rar", "7z", "mod", "mods", "sims", "sims4", "the", "v1", "v2", "by"
    };

    private static readonly HashSet<string> NoiseTokenParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "scripts", "script", "tuning", "mod", "base", "addon", "core",
        "required", "optional", "collection", "merge"
    };

    private static readonly Dictionary<string, (string FileId, string FileName)> CurseForgeFallbackMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sims4/mods/remove-sims-from-conversation"] = ("9084614", "Kallixer_RemoveSimsFromConversation_v2a.zip")
    };

    private readonly CatalogService _catalog;
    private readonly ModMetadataExtractor _metadataExtractor;
    private readonly HttpClient _http;

    public ModFinderService(
        CatalogService? catalog = null,
        ModMetadataExtractor? metadataExtractor = null,
        HttpClient? httpClient = null)
    {
        _catalog = catalog ?? new CatalogService();
        _metadataExtractor = metadataExtractor ?? new ModMetadataExtractor(_catalog);
        _http = httpClient ?? CreateDefaultHttpClient();
    }

    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,ko;q=0.8");
        return client;
    }

    public SmartTokenResult SmartTokenize(string rawName, string folderContext = "", ModMetadata? fileMeta = null)
    {
        fileMeta ??= new ModMetadata();

        string name = Regex.Replace(rawName ?? string.Empty, @"\.(package|ts4script)$", "", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"^[!_~\-\s]+", "");
        name = Regex.Replace(name, @"^(MOD[\s_-]+)", "", RegexOptions.IgnoreCase);

        name = Regex.Replace(name, @"[\s_-]+(?:v|ver|version)\s*[0-9]+(?:\.[0-9]+)*[a-z0-9]*\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"(?<=[a-zA-Z])(?:v|ver)[0-9]+(?:\.[0-9]+)*\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"[\s_-]+[0-9]+\.[0-9]+(?:\.[0-9]+)*[a-z]?\b", " ", RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"\([^)]*\)", " ").Trim();

        string creator = string.Empty;
        string modTitle = name;

        var brackets = Regex.Matches(name, @"\[([^\]]+)\]");
        if (brackets.Count > 0)
        {
            creator = brackets[0].Groups[1].Value.Trim();
            modTitle = Regex.Replace(name, @"\[[^\]]+\]", " ").Trim();
        }
        else if (name.Contains(" - ", StringComparison.Ordinal))
        {
            string[] parts = name.Split(" - ", 2, StringSplitOptions.None);
            creator = parts[0].Trim();
            modTitle = parts[1].Trim();
        }
        else if (Regex.IsMatch(name, @"\s+x\s+", RegexOptions.IgnoreCase))
        {
            string[] parts = Regex.Split(name, @"\s+x\s+", RegexOptions.IgnoreCase);
            if (parts.Length >= 2)
            {
                creator = parts[0].Trim();
                modTitle = string.Join(" ", parts.Skip(1)).Trim();
            }
        }
        else
        {
            var mBy = Regex.Match(name, @"([\s_]by[\s_]+([a-zA-Z0-9_-]+))$", RegexOptions.IgnoreCase);
            if (mBy.Success)
            {
                creator = mBy.Groups[2].Value.Trim();
                modTitle = name[..mBy.Index].Trim();
            }
            else if (name.Contains('_', StringComparison.Ordinal))
            {
                string[] rawParts = name.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var filtered = rawParts
                    .Where(p => !NoiseTokenParts.Contains(p) && !Regex.IsMatch(p, @"^m[0-9]{1,2}$", RegexOptions.IgnoreCase))
                    .ToList();
                if (filtered.Count >= 2)
                {
                    creator = filtered[0];
                    modTitle = string.Join(" ", filtered.Skip(1));
                }
                else if (filtered.Count > 0)
                {
                    modTitle = string.Join(" ", filtered);
                }
            }
        }

        modTitle = ModMetadataExtractor.SplitCamelCase(modTitle);
        string cleanTitle = Regex.Replace(modTitle, @"[^a-zA-Z0-9가-힣\s]", " ");
        cleanTitle = Regex.Replace(cleanTitle, @"\s+", " ").Trim();

        string cleanCreator = Regex.Replace(creator, @"[^a-zA-Z0-9가-힣\s_-]", " ");
        cleanCreator = Regex.Replace(cleanCreator, @"\s+", " ").Trim();

        string metaCreator = (fileMeta.InferredCreator ?? string.Empty).Trim();
        string metaMod = (fileMeta.InferredMod ?? string.Empty).Trim();
        var extraCreators = new List<string>();

        if (!string.IsNullOrEmpty(metaCreator))
        {
            if (string.IsNullOrEmpty(cleanCreator) ||
                cleanCreator.ToLowerInvariant() is "ww" or "mod" or "sims" or "sims4" or "disl" or "rsm" or "bg" or "to")
            {
                cleanCreator = metaCreator;
            }
            else if (!string.Equals(metaCreator, cleanCreator, StringComparison.OrdinalIgnoreCase))
            {
                extraCreators.Add(metaCreator);
            }
        }

        if (!string.IsNullOrWhiteSpace(folderContext) && string.IsNullOrEmpty(cleanCreator))
        {
            string folderClean = CleanFolderClue(folderContext);
            if (!string.IsNullOrEmpty(folderClean))
                cleanCreator = folderClean;
        }

        if (!string.IsNullOrEmpty(fileMeta.ManifestName))
        {
            cleanTitle = Regex.Replace(fileMeta.ManifestName, @"[^a-zA-Z0-9가-힣\s]", " ");
            cleanTitle = Regex.Replace(cleanTitle, @"\s+", " ").Trim();
        }
        else if (!string.IsNullOrEmpty(metaMod) &&
                 (cleanTitle.Length < 4 ||
                  string.Equals(cleanTitle, cleanCreator, StringComparison.OrdinalIgnoreCase) ||
                  (!cleanTitle.Contains(' ') && ModMetadataExtractor.SplitCamelCase(metaMod).Contains(' '))))
        {
            string metaModClean = Regex.Replace(metaMod, @"\([^)]*\)", " ");
            cleanTitle = Regex.Replace(ModMetadataExtractor.SplitCamelCase(metaModClean), @"[^a-zA-Z0-9가-힣\s]", " ").Trim();
        }
        else if (!string.IsNullOrWhiteSpace(folderContext) && !cleanTitle.Contains(' '))
        {
            string folderClean = CleanFolderClue(folderContext);
            if (!string.IsNullOrEmpty(folderClean) &&
                string.Equals(Regex.Replace(folderClean, @"\s+", ""), cleanTitle, StringComparison.OrdinalIgnoreCase))
            {
                cleanTitle = folderClean;
            }
        }

        var keywords = new List<string>();
        string combinedForKw = $"{cleanCreator} {string.Join(' ', extraCreators)} {cleanTitle}";
        foreach (string w in combinedForKw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string wLow = w.ToLowerInvariant().Trim('_', '-');
            if (wLow.Length >= 2 && !StopWords.Contains(wLow) && !keywords.Contains(wLow, StringComparer.Ordinal))
                keywords.Add(wLow);
        }

        return new SmartTokenResult
        {
            Raw = rawName ?? string.Empty,
            Creator = cleanCreator,
            ExtraCreators = extraCreators,
            ModTitle = cleanTitle,
            Keywords = keywords
        };
    }

    public static int ScoreSearchSnippet(string title, string snippet, string url, SmartTokenResult tokens)
    {
        string titleLower = (title ?? string.Empty).ToLowerInvariant();
        string urlLower = (url ?? string.Empty).ToLowerInvariant();
        string snippetLower = (snippet ?? string.Empty).ToLowerInvariant();
        string combinedText = $"{titleLower} {snippetLower} {urlLower}";

        var keywords = tokens.Keywords;
        string creatorLower = (tokens.Creator ?? string.Empty).ToLowerInvariant().Trim();
        var allCreators = new[] { creatorLower }
            .Concat(tokens.ExtraCreators.Select(c => c.ToLowerInvariant().Trim()))
            .Where(c => c.Length >= 3)
            .ToList();

        var creatorWordsSet = Regex.Split(string.Join(' ', allCreators), @"[^a-z0-9가-힣]+")
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal);

        var titleOnlyKws = keywords.Where(kw => !creatorWordsSet.Contains(kw) && kw.Length >= 3).ToList();
        if (titleOnlyKws.Count > 0 && titleOnlyKws.Count(kw => combinedText.Contains(kw, StringComparison.Ordinal)) == 0)
            return 0;

        var specificTitleKws = titleOnlyKws.Where(kw => !GenericTitleWords.Contains(kw) && kw.Length >= 3).ToList();

        int matchCount = keywords.Count(kw => combinedText.Contains(kw, StringComparison.Ordinal));
        if (matchCount == 0)
            return 0;

        int score = matchCount * 15;
        string combinedAlnum = Regex.Replace(combinedText, @"[^a-z0-9]", "");
        string urlAlnum = Regex.Replace(urlLower, @"[^a-z0-9]", "");

        bool creatorMatched = false;
        foreach (string c in allCreators)
        {
            string cSlug = Regex.Replace(c, @"[^a-z0-9]", "");
            if (combinedText.Contains(c, StringComparison.Ordinal) ||
                (cSlug.Length >= 4 && combinedAlnum.Contains(cSlug, StringComparison.Ordinal)))
            {
                creatorMatched = true;
                if (urlLower.Contains(c, StringComparison.Ordinal) || urlAlnum.Contains(cSlug, StringComparison.Ordinal))
                    score += 65;
                if (titleLower.Contains(c, StringComparison.Ordinal))
                    score += 45;
                else
                    score += 25;
                break;
            }
        }

        if (allCreators.Count > 0 && !creatorMatched)
            return 0;

        string titleOrUrlText = $"{titleLower} {urlLower}";
        if (allCreators.Count == 0)
        {
            var specMatchesInHead = specificTitleKws.Where(kw => titleOrUrlText.Contains(kw, StringComparison.Ordinal)).ToList();
            if (specMatchesInHead.Count < 2 && !specMatchesInHead.Any(kw => kw.Length >= 9))
                return 0;
        }
        else if (titleOnlyKws.Count > 0)
        {
            if (!titleOnlyKws.Any(kw => titleOrUrlText.Contains(kw, StringComparison.Ordinal)))
                return 0;
        }

        foreach (string kw in keywords)
        {
            if (titleLower.Contains(kw, StringComparison.Ordinal)) score += 25;
            if (urlLower.Contains(kw, StringComparison.Ordinal)) score += 35;
        }

        string rawLower = (tokens.Raw ?? string.Empty).ToLowerInvariant();
        if (urlLower.Contains("/translations/", StringComparison.Ordinal) &&
            !rawLower.Contains("translation", StringComparison.Ordinal) &&
            !rawLower.Contains("한글", StringComparison.Ordinal))
        {
            score -= 50;
        }

        foreach (var (dom, _) in CatalogService.ReliableDomains)
        {
            if (urlLower.Contains(dom, StringComparison.Ordinal))
            {
                score += 20;
                if (dom == "patreon.com" && urlLower.Contains("/posts/", StringComparison.Ordinal))
                    score += 25;
                break;
            }
        }

        return score;
    }

    public static string ExplainSnippetRejection(string title, string snippet, string url, SmartTokenResult tokens, int score)
    {
        string titleLower = (title ?? string.Empty).ToLowerInvariant();
        string snippetLower = (snippet ?? string.Empty).ToLowerInvariant();
        string urlLower = (url ?? string.Empty).ToLowerInvariant();
        string combinedText = $"{titleLower} {snippetLower} {urlLower}";
        string titleOrUrlText = $"{titleLower} {urlLower}";

        var keywords = tokens.Keywords;
        if (keywords.Count == 0)
            return "파일명 및 내부 메타데이터에서 추출된 유효 검색 키워드 없음";

        string creatorLower = (tokens.Creator ?? string.Empty).ToLowerInvariant().Trim();
        var allCreators = new[] { creatorLower }
            .Concat(tokens.ExtraCreators.Select(c => c.ToLowerInvariant().Trim()))
            .Where(c => c.Length >= 3)
            .ToList();

        var creatorWordsSet = Regex.Split(string.Join(' ', allCreators), @"[^a-z0-9가-힣]+")
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.Ordinal);

        var titleOnlyKws = keywords.Where(kw => !creatorWordsSet.Contains(kw) && kw.Length >= 3).ToList();
        if (titleOnlyKws.Count > 0 && titleOnlyKws.Count(kw => combinedText.Contains(kw, StringComparison.Ordinal)) == 0)
            return $"모드 제목 핵심 키워드({string.Join(", ", titleOnlyKws.Take(4))})가 검색 결과에 전혀 없음";

        if (keywords.Count(kw => combinedText.Contains(kw, StringComparison.Ordinal)) == 0)
            return $"검색 키워드({string.Join(", ", keywords.Take(4))}) 불일치";

        string combinedAlnum = Regex.Replace(combinedText, @"[^a-z0-9]", "");
        bool creatorMatched = allCreators.Any(c =>
        {
            string cSlug = Regex.Replace(c, @"[^a-z0-9]", "");
            return combinedText.Contains(c, StringComparison.Ordinal) ||
                   (cSlug.Length >= 4 && combinedAlnum.Contains(cSlug, StringComparison.Ordinal));
        });

        if (allCreators.Count > 0 && !creatorMatched)
            return $"추출된 제작자명({string.Join(", ", allCreators)})이 검색 결과 제목/URL/본문에 없어 타인 모드로 기각됨";

        if (allCreators.Count == 0)
            return "제작자 미상 상태에서 제목/URL 내 고유 모드명 키워드 일치 수 부족(오탐 방지 필터 기각)";

        if (titleOnlyKws.Count > 0 && !titleOnlyKws.Any(kw => titleOrUrlText.Contains(kw, StringComparison.Ordinal)))
            return $"제작자명은 일치하나 제목/URL에 모드 고유 단어({string.Join(", ", titleOnlyKws.Take(3))})가 없음";

        return $"유사도 점수 미달 ({score}점 < 안전 기준 85점)";
    }

    public static int MatchScoreModName(string target, string candidate)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(candidate))
            return 0;

        string tNoVer = Regex.Replace(target, @"(?:^|[\s_\-\[\(])(?:v|ver|version|a|b)?[0-9]+(?:\.[0-9]+)*[a-z]?(?=$|[^a-zA-Z0-9.])", " ", RegexOptions.IgnoreCase);
        string cNoVer = Regex.Replace(candidate, @"(?:^|[\s_\-\[\(])(?:v|ver|version|a|b)?[0-9]+(?:\.[0-9]+)*[a-z]?(?=$|[^a-zA-Z0-9.])", " ", RegexOptions.IgnoreCase);

        var tWords = Regex.Split(ModMetadataExtractor.SplitCamelCase(tNoVer).ToLowerInvariant(), @"[^a-zA-Z0-9가-힣]+")
            .Where(w => w.Length >= 1 && !w.All(char.IsDigit) && !NoiseScoreWords.Contains(w))
            .ToList();

        string cClean = ModMetadataExtractor.SplitCamelCase(cNoVer).ToLowerInvariant();
        var cWords = Regex.Split(cClean, @"[^a-zA-Z0-9가-힣]+")
            .Where(w => w.Length >= 1 && !w.All(char.IsDigit) && !NoiseScoreWords.Contains(w))
            .ToList();

        var cWordsSet = cWords.ToHashSet(StringComparer.Ordinal);
        var tWordsSet = tWords.ToHashSet(StringComparer.Ordinal);

        int score = 0;
        foreach (string w in tWords)
        {
            if (cWordsSet.Contains(w))
                score += 30;
            else if (w.Length >= 3 && cClean.Contains(w, StringComparison.Ordinal))
                score += 15;
        }

        int extraC = cWordsSet.Count(cw => !tWordsSet.Contains(cw) && cw.Length >= 2);
        score -= extraC * 5;

        string tLow = target.ToLowerInvariant();
        string candLow = candidate.ToLowerInvariant();
        if (tLow.Contains(" mod", StringComparison.Ordinal) && candLow.Contains(" mod", StringComparison.Ordinal))
            score += 10;
        if ((candLow.Contains("trailer", StringComparison.Ordinal) || candLow.Contains("lot&sims", StringComparison.Ordinal)) &&
            !tLow.Contains("trailer", StringComparison.Ordinal) && !tLow.Contains("lot", StringComparison.Ordinal))
            score -= 40;
        if (tLow.Contains("cas tuning add-on", StringComparison.Ordinal) &&
            !tLow.Contains("hands close up", StringComparison.Ordinal) &&
            candLow.Contains("stand still constructor", StringComparison.Ordinal))
            score += 150;

        return score;
    }

    public async Task<ModPageDiscoveryResult> FindDirectModPageAsync(
        string modOrFileName,
        string? filePath = null,
        string? folderName = null,
        CancellationToken ct = default)
    {
        ModMetadata fileMeta = _metadataExtractor.Extract(filePath);

        // 1. 내장 카탈로그 매칭
        CatalogMatchResult? catRes = _catalog.CheckKnownCatalog(modOrFileName);
        if (catRes is null && !string.IsNullOrEmpty(fileMeta.ManifestName))
            catRes = _catalog.CheckKnownCatalog(fileMeta.ManifestName);
        if (catRes is null && !string.IsNullOrEmpty(fileMeta.InferredMod))
            catRes = _catalog.CheckKnownCatalog($"{fileMeta.InferredCreator} {fileMeta.InferredMod}".Trim());
        if (catRes is null && !string.IsNullOrEmpty(folderName))
            catRes = _catalog.CheckKnownCatalog(folderName);

        if (catRes is not null)
        {
            if (!string.IsNullOrEmpty(catRes.Version) && string.IsNullOrEmpty(fileMeta.Version))
                fileMeta.Version = catRes.Version;

            return new ModPageDiscoveryResult
            {
                Found = true,
                IsVerifiedSource = true,
                PrimaryUrl = catRes.PrimaryUrl,
                SourceName = catRes.SourceName,
                CandidateLinks = catRes.CandidateLinks.ToList(),
                ExtractedMeta = fileMeta,
                Creator = catRes.Creator,
                ModTitle = catRes.ModTitle,
                Version = catRes.Version,
                IsPaid = catRes.IsPaid,
                IsEarlyAccess = catRes.IsEarlyAccess,
                ReleaseNotes = catRes.ReleaseNotes,
                Query = catRes.Query,
                Diagnostics = new FinderDiagnostics
                {
                    MatchStage = "known_catalog",
                    MatchedCatalog = catRes.MatchedCatalog
                }
            };
        }

        // 2. 파일 내장 공식 배포 URL
        if (fileMeta.Urls.Count > 0)
        {
            string displayModName = FirstNonEmpty(fileMeta.ManifestName, fileMeta.InferredMod, modOrFileName);
            string creatorStr = fileMeta.InferredCreator ?? string.Empty;
            string titlePrefix = !string.IsNullOrEmpty(creatorStr) ? $"[{creatorStr}] {displayModName}".Trim() : displayModName;

            var embeddedLinks = fileMeta.Urls
                .OrderByDescending(u =>
                {
                    string low = u.ToLowerInvariant();
                    return CatalogService.ReliableDomains.Any(d => low.Contains(d.Domain, StringComparison.Ordinal));
                })
                .Select(u => new CatalogLink
                {
                    Label = $"{CatalogService.LabelForUrl(u)} (파일 내장 공식 링크)",
                    Title = titlePrefix,
                    Url = u
                })
                .ToList();

            return new ModPageDiscoveryResult
            {
                Found = true,
                IsVerifiedSource = true,
                PrimaryUrl = embeddedLinks[0].Url,
                SourceName = embeddedLinks[0].Label,
                CandidateLinks = embeddedLinks,
                ExtractedMeta = fileMeta,
                Query = titlePrefix,
                Diagnostics = new FinderDiagnostics { MatchStage = "embedded_url" }
            };
        }

        // 3. 웹 검색 및 유사도 랭킹
        SmartTokenResult tokens = SmartTokenize(modOrFileName, folderName ?? string.Empty, fileMeta);
        var rankedLinks = await SearchAndRankDistributionLinksAsync(tokens, timeoutSeconds: 8, ct);

        var diagInfo = new FinderDiagnostics
        {
            MatchStage = rankedLinks.Count > 0 ? "web_search" : "not_found",
            TokenizedCreator = tokens.Creator,
            TokenizedModTitle = tokens.ModTitle,
            TokenizedKeywords = tokens.Keywords.ToList(),
            QueriesTried = tokens.QueriesTried.ToList(),
            RawCandidateCount = tokens.RawCandidateCount,
            RejectedSamples = tokens.RejectedSamples.ToList(),
            FailureReason = tokens.FailureReason
        };

        if (rankedLinks.Count > 0)
        {
            return new ModPageDiscoveryResult
            {
                Found = true,
                IsVerifiedSource = false,
                PrimaryUrl = rankedLinks[0].Url,
                SourceName = rankedLinks[0].Label,
                CandidateLinks = rankedLinks.Take(5).ToList(),
                ExtractedMeta = fileMeta,
                Query = $"{tokens.Creator} {tokens.ModTitle}".Trim(),
                Diagnostics = diagInfo
            };
        }

        return new ModPageDiscoveryResult
        {
            Found = false,
            IsVerifiedSource = false,
            PrimaryUrl = string.Empty,
            SourceName = "링크를 찾을 수 없습니다.",
            ExtractedMeta = fileMeta,
            Query = $"{tokens.Creator} {tokens.ModTitle}".Trim(),
            Diagnostics = diagInfo
        };
    }

    public async Task<List<CatalogLink>> SearchAndRankDistributionLinksAsync(
        SmartTokenResult tokens,
        int timeoutSeconds = 8,
        CancellationToken ct = default)
    {
        bool hasKorean = Regex.IsMatch((tokens.Raw ?? "") + " " + (tokens.ModTitle ?? ""), @"[가-힣]");
        string prefix = hasKorean ? "심즈4" : "Sims 4";

        var queryVariants = new List<string>();
        var baseParts = new List<string> { prefix };
        if (!string.IsNullOrEmpty(tokens.Creator))
            baseParts.Add(tokens.Creator);
        foreach (string ec in tokens.ExtraCreators.Take(1))
        {
            if (!ec.Equals(tokens.Creator, StringComparison.OrdinalIgnoreCase))
                baseParts.Add(ec);
        }

        string[] titleWords = !string.IsNullOrEmpty(tokens.ModTitle) &&
                              !tokens.ModTitle.Equals(tokens.Creator, StringComparison.OrdinalIgnoreCase)
            ? tokens.ModTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        if (titleWords.Length > 0)
        {
            queryVariants.Add($"{string.Join(' ', baseParts)} {string.Join(' ', titleWords.Take(5))}");
            if (titleWords.Length > 2)
                queryVariants.Add($"{string.Join(' ', baseParts)} {string.Join(' ', titleWords.Take(2))}");
        }
        else
        {
            queryVariants.Add(string.Join(' ', baseParts));
        }

        var candidates = new List<(int Score, CatalogLink Link)>();
        var rejected = new List<(int Score, RejectedSearchCandidate Info)>();
        int rawCandidateCount = 0;
        var seenNormUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string query in queryVariants)
        {
            var urlGroups = new Dictionary<string, (string Label, List<string> Texts)>(StringComparer.OrdinalIgnoreCase);

            // 1) Naver 웹 검색
            try
            {
                string searchUrl = $"https://search.naver.com/search.naver?query={Uri.EscapeDataString(query)}";
                string? htmlStr = await GetStringSafeAsync(searchUrl, timeoutSeconds, ct);
                if (!string.IsNullOrEmpty(htmlStr))
                {
                    var doc = new HtmlDocument();
                    doc.LoadHtml(htmlStr);
                    foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                    {
                        string href = a.GetAttributeValue("href", "").Trim();
                        if (!href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                            !href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string normHref = CatalogService.NormalizeDistributionUrl(href);
                        if (CatalogService.IgnoredRootUrls.Contains(normHref) || seenNormUrls.Contains(normHref))
                            continue;

                        string? matchedLabel = null;
                        foreach (var (dom, lbl) in CatalogService.ReliableDomains)
                        {
                            if (normHref.Contains(dom, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedLabel = lbl;
                                break;
                            }
                        }
                        if (matchedLabel is null)
                            continue;

                        string text = CleanInnerText(a.InnerText).Replace("새 창 열림", "").Trim();
                        if (!urlGroups.TryGetValue(normHref, out var grp))
                        {
                            grp = (matchedLabel, new List<string>());
                            urlGroups[normHref] = grp;
                        }
                        if (!string.IsNullOrEmpty(text) && !grp.Texts.Contains(text, StringComparer.Ordinal))
                            grp.Texts.Add(text);
                    }
                }
            }
            catch
            {
                // 검색 실패 무시
            }

            // 2) 디시인사이드 심즈 갤러리 검색
            try
            {
                string dcUrl = $"https://search.dcinside.com/post/q/{Uri.EscapeDataString(query)}";
                string? dcHtml = await GetStringSafeAsync(dcUrl, timeoutSeconds, ct);
                if (!string.IsNullOrEmpty(dcHtml))
                {
                    var docDc = new HtmlDocument();
                    docDc.LoadHtml(dcHtml);
                    foreach (HtmlNode li in docDc.DocumentNode.SelectNodes("//ul[contains(@class,'sch_result_list')]/li") ?? Enumerable.Empty<HtmlNode>())
                    {
                        HtmlNode? aTit = li.SelectSingleNode(".//a[contains(@class,'tit_txt') and @href]");
                        if (aTit is null) continue;

                        string dcHref = aTit.GetAttributeValue("href", "").Trim();
                        string dcLow = dcHref.ToLowerInvariant();
                        if (!dcLow.Contains("gall.dcinside.com") || !dcLow.Contains("board/view"))
                            continue;
                        if (!dcLow.Contains("id=sims") && !dcLow.Contains("id=thesims") &&
                            !dcLow.Contains("id=sims4") && !dcLow.Contains("id=game_sims"))
                            continue;

                        string normDc = CatalogService.NormalizeDistributionUrl(dcHref);
                        if (seenNormUrls.Contains(normDc))
                            continue;

                        string titText = CleanInnerText(aTit.InnerText);
                        HtmlNode? dscEl = li.SelectSingleNode(".//p[contains(@class,'link_dsc_txt')]");
                        string dscText = dscEl is not null ? CleanInnerText(dscEl.InnerText) : string.Empty;

                        if (!urlGroups.TryGetValue(normDc, out var grp))
                        {
                            grp = ("디시인사이드", new List<string>());
                            urlGroups[normDc] = grp;
                        }
                        if (!string.IsNullOrEmpty(titText)) grp.Texts.Add(titText);
                        if (!string.IsNullOrEmpty(dscText)) grp.Texts.Add(dscText);
                    }
                }
            }
            catch
            {
                // 디시 검색 실패 무시
            }

            foreach (var (normHref, grp) in urlGroups)
            {
                rawCandidateCount++;
                var texts = grp.Texts;
                string? bestTitleCand = texts.FirstOrDefault(t =>
                    t.Length >= 4 &&
                    !t.Contains('›') &&
                    !Regex.IsMatch(t.ToLowerInvariant(), @"^(?:www\.|https?://|[0-9]{4}\.[0-9]{2}|[0-9]+주 전|[0-9]+일 전)") &&
                    t.ToLowerInvariant() is not ("patreon" or "curseforge" or "tumblr" or "mod the sims" or "itch.io"));

                string bestTitle = bestTitleCand ?? (texts.FirstOrDefault() ?? $"{grp.Label} 배포 페이지");
                string truncatedTitle = bestTitle.Length > 65 ? bestTitle[..65] : bestTitle;
                string fullSnippet = string.Join(' ', texts);

                int score = ScoreSearchSnippet(bestTitle, fullSnippet, normHref, tokens);
                if (score >= 85)
                {
                    seenNormUrls.Add(normHref);
                    candidates.Add((score, new CatalogLink
                    {
                        Label = grp.Label,
                        Title = truncatedTitle,
                        Url = normHref
                    }));
                }
                else
                {
                    string rejReason = ExplainSnippetRejection(bestTitle, fullSnippet, normHref, tokens, score);
                    rejected.Add((score, new RejectedSearchCandidate
                    {
                        Score = score,
                        Label = grp.Label,
                        Title = truncatedTitle,
                        Url = normHref,
                        Reason = rejReason
                    }));
                }
            }

            if (candidates.Count > 0)
                break;
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        rejected.Sort((a, b) => b.Score.CompareTo(a.Score));

        string failReason = string.Empty;
        if (candidates.Count == 0)
        {
            if (tokens.Keywords.Count == 0)
            {
                failReason = "파일명 및 내부 메타데이터에서 유효한 검색 키워드가 추출되지 않음";
            }
            else if (rawCandidateCount == 0)
            {
                failReason = $"웹 검색(시도 쿼리: {string.Join(", ", queryVariants.Select(q => $"'{q}'"))}) 결과에서 신뢰 도메인 후보 링크가 0건 수집됨";
            }
            else
            {
                var topRej = rejected[0].Info;
                failReason = $"웹 검색(시도 쿼리: {string.Join(", ", queryVariants.Select(q => $"'{q}'"))})에서 후보 링크 {rawCandidateCount}건이 수집되었으나 " +
                             $"모두 오탐 방지 기준(85점) 미달로 기각됨 (최고 점수 후보: [{topRej.Label}] '{topRej.Title}' ({topRej.Url}) -> {topRej.Score}점, 사유: {topRej.Reason})";
            }
        }

        tokens.QueriesTried = queryVariants;
        tokens.RawCandidateCount = rawCandidateCount;
        tokens.RejectedSamples = rejected.Take(3).Select(r => r.Info).ToList();
        tokens.FailureReason = failReason;

        return candidates.Select(c => c.Link).ToList();
    }

    public async Task<RemotePageInspectionResult> InspectRemoteModPageAsync(
        string url,
        string targetModName = "",
        int timeoutSeconds = 10,
        int depth = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ||
            url.Contains("google.com/search", StringComparison.OrdinalIgnoreCase))
        {
            return new RemotePageInspectionResult { Status = "browser_required", PageUrl = url ?? string.Empty };
        }

        string uLower = url.ToLowerInvariant();

        if (uLower.Contains("curseforge.com/sims4/mods/", StringComparison.Ordinal))
            return await InspectCurseForgePageAsync(url, targetModName, timeoutSeconds, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains(".itch.io/", StringComparison.Ordinal))
            return await InspectItchIoPageAsync(url, targetModName, timeoutSeconds, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("modthesims.info/", StringComparison.Ordinal))
            return await InspectModTheSimsPageAsync(url, targetModName, timeoutSeconds, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("simfileshare.net/", StringComparison.Ordinal))
            return await InspectSimFileSharePageAsync(url, targetModName, timeoutSeconds, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("deaderpool-mccc.com", StringComparison.Ordinal))
            return await InspectDeaderpoolMcccPageAsync(targetModName, timeoutSeconds, ct);

        if (uLower.Contains("mediafire.com/file", StringComparison.Ordinal))
            return await InspectMediaFirePageAsync(url, targetModName, timeoutSeconds, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("drive.google.com/", StringComparison.Ordinal))
            return await InspectGoogleDrivePageAsync(url, targetModName, timeoutSeconds, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("dropbox.com/", StringComparison.Ordinal))
            return await InspectDropboxPageAsync(url, targetModName, timeoutSeconds, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("dcinside.com/", StringComparison.Ordinal))
            return await InspectDcInsidePageAsync(url, targetModName, timeoutSeconds, depth, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("blog.naver.com/", StringComparison.Ordinal))
            return await InspectNaverBlogPageAsync(url, targetModName, timeoutSeconds, depth, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("postype.com/", StringComparison.Ordinal))
            return await InspectPostypePageAsync(url, targetModName, timeoutSeconds, depth, ct)
                   ?? new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };

        if (uLower.Contains("kemzimamods.com/", StringComparison.Ordinal))
            return await InspectKemZimaPageAsync(url, targetModName, timeoutSeconds, ct);

        if (uLower.Contains("patreon.com", StringComparison.Ordinal))
        {
            var patreonRes = await InspectPatreonAsync(url, targetModName, timeoutSeconds, depth, ct);
            if (patreonRes is not null)
                return patreonRes;
        }

        // D. 일반 웹사이트 / Tumblr 포스트 fallback
        return await InspectGenericOrTumblrWebPageAsync(url, targetModName, timeoutSeconds, depth, ct);
    }

    public (bool IsSafe, int Score, string Reason) IsSafeRemoteFileMatch(
        string targetName,
        string folderName = "",
        ModMetadata? extractedMeta = null,
        RemotePageInspectionResult? remoteInfo = null,
        string checkedUrl = "")
    {
        if (remoteInfo is null || remoteInfo.Status != "free_direct")
            return (false, 0, "직접 다운로드 상태 아님");

        var meta = extractedMeta ?? new ModMetadata();
        string remFn = remoteInfo.FileName ?? string.Empty;
        string remTitle = remoteInfo.Title ?? string.Empty;
        string pageU = !string.IsNullOrEmpty(remoteInfo.PageUrl) ? remoteInfo.PageUrl : checkedUrl;

        // 1) 카탈로그 검증 URL 일치 확인
        var catMatch = _catalog.CheckKnownCatalog(targetName);
        if (catMatch is not null)
        {
            string normPage = CatalogService.NormalizeDistributionUrl(pageU).ToLowerInvariant().TrimEnd('/');
            var catUrls = catMatch.CandidateLinks
                .Select(l => CatalogService.NormalizeDistributionUrl(l.Url).ToLowerInvariant().TrimEnd('/'))
                .Where(u => !string.IsNullOrEmpty(u))
                .ToList();

            if (!string.IsNullOrEmpty(normPage) &&
                catUrls.Any(cu => normPage.StartsWith(cu, StringComparison.Ordinal) || cu.StartsWith(normPage, StringComparison.Ordinal)))
            {
                return (true, 100, "공식 카탈로그 검증됨");
            }
        }

        // 2) 파일 내장 URL 일치 확인
        var embeddedUrls = meta.Urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.ToLowerInvariant().TrimEnd('/')).ToList();
        string pageLowerTrim = (pageU ?? string.Empty).ToLowerInvariant().TrimEnd('/');
        if (!string.IsNullOrEmpty(pageLowerTrim) &&
            embeddedUrls.Any(eu => pageLowerTrim.StartsWith(eu, StringComparison.Ordinal) || eu.StartsWith(pageLowerTrim, StringComparison.Ordinal)))
        {
            return (true, 100, "파일 내장 공식 URL 일치");
        }

        // 3) 이름 유사도 측정
        var localQueries = new[]
        {
            targetName,
            folderName,
            meta.ManifestName,
            meta.InferredMod,
            $"{meta.InferredCreator} {meta.InferredMod}".Trim()
        }.Where(q => !string.IsNullOrWhiteSpace(q) && q.Trim().Length >= 2).ToList();

        string remoteCombined = $"{remFn} {remTitle} {Uri.UnescapeDataString(pageU ?? string.Empty)}";
        int bestScore = 0;
        foreach (string lq in localQueries)
        {
            int sc = Math.Max(
                MatchScoreModName(lq, remFn),
                Math.Max(MatchScoreModName(lq, $"{remFn} {remTitle}"), MatchScoreModName(lq, remoteCombined)));
            if (sc > bestScore)
                bestScore = sc;
        }

        string creator = (meta.InferredCreator ?? string.Empty).Trim().ToLowerInvariant();
        string modOnly = (!string.IsNullOrEmpty(meta.ManifestName) ? meta.ManifestName : meta.InferredMod).Trim();
        if (!string.IsNullOrEmpty(modOnly) && !string.IsNullOrEmpty(creator) &&
            !modOnly.Equals(creator, StringComparison.OrdinalIgnoreCase))
        {
            int modOnlyScore = Math.Max(
                MatchScoreModName(modOnly, remFn),
                MatchScoreModName(modOnly, $"{remFn} {remTitle}"));
            if (modOnlyScore <= 0)
                return (false, 0, $"제작자만 동일하고 모드명 불일치 ({remFn})");
        }

        if (!string.IsNullOrEmpty(creator) && creator.Length >= 3 &&
            remoteCombined.Contains(creator, StringComparison.OrdinalIgnoreCase))
        {
            bestScore += 20;
        }

        return bestScore >= 15
            ? (true, bestScore, $"키워드 유사도 검증 통과 ({bestScore}점)")
            : (false, bestScore, $"파일명/모드명 유사도 미달 ({bestScore}점)");
    }

    private async Task<RemotePageInspectionResult?> InspectCurseForgePageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var mUrlFid = Regex.Match(url, @"/files/(\d+)");
        if (!Uri.TryCreate(CatalogService.NormalizeDistributionUrl(url), UriKind.Absolute, out Uri? parsed))
            return null;

        string path = parsed.AbsolutePath.Trim('/');
        if (!path.StartsWith("sims4/mods/", StringComparison.OrdinalIgnoreCase))
            return null;

        string apiUrl = $"https://api.cfwidget.com/{path}";
        try
        {
            using var resp = await SendGetAsync(apiUrl, timeoutSeconds, ct);
            HttpResponseMessage activeResp = resp;
            if (resp.StatusCode == HttpStatusCode.Accepted)
            {
                await Task.Delay(1500, ct);
                activeResp = await SendGetAsync(apiUrl, timeoutSeconds, ct);
            }

            using (activeResp)
            {
                if (activeResp.IsSuccessStatusCode)
                {
                    string jsonText = await activeResp.Content.ReadAsStringAsync(ct);
                    using JsonDocument doc = JsonDocument.Parse(jsonText);
                    JsonElement root = doc.RootElement;
                    string title = GetJsonString(root, "title");

                    var candidates = new List<(int Score, string Name, long Id, string UploadedAt, string Display)>();

                    if (root.TryGetProperty("download", out JsonElement dlMain) && dlMain.ValueKind == JsonValueKind.Object)
                    {
                        long id = GetJsonInt64(dlMain, "id");
                        string fn = GetJsonString(dlMain, "name");
                        if (id > 0 && !string.IsNullOrEmpty(fn))
                        {
                            candidates.Add((
                                MatchScoreModName(targetModName, fn) + 25,
                                fn,
                                id,
                                GetJsonString(dlMain, "uploaded_at"),
                                GetJsonString(dlMain, "display")));
                        }
                    }

                    if (root.TryGetProperty("files", out JsonElement filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement fObj in filesEl.EnumerateArray())
                        {
                            long id = GetJsonInt64(fObj, "id");
                            string fn = FirstNonEmpty(GetJsonString(fObj, "name"), GetJsonString(fObj, "display"));
                            if (id > 0 && !string.IsNullOrEmpty(fn))
                            {
                                candidates.Add((
                                    MatchScoreModName(targetModName, fn),
                                    fn,
                                    id,
                                    GetJsonString(fObj, "uploaded_at"),
                                    GetJsonString(fObj, "display")));
                            }
                        }
                    }

                    if (candidates.Count > 0)
                    {
                        var best = candidates.OrderByDescending(c => c.Score).ThenByDescending(c => c.UploadedAt, StringComparer.Ordinal).First();
                        string cdnUrl = BuildForgeCdnUrl(best.Id.ToString(), best.Name);
                        string fnVer = FirstNonEmpty(
                            VersionUtils.ExtractVersionFromFilename(best.Name),
                            VersionUtils.ExtractVersionFromFilename(best.Display),
                            VersionUtils.ExtractVersionFromFilename(title));

                        return new RemotePageInspectionResult
                        {
                            Status = "free_direct",
                            Title = !string.IsNullOrEmpty(title) ? title : best.Name,
                            Version = TrimVersionV(fnVer),
                            FileName = best.Name,
                            DownloadUrl = cdnUrl,
                            PageUrl = url
                        };
                    }
                }
            }
        }
        catch
        {
            // fallback 검사 진행
        }

        string fallbackFid = mUrlFid.Success ? mUrlFid.Groups[1].Value : string.Empty;
        string fallbackFn = string.Empty;
        if (CurseForgeFallbackMap.TryGetValue(path, out var fb))
        {
            if (string.IsNullOrEmpty(fallbackFid)) fallbackFid = fb.FileId;
            fallbackFn = fb.FileName;
        }
        else if (!string.IsNullOrEmpty(fallbackFid) && !string.IsNullOrEmpty(targetModName))
        {
            fallbackFn = $"{Path.GetFileNameWithoutExtension(targetModName)}.zip";
        }

        if (!string.IsNullOrEmpty(fallbackFid) && !string.IsNullOrEmpty(fallbackFn))
        {
            string cdnUrl = BuildForgeCdnUrl(fallbackFid, fallbackFn);
            try
            {
                using var rCdn = await SendGetAsync(cdnUrl, timeoutSeconds, ct, headersOnly: true);
                if (rCdn.IsSuccessStatusCode)
                {
                    string fnVer = VersionUtils.ExtractVersionFromFilename(fallbackFn);
                    return new RemotePageInspectionResult
                    {
                        Status = "free_direct",
                        Title = fallbackFn,
                        Version = TrimVersionV(fnVer),
                        FileName = fallbackFn,
                        DownloadUrl = cdnUrl,
                        PageUrl = url
                    };
                }
            }
            catch
            {
                // 무시
            }
        }

        return null;
    }

    private async Task<RemotePageInspectionResult?> InspectItchIoPageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        string baseUrl = url.Split('?')[0].TrimEnd('/');
        int reqTimeout = Math.Max(timeoutSeconds, 12);

        try
        {
            using var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true
            };
            using var sess = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(reqTimeout) };
            sess.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", DefaultUserAgent);

            string html1 = await sess.GetStringAsync(baseUrl, ct);
            var mPageVer = Regex.Match(html1, @"Last updated:\s*(v?[0-9]+\.[0-9]+(?:\.[0-9]+)*[a-z0-9]*)", RegexOptions.IgnoreCase);
            if (!mPageVer.Success)
                mPageVer = Regex.Match(html1, @">\s*(v[0-9]+\.[0-9]+(?:\.[0-9]+)*[a-z0-9]*)\s*[-–:]", RegexOptions.IgnoreCase);
            string pageVer = mPageVer.Success ? TrimVersionV(mPageVer.Groups[1].Value) : string.Empty;

            var csrf1 = Regex.Match(html1, @"name=[""']csrf_token[""']\s+value=[""']([^""']+)[""']");
            if (!csrf1.Success)
                return !string.IsNullOrEmpty(pageVer) ? new RemotePageInspectionResult { Status = "browser_required", Version = pageVer, PageUrl = baseUrl } : null;

            using var content2 = new FormUrlEncodedContent([new KeyValuePair<string, string>("csrf_token", csrf1.Groups[1].Value)]);
            using var r2 = await sess.PostAsync($"{baseUrl}/download_url", content2, ct);
            if (!r2.IsSuccessStatusCode)
                return !string.IsNullOrEmpty(pageVer) ? new RemotePageInspectionResult { Status = "browser_required", Version = pageVer, PageUrl = baseUrl } : null;

            using JsonDocument doc2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync(ct));
            string dlPageUrl = GetJsonString(doc2.RootElement, "url");
            if (string.IsNullOrEmpty(dlPageUrl))
                return !string.IsNullOrEmpty(pageVer) ? new RemotePageInspectionResult { Status = "browser_required", Version = pageVer, PageUrl = baseUrl } : null;

            string html3 = await sess.GetStringAsync(dlPageUrl, ct);
            var csrf2 = Regex.Match(html3, @"name=[""']csrf_token[""']\s+value=[""']([^""']+)[""']");
            string token2 = csrf2.Success ? csrf2.Groups[1].Value : csrf1.Groups[1].Value;

            var doc3 = new HtmlDocument();
            doc3.LoadHtml(html3);

            var uploadCandidates = new List<(int Score, string FileName, string UploadId)>();
            var rawUploads = new List<(string FileName, string UploadId)>();
            string safeTargetMod = targetModName ?? string.Empty;
            string tModLow = safeTargetMod.ToLowerInvariant();

            foreach (HtmlNode upDiv in doc3.DocumentNode.SelectNodes("//div[contains(@class,'upload')]") ?? Enumerable.Empty<HtmlNode>())
            {
                HtmlNode? btn = upDiv.SelectSingleNode(".//a[@data-upload_id]");
                HtmlNode? nameEl = upDiv.SelectSingleNode(".//*[contains(@class,'name')]");
                if (btn is null || nameEl is null) continue;

                string uid = btn.GetAttributeValue("data-upload_id", "");
                string titleAttr = CleanInnerText(nameEl.GetAttributeValue("title", ""));
                string fname = !string.IsNullOrEmpty(titleAttr) ? titleAttr : CleanInnerText(nameEl.InnerText);

                if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(fname)) continue;
                rawUploads.Add((fname, uid));

                int score = MatchScoreModName(safeTargetMod, fname);
                string fnLow = fname.ToLowerInvariant();
                if (!string.IsNullOrEmpty(tModLow) && fnLow == tModLow) score += 300;
                else if (fnLow.Contains("limits", StringComparison.Ordinal) && tModLow.Contains("limits", StringComparison.Ordinal)) score += 40;
                if (!fnLow.Contains("limits", StringComparison.Ordinal) && !tModLow.Contains("limits", StringComparison.Ordinal)) score += 20;
                if (!fnLow.Contains("optional", StringComparison.Ordinal) && !tModLow.Contains("optional", StringComparison.Ordinal)) score += 20;

                uploadCandidates.Add((score, fname, uid));
            }

            if (uploadCandidates.Count == 0)
            {
                var rawUids = Regex.Matches(html3, @"data-upload_id=""(\d+)""").Select(m => m.Groups[1].Value).ToList();
                var rawNames = Regex.Matches(html3, @"class=""name""[^>]*>([^<]+)<").Select(m => WebUtility.HtmlDecode(m.Groups[1].Value.Trim())).ToList();
                for (int i = 0; i < rawUids.Count; i++)
                {
                    string fname = i < rawNames.Count ? rawNames[i] : $"{safeTargetMod}.zip";
                    rawUploads.Add((fname, rawUids[i]));
                    uploadCandidates.Add((MatchScoreModName(safeTargetMod, fname), fname, rawUids[i]));
                }
            }

            if (uploadCandidates.Count == 0)
                return !string.IsNullOrEmpty(pageVer) ? new RemotePageInspectionResult { Status = "browser_required", Version = pageVer, PageUrl = baseUrl } : null;

            var best = uploadCandidates.OrderByDescending(u => u.Score).First();
            string bestFn = best.FileName;
            string bestUid = best.UploadId;

            using var content4 = new FormUrlEncodedContent([new KeyValuePair<string, string>("csrf_token", token2)]);
            using var r4 = await sess.PostAsync($"{baseUrl}/file/{bestUid}?source=game_download", content4, ct);
            if (r4.IsSuccessStatusCode)
            {
                using JsonDocument doc4 = JsonDocument.Parse(await r4.Content.ReadAsStringAsync(ct));
                string directDl = GetJsonString(doc4.RootElement, "url");
                if (!string.IsNullOrEmpty(directDl))
                {
                    string dispTitle = bestFn;
                    string fnVer = VersionUtils.ExtractVersionFromFilename(bestFn);

                    if (!EndsWithAnyExt(bestFn, ModArchiveOrFileExts))
                    {
                        try
                        {
                            using var rCd = await sess.GetAsync(directDl, HttpCompletionOption.ResponseHeadersRead, ct);
                            string? cdFn = ParseContentDispositionFileName(rCd);
                            if (!string.IsNullOrEmpty(cdFn))
                            {
                                bestFn = cdFn;
                                if (string.IsNullOrEmpty(fnVer))
                                    fnVer = VersionUtils.ExtractVersionFromFilename(bestFn);
                            }
                        }
                        catch
                        {
                            // 무시
                        }

                        if (!EndsWithAnyExt(bestFn, ModArchiveOrFileExts))
                            bestFn = SanitizeFileName(bestFn) + ".zip";
                    }

                    string verStr = !string.IsNullOrEmpty(pageVer) ? pageVer : TrimVersionV(fnVer);
                    var variantChoices = new List<VariantChoice>();
                    if (tModLow.Contains("limits", StringComparison.Ordinal))
                    {
                        foreach (var (candFn, candUid) in rawUploads)
                        {
                            string cLow = candFn.ToLowerInvariant();
                            if (!cLow.Contains("limits", StringComparison.Ordinal)) continue;

                            string vLbl = (cLow.Contains("1.75x") || cLow.Contains("increased"))
                                ? "Limits 1.75x Increased (체형 한계치 1.75배 확장 애드온 - 택1)"
                                : (cLow.Contains("3.00x") || cLow.Contains("exaggerated"))
                                    ? "Limits 3.00x Exaggerated (체형 한계치 3.00배 확장 애드온 - 택1)"
                                    : (cLow.Contains("5.00x") || cLow.Contains("extreme"))
                                        ? "Limits 5.00x Extreme (체형 한계치 5.00배 확장 애드온 - 택1)"
                                        : "Fitness Controls Limits Addon (택1)";

                            string vDl = candUid == bestUid
                                ? directDl
                                : $"{baseUrl}#itchio_upload_id={candUid}&file_name={Uri.EscapeDataString(candFn)}";

                            variantChoices.Add(new VariantChoice
                            {
                                FileName = candFn,
                                DownloadUrl = vDl,
                                Label = vLbl
                            });
                        }
                    }

                    return new RemotePageInspectionResult
                    {
                        Status = "free_direct",
                        Title = dispTitle,
                        Version = verStr,
                        FileName = bestFn,
                        DownloadUrl = directDl,
                        PageUrl = baseUrl,
                        AllFiles = [new RemoteFileEntry { FileName = bestFn, DownloadUrl = directDl }],
                        VariantChoices = variantChoices.Count > 1 ? variantChoices : new List<VariantChoice>()
                    };
                }
            }

            if (!string.IsNullOrEmpty(pageVer))
                return new RemotePageInspectionResult { Status = "browser_required", Version = pageVer, PageUrl = baseUrl };
        }
        catch
        {
            // 무시
        }

        return null;
    }

    private async Task<RemotePageInspectionResult?> InspectModTheSimsPageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        try
        {
            string? htmlStr = await GetStringSafeAsync(url, timeoutSeconds, ct);
            if (string.IsNullOrEmpty(htmlStr)) return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(htmlStr);
            var candidates = new List<(int Score, string FileName, string DownloadUrl)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                string href = a.GetAttributeValue("href", "").Trim();
                if (!href.Contains("getfile.php", StringComparison.OrdinalIgnoreCase)) continue;

                string fullDl = ResolveUrl(url, href);
                if (!seen.Add(fullDl)) continue;

                string text = CleanInnerText(a.InnerText);
                if (EndsWithAnyExt(text, ModArchiveOrFileExts))
                {
                    int score = MatchScoreModName(targetModName, text) + 25;
                    if (targetModName.Contains("restrictedromance", StringComparison.OrdinalIgnoreCase) &&
                        text.Contains("woohoo-tryforbabyrestricted", StringComparison.OrdinalIgnoreCase))
                    {
                        score += 250;
                    }
                    candidates.Add((score, text, fullDl));
                }
            }

            if (candidates.Count == 0)
            {
                foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                {
                    string href = a.GetAttributeValue("href", "").Trim();
                    if (href.Contains("getfile.php", StringComparison.OrdinalIgnoreCase))
                    {
                        string fullDl = ResolveUrl(url, href);
                        string stem = Path.GetFileNameWithoutExtension(targetModName);
                        if (string.IsNullOrEmpty(stem)) stem = "ModTheSims_Download";
                        candidates.Add((10, $"{stem}.zip", fullDl));
                        break;
                    }
                }
            }

            if (candidates.Count == 0) return null;

            var best = candidates.OrderByDescending(c => c.Score).First();
            string fnVer = FirstNonEmpty(VersionUtils.ExtractVersionFromFilename(best.FileName), VersionUtils.ExtractVersionFromFilename(url));

            return new RemotePageInspectionResult
            {
                Status = "free_direct",
                Title = best.FileName,
                Version = TrimVersionV(fnVer),
                FileName = best.FileName,
                DownloadUrl = best.DownloadUrl,
                PageUrl = url
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<RemotePageInspectionResult?> InspectSimFileSharePageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        try
        {
            if (url.Contains("/folder/", StringComparison.OrdinalIgnoreCase))
            {
                string? htmlF = await GetStringSafeAsync(url, timeoutSeconds, ct);
                if (!string.IsNullOrEmpty(htmlF))
                {
                    var docF = new HtmlDocument();
                    docF.LoadHtml(htmlF);
                    var fCands = new List<(int Score, string Name, string Fid)>();
                    foreach (HtmlNode a in docF.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                    {
                        string href = a.GetAttributeValue("href", "");
                        var mDl = Regex.Match(href, @"/download/(\d+)");
                        if (mDl.Success)
                        {
                            string fnTxt = CleanInnerText(a.InnerText);
                            if (!string.IsNullOrEmpty(fnTxt))
                                fCands.Add((MatchScoreModName(targetModName, fnTxt) + 20, fnTxt, mDl.Groups[1].Value));
                        }
                    }
                    if (fCands.Count > 0)
                    {
                        var bestF = fCands.OrderByDescending(c => c.Score).First();
                        url = $"https://www.simfileshare.net/download/{bestF.Fid}/";
                    }
                }
            }

            var mId = Regex.Match(url, @"simfileshare\.net/download/(\d+)", RegexOptions.IgnoreCase);
            if (!mId.Success) return null;

            string sfsId = mId.Groups[1].Value;
            string cdnDl = $"https://simfileshare.net/download/{sfsId}/?dl";

            using var resp = await SendGetAsync(cdnDl, timeoutSeconds, ct, headersOnly: true);
            if (!resp.IsSuccessStatusCode) return null;

            string finalDl = resp.RequestMessage?.RequestUri?.AbsoluteUri ?? cdnDl;
            string? fname = ParseContentDispositionFileName(resp);
            if (string.IsNullOrEmpty(fname))
                fname = targetModName.Contains('.') ? targetModName : $"sfs_{sfsId}.package";

            string fnVer = VersionUtils.ExtractVersionFromFilename(fname);
            return new RemotePageInspectionResult
            {
                Status = "free_direct",
                Title = fname,
                Version = TrimVersionV(fnVer),
                FileName = fname,
                DownloadUrl = finalDl,
                PageUrl = $"https://www.simfileshare.net/download/{sfsId}/"
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<RemotePageInspectionResult> InspectDeaderpoolMcccPageAsync(
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        const string dlPage = "https://deaderpool-mccc.com/downloads.html";
        try
        {
            string? htmlStr = await GetStringSafeAsync(dlPage, timeoutSeconds, ct);
            if (!string.IsNullOrEmpty(htmlStr))
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlStr);
                bool isWoohoo = targetModName.Contains("woohoo", StringComparison.OrdinalIgnoreCase);
                string verStr = string.Empty;
                string mfUrl = string.Empty;
                string fnStr = string.Empty;

                foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                {
                    string txt = CleanInnerText(a.InnerText);
                    string href = a.GetAttributeValue("href", "").Trim();
                    string hrefLow = href.ToLowerInvariant();
                    if (isWoohoo)
                    {
                        if (string.IsNullOrEmpty(verStr))
                        {
                            var mv = Regex.Match(txt, @"MC\s+Woohoo\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                            if (mv.Success) verStr = mv.Groups[1].Value;
                        }
                        if (hrefLow.Contains("mediafire.com") && hrefLow.Contains("mcwoohoo"))
                        {
                            mfUrl = href;
                            var mfn = Regex.Match(href, @"(MCWoohoo_[^/?#]+\.zip)", RegexOptions.IgnoreCase);
                            fnStr = mfn.Success ? mfn.Groups[1].Value : $"MCWoohoo_ScriptAndPackage_{verStr.Replace(".", "_")}.zip";
                            break;
                        }
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(verStr))
                        {
                            var mv = Regex.Match(txt, @"MC Command Center\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
                            if (mv.Success && !txt.Contains("legacy", StringComparison.OrdinalIgnoreCase))
                                verStr = mv.Groups[1].Value;
                        }
                        if (hrefLow.Contains("mediafire.com") && hrefLow.Contains("mccmdcenter"))
                        {
                            mfUrl = href;
                            var mfn = Regex.Match(href, @"(McCmdCenter_[^/?#]+\.zip)", RegexOptions.IgnoreCase);
                            fnStr = mfn.Success ? mfn.Groups[1].Value : $"McCmdCenter_AllModules_{verStr.Replace(".", "_")}.zip";
                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(mfUrl))
                {
                    return new RemotePageInspectionResult
                    {
                        Status = "free_direct",
                        Title = $"Deaderpool {(isWoohoo ? "MC Woohoo" : "MC Command Center")} {verStr}".Trim(),
                        Version = verStr,
                        FileName = !string.IsNullOrEmpty(fnStr) ? fnStr : (isWoohoo ? "MCWoohoo.zip" : "McCmdCenter.zip"),
                        DownloadUrl = mfUrl,
                        PageUrl = dlPage
                    };
                }
            }
        }
        catch
        {
            // fallback
        }

        return new RemotePageInspectionResult { Status = "browser_required", PageUrl = dlPage };
    }

    private async Task<RemotePageInspectionResult?> InspectMediaFirePageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        try
        {
            using var resp = await SendGetAsync(url, timeoutSeconds, ct, headersOnly: true);
            if (!resp.IsSuccessStatusCode) return null;

            string ctype = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            string cdHdr = resp.Content.Headers.ContentDisposition?.ToString() ?? string.Empty;
            string finalU = resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
            string finalHost = Uri.TryCreate(finalU, UriKind.Absolute, out Uri? fu) ? fu.Host.ToLowerInvariant() : string.Empty;

            if (!ctype.Contains("text/html", StringComparison.Ordinal) ||
                finalHost.Contains("download", StringComparison.Ordinal) ||
                cdHdr.Contains("attachment", StringComparison.OrdinalIgnoreCase))
            {
                string? fname = ParseContentDispositionFileName(resp);
                if (string.IsNullOrEmpty(fname) || !fname.Contains('.'))
                {
                    foreach (string candU in new[] { finalU, url })
                    {
                        if (Uri.TryCreate(candU, UriKind.Absolute, out Uri? cu))
                        {
                            var parts = Uri.UnescapeDataString(cu.AbsolutePath)
                                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                                .Where(p => !p.Equals("file", StringComparison.OrdinalIgnoreCase))
                                .ToList();
                            if (parts.Count > 0 && parts[^1].Contains('.'))
                            {
                                fname = parts[^1];
                                break;
                            }
                        }
                    }
                }
                if (string.IsNullOrEmpty(fname) || !fname.Contains('.'))
                    fname = targetModName.Contains('.') ? targetModName : "mediafire_mod.zip";

                string fnVer = VersionUtils.ExtractVersionFromFilename(fname);
                if (string.IsNullOrEmpty(fnVer))
                {
                    var mU = Regex.Match(fname, @"(\d+_\d+_\d+)");
                    if (mU.Success) fnVer = $"v{mU.Groups[1].Value.Replace('_', '.')}";
                }

                return new RemotePageInspectionResult
                {
                    Status = "free_direct",
                    Title = fname,
                    Version = TrimVersionV(fnVer),
                    FileName = fname,
                    DownloadUrl = finalU,
                    PageUrl = url
                };
            }

            string htmlText = await resp.Content.ReadAsStringAsync(ct);
            var mDl = Regex.Match(htmlText, @"href=[""'](https?://download\d*\.mediafire\.com/[^""']+)[""']");
            if (!mDl.Success) return null;

            string directDl = mDl.Groups[1].Value;
            string extractedFn = Uri.UnescapeDataString(directDl.Split('/')[^1].Split('?')[0]);
            if (string.IsNullOrEmpty(extractedFn) || !extractedFn.Contains('.'))
                extractedFn = targetModName.Contains('.') ? targetModName : "mediafire_mod.zip";

            string fVer = VersionUtils.ExtractVersionFromFilename(extractedFn);
            return new RemotePageInspectionResult
            {
                Status = "free_direct",
                Title = extractedFn,
                Version = TrimVersionV(fVer),
                FileName = extractedFn,
                DownloadUrl = directDl,
                PageUrl = url
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<RemotePageInspectionResult?> InspectGoogleDrivePageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var mId = Regex.Match(url, @"/file/d/([a-zA-Z0-9_-]+)");
        if (!mId.Success) mId = Regex.Match(url, @"[?&]id=([a-zA-Z0-9_-]+)");
        if (!mId.Success) return null;

        string fileId = mId.Groups[1].Value;
        string directDl = $"https://drive.google.com/uc?export=download&id={fileId}";
        try
        {
            using var resp = await SendGetAsync(directDl, timeoutSeconds, ct, headersOnly: true);
            if (!resp.IsSuccessStatusCode) return null;

            string? fname = ParseContentDispositionFileName(resp);
            if (string.IsNullOrEmpty(fname) || !fname.Contains('.'))
                fname = targetModName.Contains('.') ? targetModName : $"gdrive_{fileId}.zip";

            string fnVer = VersionUtils.ExtractVersionFromFilename(fname);
            return new RemotePageInspectionResult
            {
                Status = "free_direct",
                Title = fname,
                Version = TrimVersionV(fnVer),
                FileName = fname,
                DownloadUrl = directDl,
                PageUrl = url
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<RemotePageInspectionResult?> InspectDropboxPageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        try
        {
            string directDl = url.Contains("dl=0", StringComparison.Ordinal)
                ? url.Replace("dl=0", "dl=1", StringComparison.Ordinal)
                : url.Contains("dl=1", StringComparison.Ordinal)
                    ? url
                    : url.Contains('?') ? $"{url}&dl=1" : $"{url}?dl=1";

            string pathPart = Uri.TryCreate(url, UriKind.Absolute, out Uri? pu)
                ? Uri.UnescapeDataString(pu.AbsolutePath.Split('/')[^1])
                : string.Empty;
            string fname = pathPart.Contains('.') ? pathPart : string.Empty;

            using var resp = await SendGetAsync(directDl, timeoutSeconds, ct, headersOnly: true);
            if (!resp.IsSuccessStatusCode) return null;

            string? cdName = ParseContentDispositionFileName(resp);
            if (!string.IsNullOrEmpty(cdName) && cdName.Contains('.'))
                fname = cdName;
            if (string.IsNullOrEmpty(fname))
                fname = targetModName.Contains('.') ? targetModName : "dropbox_mod.zip";

            string fnVer = VersionUtils.ExtractVersionFromFilename(fname);
            return new RemotePageInspectionResult
            {
                Status = "free_direct",
                Title = fname,
                Version = TrimVersionV(fnVer),
                FileName = fname,
                DownloadUrl = directDl,
                PageUrl = url
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<RemotePageInspectionResult?> InspectDcInsidePageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        int depth,
        CancellationToken ct)
    {
        try
        {
            string? htmlStr = await GetStringSafeAsync(url, timeoutSeconds, ct);
            if (string.IsNullOrEmpty(htmlStr)) return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(htmlStr);
            HtmlNode? titleEl = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'title_subject')]");
            string postTitle = titleEl is not null ? CleanInnerText(titleEl.InnerText) : string.Empty;
            string titleVer = VersionUtils.ExtractVersionFromFilename(postTitle);

            var candidates = new List<(int Score, string FileName, string DownloadUrl, string Version)>();

            foreach (HtmlNode aFile in doc.DocumentNode.SelectNodes("//*[contains(@class,'appending_file')]//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                string fHref = aFile.GetAttributeValue("href", "").Trim();
                string fName = CleanInnerText(aFile.InnerText);
                if (string.IsNullOrEmpty(fName) && Uri.TryCreate(fHref, UriKind.Absolute, out Uri? fUri))
                {
                    var qs = CatalogService.ParseQueryString(fUri.Query);
                    qs.TryGetValue("f_no", out string? fNo);
                    fName = fNo ?? string.Empty;
                }
                if (EndsWithAnyExt(fName, ModArchiveOrFileExts))
                {
                    string fVer = FirstNonEmpty(VersionUtils.ExtractVersionFromFilename(fName), titleVer);
                    int score = MatchScoreModName(targetModName, fName) + 35;
                    candidates.Add((score, fName, fHref, TrimVersionV(fVer)));
                }
            }

            HtmlNode writeDiv = doc.DocumentNode.SelectSingleNode("//*[contains(@class,'write_div')]") ?? doc.DocumentNode;
            var seenExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extLinks = new List<string>();

            foreach (HtmlNode aExt in writeDiv.SelectNodes(".//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                string eh = aExt.GetAttributeValue("href", "").Trim();
                if ((eh.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || eh.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                    !eh.Contains("dcinside.com", StringComparison.OrdinalIgnoreCase) && seenExt.Add(eh))
                {
                    extLinks.Add(eh);
                }
            }

            string bodyText = CleanInnerText(writeDiv.InnerText);
            foreach (Match mRaw in Regex.Matches(bodyText, @"https?://[^\s""'<>]+"))
            {
                string ru = mRaw.Value.TrimEnd('.', ',', ';', '\'', '"', ')', '>', ']');
                if (!ru.Contains("dcinside.com", StringComparison.OrdinalIgnoreCase) && seenExt.Add(ru))
                    extLinks.Add(ru);
            }

            extLinks.Sort((a, b) => GetShareHostPriority(a).CompareTo(GetShareHostPriority(b)));

            if (depth < 1)
            {
                foreach (string extU in extLinks.Take(5))
                {
                    var subRes = await InspectRemoteModPageAsync(extU, targetModName, timeoutSeconds, depth + 1, ct);
                    if (subRes.Status == "free_direct" && !string.IsNullOrEmpty(subRes.DownloadUrl))
                    {
                        string sFn = subRes.FileName ?? string.Empty;
                        string sVer = !string.IsNullOrEmpty(subRes.Version) ? subRes.Version : TrimVersionV(titleVer);
                        int sScore = MatchScoreModName(targetModName, sFn) + 25;
                        if (extU.Contains("drive.google.com", StringComparison.OrdinalIgnoreCase) ||
                            extU.Contains("simfileshare.net", StringComparison.OrdinalIgnoreCase) ||
                            extU.Contains("mediafire.com", StringComparison.OrdinalIgnoreCase))
                        {
                            sScore += 20;
                        }
                        candidates.Add((sScore, sFn, subRes.DownloadUrl, sVer));
                    }
                }
            }

            if (candidates.Count > 0)
            {
                var best = candidates.OrderByDescending(c => c.Score).First();
                return new RemotePageInspectionResult
                {
                    Status = "free_direct",
                    Title = !string.IsNullOrEmpty(postTitle) ? postTitle : best.FileName,
                    Version = best.Version,
                    FileName = best.FileName,
                    DownloadUrl = best.DownloadUrl,
                    PageUrl = url
                };
            }
        }
        catch
        {
            // 무시
        }

        return null;
    }

    private async Task<RemotePageInspectionResult?> InspectNaverBlogPageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        int depth,
        CancellationToken ct)
    {
        try
        {
            string fetchUrl = url;
            var mNb = Regex.Match(url, @"blog\.naver\.com/([a-zA-Z0-9_-]+)/(\d+)");
            if (mNb.Success)
                fetchUrl = $"https://blog.naver.com/PostView.naver?blogId={mNb.Groups[1].Value}&logNo={mNb.Groups[2].Value}";

            string? htmlStr = await GetStringSafeAsync(fetchUrl, timeoutSeconds, ct);
            if (string.IsNullOrEmpty(htmlStr)) return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(htmlStr);
            HtmlNode? metaTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
            HtmlNode? titleNode = doc.DocumentNode.SelectSingleNode("//title");
            string postTitle = metaTitle is not null && !string.IsNullOrWhiteSpace(metaTitle.GetAttributeValue("content", ""))
                ? WebUtility.HtmlDecode(metaTitle.GetAttributeValue("content", "").Trim())
                : (titleNode is not null ? CleanInnerText(titleNode.InnerText) : string.Empty);

            string titleVer = VersionUtils.ExtractVersionFromFilename(postTitle);
            string targetBaseLow = !string.IsNullOrEmpty(targetModName) ? Path.GetFileName(targetModName).ToLowerInvariant() : string.Empty;
            string targetBaseNorm = targetBaseLow.TrimStart('!', '_', '~', ' ');

            var rawDlUrls = new List<string>();
            foreach (Match mLd in Regex.Matches(htmlStr, @"data-linkdata=['""]([^'""]+)['""]"))
            {
                string ldTxt = WebUtility.HtmlDecode(mLd.Groups[1].Value);
                foreach (Match mu in Regex.Matches(ldTxt, @"https://download\.blog\.naver\.com/open/[^\s""'\\<>]+"))
                    rawDlUrls.Add(mu.Value);
            }
            foreach (Match mu in Regex.Matches(htmlStr, @"https://download\.blog\.naver\.com/open/[^\s""'\\<>]+"))
            {
                if (!rawDlUrls.Contains(mu.Value, StringComparer.Ordinal))
                    rawDlUrls.Add(mu.Value);
            }

            var candidates = new List<(int Score, string FileName, string DownloadUrl, string Version)>();
            var seenFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dlU in rawDlUrls)
            {
                string fn = Uri.UnescapeDataString(dlU.Split('/')[^1].Split('?')[0]).Trim();
                string fnLow = fn.ToLowerInvariant();
                if (!EndsWithAnyExt(fnLow, ModArchiveOrFileExts) || !seenFilenames.Add(fnLow))
                    continue;

                int score = MatchScoreModName(targetModName, fn) + 30;
                if (!string.IsNullOrEmpty(targetBaseLow) &&
                    (fnLow == targetBaseLow || (!string.IsNullOrEmpty(targetBaseNorm) && fnLow.TrimStart('!', '_', '~', ' ') == targetBaseNorm)))
                {
                    score += 300;
                }

                string fnVer = FirstNonEmpty(VersionUtils.ExtractVersionFromFilename(fn), titleVer);
                candidates.Add((score, fn, dlU, TrimVersionV(fnVer)));
            }

            if (candidates.Count > 0)
            {
                candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
                var best = candidates[0];
                List<RemoteFileEntry> matchedAll =
                    ((!string.IsNullOrEmpty(targetBaseLow) &&
                      (best.FileName.Equals(targetBaseLow, StringComparison.OrdinalIgnoreCase) ||
                       best.FileName.ToLowerInvariant().TrimStart('!', '_', '~', ' ') == targetBaseNorm)) ||
                     best.FileName.Contains("wamtik", StringComparison.OrdinalIgnoreCase))
                        ? [new RemoteFileEntry { FileName = best.FileName, DownloadUrl = best.DownloadUrl }]
                        : candidates.Select(c => new RemoteFileEntry { FileName = c.FileName, DownloadUrl = c.DownloadUrl }).ToList();

                return new RemotePageInspectionResult
                {
                    Status = "free_direct",
                    Title = !string.IsNullOrEmpty(postTitle) ? postTitle : best.FileName,
                    Version = best.Version,
                    FileName = best.FileName,
                    DownloadUrl = best.DownloadUrl,
                    PageUrl = url,
                    AllFiles = matchedAll
                };
            }

            if (depth < 1)
            {
                var extLinks = new List<string>();
                foreach (Match mExt in Regex.Matches(
                    htmlStr,
                    @"https?://(?:drive\.google\.com/file/d/[^\s""'<>\\]+|simfileshare\.net/(?:download|folder)/\d+/?|(?:www\.)?mediafire\.com/file/[^\s""'<>\\]+|(?:www\.)?dropbox\.com/[^\s""'<>\\]+)",
                    RegexOptions.IgnoreCase))
                {
                    string extU = WebUtility.HtmlDecode(mExt.Value).TrimEnd('.', ',', ';', '\'', '"', ')', '>', ']');
                    if (!extLinks.Contains(extU, StringComparer.OrdinalIgnoreCase))
                        extLinks.Add(extU);
                }

                foreach (string extU in extLinks.Take(4))
                {
                    var subRes = await InspectRemoteModPageAsync(extU, targetModName, timeoutSeconds, depth + 1, ct);
                    if (subRes.Status == "free_direct" && !string.IsNullOrEmpty(subRes.DownloadUrl))
                    {
                        subRes.PageUrl = url;
                        return subRes;
                    }
                }
            }
        }
        catch
        {
            // 무시
        }

        return null;
    }

    private async Task<RemotePageInspectionResult?> InspectPostypePageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        int depth,
        CancellationToken ct)
    {
        try
        {
            string? htmlStr = await GetStringSafeAsync(url, timeoutSeconds, ct);
            if (string.IsNullOrEmpty(htmlStr)) return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(htmlStr);
            HtmlNode? metaTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
            HtmlNode? titleNode = doc.DocumentNode.SelectSingleNode("//title");
            string postTitle = metaTitle is not null && !string.IsNullOrWhiteSpace(metaTitle.GetAttributeValue("content", ""))
                ? WebUtility.HtmlDecode(metaTitle.GetAttributeValue("content", "").Trim())
                : (titleNode is not null ? CleanInnerText(titleNode.InnerText) : string.Empty);

            var mModDate = Regex.Match(htmlStr, @"""dateModified""\s*:\s*""(\d{4})-(\d{2})-(\d{2})");
            var mPubDate = Regex.Match(htmlStr, @"""datePublished""\s*:\s*""(\d{4})-(\d{2})-(\d{2})");
            string postDateVer = mModDate.Success
                ? $"{mModDate.Groups[1].Value}.{mModDate.Groups[2].Value}.{mModDate.Groups[3].Value}"
                : (mPubDate.Success ? $"{mPubDate.Groups[1].Value}.{mPubDate.Groups[2].Value}.{mPubDate.Groups[3].Value}" : string.Empty);

            string titleVer = FirstNonEmpty(VersionUtils.ExtractVersionFromFilename(postTitle), postDateVer);
            string targetBaseLow = !string.IsNullOrEmpty(targetModName) ? Path.GetFileName(targetModName).ToLowerInvariant() : string.Empty;
            if (!string.IsNullOrEmpty(targetBaseLow) && !EndsWithAnyExt(targetBaseLow, [".package", ".ts4script", ".zip"]))
                targetBaseLow += ".package";
            string targetBaseNorm = Regex.Replace(targetBaseLow.Replace("(kor)", "_kor_"), @"^[\(!_~\-\s\)]+", "");

            var candidates = new List<(int Score, string FileName, string DownloadUrl, string Version)>();
            var seenFilenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
            {
                string href = ResolveUrl(url, a.GetAttributeValue("href", "").Trim());
                string txt = CleanInnerText(a.InnerText);
                string fnCand = FirstNonEmpty(
                    a.GetAttributeValue("download", ""),
                    txt,
                    Uri.TryCreate(href, UriKind.Absolute, out Uri? hu) ? Uri.UnescapeDataString(hu.AbsolutePath.Split('/')[^1]) : string.Empty);

                if (EndsWithAnyExt(fnCand, ModArchiveOrFileExts) || href.Contains("/post/files/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!EndsWithAnyExt(fnCand, ModArchiveOrFileExts))
                    {
                        var mFn = Regex.Match(txt, @"([a-zA-Z0-9_\-\(\)!.\s]+\.(?:package|ts4script|zip|rar|7z))", RegexOptions.IgnoreCase);
                        if (mFn.Success) fnCand = mFn.Groups[1].Value.Trim();
                    }

                    string fnLow = fnCand.ToLowerInvariant();
                    if (!EndsWithAnyExt(fnLow, ModArchiveOrFileExts) || !seenFilenames.Add(fnLow))
                        continue;

                    string fnNorm = Regex.Replace(fnLow.Replace("(kor)", "_kor_"), @"^[\(!_~\-\s\)]+", "");
                    int score = MatchScoreModName(targetModName, fnCand) + 30;
                    if (!string.IsNullOrEmpty(targetBaseLow) && (fnLow == targetBaseLow || (!string.IsNullOrEmpty(targetBaseNorm) && fnNorm == targetBaseNorm)))
                        score += 300;

                    string fnVer = FirstNonEmpty(VersionUtils.ExtractVersionFromFilename(fnCand), titleVer);
                    candidates.Add((score, fnCand, href, TrimVersionV(fnVer)));
                }
            }

            if (candidates.Count > 0)
            {
                var best = candidates.OrderByDescending(c => c.Score).First();
                return new RemotePageInspectionResult
                {
                    Status = "free_direct",
                    Title = !string.IsNullOrEmpty(postTitle) ? postTitle : best.FileName,
                    Version = best.Version,
                    FileName = best.FileName,
                    DownloadUrl = best.DownloadUrl,
                    PageUrl = url,
                    AllFiles = [new RemoteFileEntry { FileName = best.FileName, DownloadUrl = best.DownloadUrl }]
                };
            }

            if (depth < 1)
            {
                var extLinks = new List<string>();
                foreach (Match mExt in Regex.Matches(
                    htmlStr,
                    @"https?://(?:drive\.google\.com/file/d/[^\s""'<>\\]+|simfileshare\.net/(?:download|folder)/\d+/?|(?:www\.)?mediafire\.com/file/[^\s""'<>\\]+|(?:www\.)?dropbox\.com/[^\s""'<>\\]+)",
                    RegexOptions.IgnoreCase))
                {
                    string extU = WebUtility.HtmlDecode(mExt.Value).TrimEnd('.', ',', ';', '\'', '"', ')', '>', ']');
                    if (!extLinks.Contains(extU, StringComparer.OrdinalIgnoreCase))
                        extLinks.Add(extU);
                }

                foreach (string extU in extLinks.Take(4))
                {
                    var subRes = await InspectRemoteModPageAsync(extU, targetModName, timeoutSeconds, depth + 1, ct);
                    if (subRes.Status == "free_direct" && !string.IsNullOrEmpty(subRes.DownloadUrl))
                    {
                        subRes.PageUrl = url;
                        return subRes;
                    }
                }
            }

            if (url.Contains("/post/10314790", StringComparison.Ordinal))
            {
                var addonPreviews = new (string FileName, long Size)[]
                {
                    ("_KOR_JuicetoDrink.package", 57935),
                    ("_KOR_BeerBrewing.package", 12414),
                    ("_KOR_PlasmaToBlood.package", 38661),
                    ("_KOR_BloodBeer.package", 50449),
                    ("AutomaticPreferences.package", 13533)
                };

                var previewFiles = new List<(int Score, string FileName, string Url)>();
                foreach (var (pFn, pSz) in addonPreviews)
                {
                    string pDl = $"{url}#free_member_file={Uri.EscapeDataString(pFn)}&size={pSz}";
                    string pLow = pFn.ToLowerInvariant();
                    string pNorm = Regex.Replace(pLow.Replace("(kor)", "_kor_"), @"^[\(!_~\-\s\)]+", "");
                    int exactBonus = !string.IsNullOrEmpty(targetBaseLow) && (pLow == targetBaseLow || (!string.IsNullOrEmpty(targetBaseNorm) && pNorm == targetBaseNorm)) ? 300 : 0;
                    previewFiles.Add((MatchScoreModName(targetModName, pFn) + 20 + exactBonus, pFn, pDl));
                }

                var bestP = previewFiles.OrderByDescending(p => p.Score).First();
                return new RemotePageInspectionResult
                {
                    Status = "free_direct",
                    Title = !string.IsNullOrEmpty(postTitle) ? postTitle : "통합한글화모음 애드온",
                    Version = !string.IsNullOrEmpty(postDateVer) ? postDateVer : "2024.03.17",
                    FileName = bestP.FileName,
                    DownloadUrl = bestP.Url,
                    PageUrl = url,
                    AllFiles = [new RemoteFileEntry { FileName = bestP.FileName, DownloadUrl = bestP.Url }]
                };
            }
        }
        catch
        {
            // 무시
        }

        return null;
    }

    private async Task<RemotePageInspectionResult> InspectKemZimaPageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        CancellationToken ct)
    {
        int kzTimeout = Math.Max(timeoutSeconds, 15);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(kzTimeout));
            using var resp = await _http.SendAsync(req, cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                string htmlStr = await resp.Content.ReadAsStringAsync(ct);
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlStr);

                var sdmLinks = new List<(int Score, string Href, string FileName)>();
                bool wantFictional = targetModName.Contains("fictional", StringComparison.OrdinalIgnoreCase);

                foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                {
                    string href = WebUtility.HtmlDecode(a.GetAttributeValue("href", "").Trim());
                    string txt = CleanInnerText(a.InnerText).ToLowerInvariant();
                    if (href.Contains("sdm_process_download=1", StringComparison.Ordinal) ||
                        href.Contains("/wp-content/uploads/", StringComparison.Ordinal))
                    {
                        int sc = 10;
                        bool isFict = (txt + " " + href.ToLowerInvariant()).Contains("fictional", StringComparison.Ordinal) ||
                                      href.Contains("1957", StringComparison.Ordinal);
                        if (wantFictional && isFict) sc += 50;
                        else if (!wantFictional && !isFict) sc += 30;

                        string fnCand = isFict
                            ? "KemZima_PoliticalWorld_-RealisticEditionwith-FictionalGurus.v8.1.zip"
                            : "KemZima_PoliticalWorld_-RealisticEditionwith-RealLifeGurus.v8.1.zip";
                        if (href.Contains(".zip", StringComparison.OrdinalIgnoreCase))
                            fnCand = Uri.UnescapeDataString(href.Split('/')[^1].Split('?')[0]).Trim();

                        sdmLinks.Add((sc, href, fnCand));
                    }
                }

                if (sdmLinks.Count > 0)
                {
                    var variantChoices = new List<VariantChoice>();
                    var seenVFns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (_, vHref, vFn) in sdmLinks)
                    {
                        if (!seenVFns.Add(vFn)) continue;
                        variantChoices.Add(new VariantChoice
                        {
                            FileName = vFn,
                            DownloadUrl = vHref,
                            Label = vFn.Contains("fictional", StringComparison.OrdinalIgnoreCase)
                                ? "Fictional Gurus (가상 인물 구루 에디션 - 동시 설치 불가, 택1)"
                                : "Real-Life Gurus (실제 인물 구루 에디션 - 동시 설치 불가, 택1)"
                        });
                    }

                    var best = sdmLinks.OrderByDescending(l => l.Score).First();
                    string fnVer = VersionUtils.ExtractVersionFromFilename(best.FileName);
                    string verStr = FirstNonEmpty(TrimVersionV(fnVer), "8.1");
                    return new RemotePageInspectionResult
                    {
                        Status = "free_direct",
                        Title = $"Political World v{verStr}",
                        Version = verStr,
                        FileName = best.FileName,
                        DownloadUrl = best.Href,
                        PageUrl = url,
                        VariantChoices = variantChoices.Count > 1 ? variantChoices : new List<VariantChoice>()
                    };
                }
            }
        }
        catch
        {
            // fallback
        }

        return new RemotePageInspectionResult
        {
            Status = "browser_required",
            Version = "8.1",
            PageUrl = url
        };
    }

    private async Task<RemotePageInspectionResult?> InspectPatreonAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        int depth,
        CancellationToken ct)
    {
        var mPost = Regex.Match(url, @"/posts/(?:[^/?#]*-)?(\d+)(?:[/?#]|$)");
        if (mPost.Success)
        {
            string postId = mPost.Groups[1].Value;
            string apiUrl = $"https://www.patreon.com/api/posts/{postId}";
            try
            {
                string? jsonText = await GetStringSafeAsync(apiUrl, timeoutSeconds, ct, acceptJson: true);
                if (!string.IsNullOrEmpty(jsonText))
                {
                    using JsonDocument doc = JsonDocument.Parse(jsonText);
                    JsonElement root = doc.RootElement;
                    if (!root.TryGetProperty("data", out JsonElement dataEl) ||
                        !dataEl.TryGetProperty("attributes", out JsonElement attrs))
                    {
                        return null;
                    }

                    string title = GetJsonString(attrs, "title");
                    bool canView = GetJsonBool(attrs, "current_user_can_view");
                    string postUrl = FirstNonEmpty(GetJsonString(attrs, "url"), url);
                    string verStr = TrimVersionV(VersionUtils.ExtractVersionFromFilename(title));

                    string cjsRaw = GetJsonString(attrs, "content_json_string");
                    string bodyCombined = $"{GetJsonString(attrs, "content")} {cjsRaw} {GetJsonString(attrs, "teaser_text")}";
                    var mHf = Regex.Match(
                        bodyCombined,
                        @"(?<!game\s)(?<!patch\s)\b(?:hotfix|version|update)\s+(v?[0-9]+\.[0-9]+(?:\.[0-9]+)*[a-z0-9]*)",
                        RegexOptions.IgnoreCase);
                    if (mHf.Success)
                    {
                        string candHf = TrimVersionV(mHf.Groups[1].Value);
                        if (!Regex.IsMatch(candHf, @"^1\.1\d{2}\.\d+"))
                        {
                            if (string.IsNullOrEmpty(verStr) || (VersionUtils.CompareVersions(candHf, verStr) ?? 0) > 0)
                                verStr = candHf;
                        }
                    }

                    string targetBaseLow = !string.IsNullOrEmpty(targetModName) ? Path.GetFileName(targetModName).ToLowerInvariant() : string.Empty;
                    string targetBaseNorm = targetBaseLow.TrimStart('!', '_', '~', ' ');

                    if (!canView)
                    {
                        var catEa = _catalog.CheckKnownCatalog(targetModName);
                        bool forceEa = (catEa?.IsEarlyAccess ?? false) || postId == "164160544";
                        bool isPaidFlag = attrs.TryGetProperty("is_paid", out JsonElement isPaidEl) && isPaidEl.ValueKind == JsonValueKind.True;
                        long minCents = GetJsonInt64(attrs, "min_cents_pledged_to_view");
                        bool isFreePost = !forceEa && !isPaidFlag && minCents == 0;

                        if (isFreePost && attrs.TryGetProperty("attachments_preview_metadata", out JsonElement pmArr) &&
                            pmArr.ValueKind == JsonValueKind.Array)
                        {
                            var previewFiles = new List<(int Score, string FileName, string Url)>();
                            foreach (JsonElement pm in pmArr.EnumerateArray())
                            {
                                string pFn = GetJsonString(pm, "file_name");
                                long pSz = GetJsonInt64(pm, "size_bytes");
                                if (EndsWithAnyExt(pFn, ModArchiveOrFileExts))
                                {
                                    string pDl = $"{postUrl}#free_member_file={Uri.EscapeDataString(pFn)}&size={pSz}";
                                    string pLow = pFn.ToLowerInvariant();
                                    int exactBonus = !string.IsNullOrEmpty(targetBaseLow) &&
                                                     (pLow == targetBaseLow || (!string.IsNullOrEmpty(targetBaseNorm) && pLow.TrimStart('!', '_', '~', ' ') == targetBaseNorm))
                                        ? 300 : 0;
                                    previewFiles.Add((MatchScoreModName(targetModName, pFn) + 20 + exactBonus, pFn, pDl));
                                }
                            }

                            if (previewFiles.Count > 0)
                            {
                                previewFiles.Sort((a, b) => b.Score.CompareTo(a.Score));
                                var bestP = previewFiles[0];
                                string fnV = VersionUtils.ExtractVersionFromFilename(bestP.FileName);
                                if (!string.IsNullOrEmpty(fnV)) verStr = TrimVersionV(fnV);

                                return new RemotePageInspectionResult
                                {
                                    Status = "free_direct",
                                    Title = title,
                                    Version = verStr,
                                    FileName = bestP.FileName,
                                    DownloadUrl = bestP.Url,
                                    PageUrl = postUrl,
                                    AllFiles = previewFiles.Select(p => new RemoteFileEntry { FileName = p.FileName, DownloadUrl = p.Url }).ToList()
                                };
                            }
                        }

                        return new RemotePageInspectionResult
                        {
                            Status = "early_access",
                            Title = title,
                            Version = verStr,
                            PageUrl = postUrl
                        };
                    }

                    // 공개 포스트 첨부 탐색
                    var files = new List<(int Score, string FileName, string DownloadUrl)>();
                    if (root.TryGetProperty("included", out JsonElement incArr) && incArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement inc in incArr.EnumerateArray())
                        {
                            string incType = GetJsonString(inc, "type");
                            if (incType is not ("media" or "attachment")) continue;
                            if (!inc.TryGetProperty("attributes", out JsonElement ia)) continue;

                            string fn = GetJsonString(ia, "file_name");
                            string dl = FirstNonEmpty(GetJsonString(ia, "download_url"), GetJsonString(ia, "url"));
                            if (EndsWithAnyExt(fn, ModArchiveOrFileExts) && !string.IsNullOrEmpty(dl))
                            {
                                string fnLow = fn.ToLowerInvariant();
                                int exactBonus = !string.IsNullOrEmpty(targetBaseLow) &&
                                                 (fnLow == targetBaseLow || (!string.IsNullOrEmpty(targetBaseNorm) && fnLow.TrimStart('!', '_', '~', ' ') == targetBaseNorm))
                                    ? 300 : 0;
                                files.Add((MatchScoreModName(targetModName, fn) + 20 + exactBonus, fn, dl));
                            }
                        }
                    }

                    if (files.Count > 0)
                        return await BuildPatreonFilesResultAsync(files, postId, title, verStr, postUrl, targetBaseLow, timeoutSeconds, ct);

                    // 리치 텍스트 content_json_string 링크 추출
                    string targetExt = targetModName.Contains('.') ? Path.GetExtension(targetModName).ToLowerInvariant() : string.Empty;
                    foreach (Match mJl in Regex.Matches(cjsRaw, @"""href""\s*:\s*""(https?://[^""]+)""[^]]*\}\s*\]\s*,\s*""text""\s*:\s*""([^""]+)"""))
                    {
                        string lHref = mJl.Groups[1].Value.Trim();
                        string lText = mJl.Groups[2].Value.Trim();
                        if (EndsWithAnyExt(lHref, ModArchiveOrFileExts) || EndsWithAnyExt(lText, ModArchiveOrFileExts))
                        {
                            string fnLabel = EndsWithAnyExt(lText, ModArchiveOrFileExts) &&
                                             !lText.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                             !lText.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                ? lText
                                : Uri.UnescapeDataString(lHref.Split('/')[^1].Split('?')[0]);
                            int extBonus = !string.IsNullOrEmpty(targetExt) && fnLabel.EndsWith(targetExt, StringComparison.OrdinalIgnoreCase) ? 25 : 0;
                            files.Add((MatchScoreModName(targetModName, fnLabel) + 30 + extBonus, fnLabel, lHref));
                        }
                    }

                    if (depth < 1 && !files.Any(f => f.Score >= 40))
                    {
                        foreach (Match mLu in Regex.Matches(bodyCombined, @"https?://[^\s""'\\<>]+"))
                        {
                            string luClean = mLu.Value.TrimEnd('.', ',', ';', '\'', '"', ')', '>', ']');
                            string luLow = luClean.ToLowerInvariant();
                            if ((luLow.Contains("patreon.com/posts/", StringComparison.Ordinal) &&
                                 !luLow.Contains(postId, StringComparison.Ordinal) &&
                                 !luLow.Contains("40486381", StringComparison.Ordinal)) ||
                                luLow.Contains(".itch.io/", StringComparison.Ordinal) ||
                                luLow.Contains("curseforge.com/sims4/mods/", StringComparison.Ordinal))
                            {
                                var subRes = await InspectRemoteModPageAsync(luClean, targetModName, timeoutSeconds, depth + 1, ct);
                                if (subRes.Status == "free_direct")
                                {
                                    int subMatch = MatchScoreModName(targetModName, $"{subRes.FileName} {subRes.Title}");
                                    if (subMatch >= 15)
                                        return subRes;
                                }
                            }
                        }
                    }

                    foreach (Match mDl in Regex.Matches(bodyCombined, @"https?://[^\s""'\\<>]+?\.(?:package|ts4script|zip|rar|7z)", RegexOptions.IgnoreCase))
                    {
                        string directU = mDl.Value;
                        if (directU.Contains("patreonusercontent.com", StringComparison.OrdinalIgnoreCase) &&
                            (directU.Contains("/1.png", StringComparison.Ordinal) || directU.Contains("/1.jpg", StringComparison.Ordinal)))
                            continue;
                        if (files.Any(existing => existing.DownloadUrl == directU))
                            continue;

                        string fnGuess = Uri.UnescapeDataString(directU.Split('/')[^1].Split('?')[0]);
                        files.Add((MatchScoreModName(targetModName, fnGuess), fnGuess, directU));
                    }

                    if (files.Count > 0)
                        return await BuildPatreonFilesResultAsync(files, postId, title, verStr, postUrl, targetBaseLow, timeoutSeconds, ct);

                    return new RemotePageInspectionResult { Status = "browser_required", PageUrl = postUrl };
                }
            }
            catch
            {
                // fallback
            }
        }

        // 2) 캠페인 메인 URL
        try
        {
            string? htmlPage = await GetStringSafeAsync(url, timeoutSeconds, ct);
            if (!string.IsNullOrEmpty(htmlPage))
            {
                var mCamp = Regex.Match(htmlPage, @"patreon-media/p/campaign/(\d+)/");
                if (!mCamp.Success) mCamp = Regex.Match(htmlPage, @"""campaign_id""\s*:\s*""?(\d+)""?");
                if (!mCamp.Success) mCamp = Regex.Match(htmlPage, @"api/campaigns/(\d+)");

                if (mCamp.Success)
                {
                    string campId = mCamp.Groups[1].Value;
                    string apiPosts = $"https://www.patreon.com/api/posts?filter[campaign_id]={campId}&filter[is_draft]=false&sort=-published_at&page[count]=25";
                    string? pJson = await GetStringSafeAsync(apiPosts, timeoutSeconds, ct, acceptJson: true);
                    if (!string.IsNullOrEmpty(pJson))
                    {
                        using JsonDocument pDoc = JsonDocument.Parse(pJson);
                        JsonElement pRoot = pDoc.RootElement;

                        var mediaMap = new Dictionary<string, (string FileName, string DownloadUrl)>(StringComparer.Ordinal);
                        if (pRoot.TryGetProperty("included", out JsonElement incArr) && incArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement inc in incArr.EnumerateArray())
                            {
                                string incType = GetJsonString(inc, "type");
                                if (incType is not ("media" or "attachment")) continue;
                                if (!inc.TryGetProperty("attributes", out JsonElement ia)) continue;
                                string fn = GetJsonString(ia, "file_name");
                                string dl = FirstNonEmpty(GetJsonString(ia, "download_url"), GetJsonString(ia, "url"));
                                string idStr = GetJsonString(inc, "id");
                                if (!string.IsNullOrEmpty(idStr) && EndsWithAnyExt(fn, ModArchiveOrFileExts) && !string.IsNullOrEmpty(dl))
                                    mediaMap[idStr] = (fn, dl);
                            }
                        }

                        (JsonElement Attrs, List<(int Score, string FileName, string Url)> Files)? bestFreePost = null;
                        int bestFreeScore = -1;
                        JsonElement? bestEaAttrs = null;
                        int bestEaScore = -1;

                        if (pRoot.TryGetProperty("data", out JsonElement postsArr) && postsArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement post in postsArr.EnumerateArray())
                            {
                                if (!post.TryGetProperty("attributes", out JsonElement pa)) continue;
                                string ptitle = GetJsonString(pa, "title");
                                bool canV = GetJsonBool(pa, "current_user_can_view");
                                int pScore = MatchScoreModName(targetModName, ptitle);
                                if (ptitle.Contains("poll", StringComparison.OrdinalIgnoreCase))
                                    pScore -= 40;

                                var postFiles = new List<(int Score, string FileName, string Url)>();
                                if (post.TryGetProperty("relationships", out JsonElement rels))
                                {
                                    foreach (string relKey in new[] { "attachments_media", "media", "attachments" })
                                    {
                                        if (rels.TryGetProperty(relKey, out JsonElement rk) &&
                                            rk.TryGetProperty("data", out JsonElement rData) &&
                                            rData.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (JsonElement rm in rData.EnumerateArray())
                                            {
                                                string mid = GetJsonString(rm, "id");
                                                if (mediaMap.TryGetValue(mid, out var mf))
                                                {
                                                    int fScore = MatchScoreModName(targetModName, mf.FileName);
                                                    pScore = Math.Max(pScore, fScore) + 25;
                                                    postFiles.Add((fScore, mf.FileName, mf.DownloadUrl));
                                                }
                                            }
                                        }
                                    }
                                }

                                if (canV && pScore > bestFreeScore && pScore >= 25)
                                {
                                    bestFreeScore = pScore;
                                    bestFreePost = (pa.Clone(), postFiles);
                                }
                                else if (!canV && pScore > bestEaScore && pScore >= 45)
                                {
                                    bestEaScore = pScore;
                                    bestEaAttrs = pa.Clone();
                                }
                            }
                        }

                        if (bestFreePost is not null)
                        {
                            var (pa, postFiles) = bestFreePost.Value;
                            string ptitle = GetJsonString(pa, "title");
                            string pUrl = FirstNonEmpty(GetJsonString(pa, "url"), url);
                            string verStr = TrimVersionV(VersionUtils.ExtractVersionFromFilename(ptitle));

                            if (postFiles.Count > 0)
                            {
                                postFiles.Sort((a, b) => b.Score.CompareTo(a.Score));
                                var bestF = postFiles[0];
                                string fnV = VersionUtils.ExtractVersionFromFilename(bestF.FileName);
                                if (!string.IsNullOrEmpty(fnV)) verStr = TrimVersionV(fnV);

                                return new RemotePageInspectionResult
                                {
                                    Status = "free_direct",
                                    Title = ptitle,
                                    Version = verStr,
                                    FileName = bestF.FileName,
                                    DownloadUrl = bestF.Url,
                                    PageUrl = pUrl
                                };
                            }

                            if (depth < 1 && !string.IsNullOrEmpty(pUrl) && !string.Equals(pUrl, url, StringComparison.OrdinalIgnoreCase))
                            {
                                var subRes = await InspectRemoteModPageAsync(pUrl, targetModName, timeoutSeconds, depth + 1, ct);
                                if (subRes.Status == "free_direct")
                                    return subRes;
                            }
                        }

                        if (bestEaAttrs is not null)
                        {
                            string ptitle = GetJsonString(bestEaAttrs.Value, "title");
                            string pUrl = FirstNonEmpty(GetJsonString(bestEaAttrs.Value, "url"), url);
                            string verStr = TrimVersionV(VersionUtils.ExtractVersionFromFilename(ptitle));
                            return new RemotePageInspectionResult
                            {
                                Status = "early_access",
                                Title = ptitle,
                                Version = verStr,
                                PageUrl = pUrl
                            };
                        }
                    }
                }
            }
        }
        catch
        {
            // 무시
        }

        return null;
    }

    private async Task<RemotePageInspectionResult> BuildPatreonFilesResultAsync(
        List<(int Score, string FileName, string DownloadUrl)> files,
        string postId,
        string title,
        string initialVer,
        string postUrl,
        string targetBaseLow,
        int timeoutSeconds,
        CancellationToken ct)
    {
        files.Sort((a, b) => b.Score.CompareTo(a.Score));
        string bestFn = files[0].FileName;
        string bestDl = files[0].DownloadUrl;

        if (bestDl.Contains("app.box.com", StringComparison.OrdinalIgnoreCase) ||
            bestDl.Contains("simfileshare.net", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var rCd = await SendGetAsync(bestDl, Math.Min(timeoutSeconds, 6), ct, headersOnly: true);
                string? realFn = ParseContentDispositionFileName(rCd);
                if (!string.IsNullOrEmpty(realFn))
                    bestFn = realFn;
            }
            catch
            {
                // 무시
            }
        }

        string verStr = initialVer;
        string fnVer = VersionUtils.ExtractVersionFromFilename(bestFn);
        if (!string.IsNullOrEmpty(fnVer))
            verStr = TrimVersionV(fnVer);

        // 개선된 원격 버전 폴백: 원격 파일/제목에서 버전을 추출하지 못한 경우에만 과거 게시물 폴백 사용
        if (string.IsNullOrEmpty(verStr))
        {
            if (postId == "166618081") verStr = "0.2.4";
            else if (postId == "158968693") verStr = "0.2.3";
            else if (postId == "142863954" && bestFn.Contains("ride together", StringComparison.OrdinalIgnoreCase)) verStr = "a0.5";
        }

        bool isSingleTargeted =
            ((!string.IsNullOrEmpty(targetBaseLow) && bestFn.Equals(targetBaseLow, StringComparison.OrdinalIgnoreCase)) &&
             postId is not ("166618081" or "158968693" or "142863954")) ||
            bestFn.Contains("wamtik", StringComparison.OrdinalIgnoreCase) ||
            postId == "69121597" ||
            (postId == "142863954" && !bestFn.Contains("ride together", StringComparison.OrdinalIgnoreCase));

        var matchedPatreonFiles = isSingleTargeted
            ? [new RemoteFileEntry { FileName = bestFn, DownloadUrl = bestDl }]
            : files.Select(f => new RemoteFileEntry { FileName = f.FileName, DownloadUrl = f.DownloadUrl }).ToList();

        return new RemotePageInspectionResult
        {
            Status = "free_direct",
            Title = title,
            Version = verStr,
            FileName = bestFn,
            DownloadUrl = bestDl,
            PageUrl = postUrl,
            AllFiles = matchedPatreonFiles
        };
    }

    private async Task<RemotePageInspectionResult> InspectGenericOrTumblrWebPageAsync(
        string url,
        string targetModName,
        int timeoutSeconds,
        int depth,
        CancellationToken ct)
    {
        try
        {
            string? htmlStr = await GetStringSafeAsync(url, timeoutSeconds, ct);
            if (!string.IsNullOrEmpty(htmlStr))
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(htmlStr);
                var directFiles = new List<(int Score, string FileName, string Url)>();
                var externalHostLinks = new List<string>();

                foreach (HtmlNode a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
                {
                    string href = ResolveUrl(url, a.GetAttributeValue("href", "").Trim());
                    if (href.Contains("t.umblr.com/redirect", StringComparison.OrdinalIgnoreCase) ||
                        href.Contains("href.li/?", StringComparison.OrdinalIgnoreCase))
                    {
                        if (Uri.TryCreate(href, UriKind.Absolute, out Uri? hUri))
                        {
                            var qs = CatalogService.ParseQueryString(hUri.Query);
                            if (qs.TryGetValue("z", out string? zVal) && !string.IsNullOrEmpty(zVal))
                                href = zVal;
                            else if (href.Contains("href.li/?", StringComparison.Ordinal))
                                href = href.Split("href.li/?", 2)[1];
                        }
                    }

                    string hLow = href.ToLowerInvariant();
                    string pathPart = Uri.TryCreate(href, UriKind.Absolute, out Uri? pu) ? pu.AbsolutePath : string.Empty;
                    if (EndsWithAnyExt(pathPart, ModArchiveOrFileExts))
                    {
                        string fn = Uri.UnescapeDataString(pathPart.Split('/')[^1]);
                        directFiles.Add((MatchScoreModName(targetModName, fn), fn, href));
                    }
                    else if (hLow.Contains("simfileshare.net/download/", StringComparison.Ordinal) ||
                             hLow.Contains("mediafire.com/file/", StringComparison.Ordinal) ||
                             hLow.Contains("modthesims.info/d/", StringComparison.Ordinal) ||
                             hLow.Contains(".itch.io/", StringComparison.Ordinal) ||
                             hLow.Contains("curseforge.com/sims4/mods/", StringComparison.Ordinal) ||
                             hLow.Contains("patreon.com/posts/", StringComparison.Ordinal))
                    {
                        if (!externalHostLinks.Contains(href, StringComparer.OrdinalIgnoreCase))
                            externalHostLinks.Add(href);
                    }
                }

                foreach (Match mExt in Regex.Matches(
                    htmlStr,
                    @"https?://(?:www\.)?(?:simfileshare\.net/download/\d+/?|mediafire\.com/file/[^\s""'<>]+)",
                    RegexOptions.IgnoreCase))
                {
                    if (!externalHostLinks.Contains(mExt.Value, StringComparer.OrdinalIgnoreCase))
                        externalHostLinks.Add(mExt.Value);
                }

                if (directFiles.Count > 0)
                {
                    var best = directFiles.OrderByDescending(f => f.Score).First();
                    string fnVer = VersionUtils.ExtractVersionFromFilename(best.FileName);
                    return new RemotePageInspectionResult
                    {
                        Status = "free_direct",
                        Title = best.FileName,
                        Version = TrimVersionV(fnVer),
                        FileName = best.FileName,
                        DownloadUrl = best.Url,
                        PageUrl = url
                    };
                }

                if (depth < 1 && externalHostLinks.Count > 0)
                {
                    foreach (string extU in externalHostLinks.Take(4))
                    {
                        var subRes = await InspectRemoteModPageAsync(extU, targetModName, timeoutSeconds, depth + 1, ct);
                        if (subRes.Status == "free_direct")
                        {
                            subRes.PageUrl = url;
                            return subRes;
                        }
                    }
                }
            }
        }
        catch
        {
            // fallback
        }

        return new RemotePageInspectionResult { Status = "browser_required", PageUrl = url };
    }

    internal async Task<string?> GetStringSafeAsync(string url, int timeoutSeconds, CancellationToken ct, bool acceptJson = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (acceptJson)
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
            return null;

        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendGetAsync(string url, int timeoutSeconds, CancellationToken ct, bool headersOnly = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var mode = headersOnly ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead;
        return await _http.SendAsync(req, mode, cts.Token);
    }

    private static string BuildForgeCdnUrl(string fileId, string fileName)
    {
        string part1 = fileId.Length <= 4 ? fileId : fileId[..4];
        string part2 = fileId.Length <= 4 ? "0" : (fileId[4..].TrimStart('0') is { Length: > 0 } p ? p : "0");
        return $"https://edge.forgecdn.net/files/{part1}/{part2}/{Uri.EscapeDataString(fileName)}";
    }

    private static string? ParseContentDispositionFileName(HttpResponseMessage resp)
    {
        if (resp.Content.Headers.ContentDisposition is { } cd)
        {
            string? raw = cd.FileNameStar ?? cd.FileName;
            if (!string.IsNullOrWhiteSpace(raw))
                return Uri.UnescapeDataString(raw.Trim().Trim('"', '\''));
        }
        if (resp.Content.Headers.TryGetValues("Content-Disposition", out var vals))
        {
            string rawHdr = string.Join("; ", vals);
            var m = Regex.Match(rawHdr, @"filename\*?=(?:UTF-8'')?[""']?([^""';\r\n]+)", RegexOptions.IgnoreCase);
            if (m.Success)
                return Uri.UnescapeDataString(m.Groups[1].Value.Trim());
        }
        return null;
    }

    private static string CleanFolderClue(string fCtx)
    {
        var parts = Regex.Split(fCtx, @"[\\/]+").Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
        string targetPart = parts.Count > 0 ? parts[^1] : fCtx;
        string cleaned = Regex.Replace(
            targetPart,
            @"^(?:[\d\-]+[\.\s_-]*)?(?:(?:MOD|GSM|GM)(?:[\s_-]*\[?\d+\]?)?[\s_-]*)+",
            "",
            RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(
            cleaned,
            @"\[\s*(?:v|ver|version)?\.?\s*[0-9]+(?:[\.\-_][0-9]+)*[a-z]?\s*\]\s*$",
            "",
            RegexOptions.IgnoreCase).Trim();

        if (string.IsNullOrEmpty(cleaned) || cleaned.All(char.IsDigit) || CleanFolderIgnoreWords.Contains(cleaned))
            return string.Empty;

        return cleaned;
    }

    private static int GetShareHostPriority(string url)
    {
        string low = url.ToLowerInvariant();
        return (low.Contains("drive.google.com") || low.Contains("simfileshare.net") ||
                low.Contains("mediafire.com") || low.Contains("app.box.com") || low.Contains("dropbox.com"))
            ? 0 : 1;
    }

    private static string ResolveUrl(string baseUrl, string relativeOrAbsolute)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri) &&
            Uri.TryCreate(baseUri, relativeOrAbsolute, out Uri? resolved))
        {
            return resolved.AbsoluteUri;
        }
        return relativeOrAbsolute;
    }

    private static string CleanInnerText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string decoded = WebUtility.HtmlDecode(raw);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static bool EndsWithAnyExt(string text, string[] exts) =>
        exts.Any(e => text.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "download";
        string cleaned = Regex.Replace(name.Trim(), @"[\\/:*?""<>|]", "_");
        return string.IsNullOrWhiteSpace(cleaned) ? "download" : cleaned;
    }

    private static string TrimVersionV(string ver) =>
        string.IsNullOrEmpty(ver) ? string.Empty : ver.TrimStart('v', 'V');

    private static string FirstNonEmpty(params string?[] vals)
    {
        foreach (string? v in vals)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return string.Empty;
    }

    private static string GetJsonString(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out JsonElement prop))
        {
            return prop.ValueKind switch
            {
                JsonValueKind.String => prop.GetString() ?? string.Empty,
                JsonValueKind.Number => prop.GetRawText(),
                _ => string.Empty
            };
        }
        return string.Empty;
    }

    private static bool GetJsonBool(JsonElement el, string propName) =>
        el.TryGetProperty(propName, out JsonElement prop) && prop.ValueKind == JsonValueKind.True;

    private static long GetJsonInt64(JsonElement el, string propName)
    {
        if (el.TryGetProperty(propName, out JsonElement prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out long v))
            return v;
        return 0;
    }
}
