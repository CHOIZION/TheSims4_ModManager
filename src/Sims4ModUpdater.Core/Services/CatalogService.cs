using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 내장 공식 모드 카탈로그(80개+) 및 신뢰 배포 도메인 판별·URL 정규화 서비스
/// </summary>
public sealed class CatalogService
{
    public static readonly IReadOnlyList<(string Domain, string Label)> ReliableDomains =
    [
        ("curseforge.com", "CurseForge"),
        ("patreon.com", "Patreon"),
        ("itch.io", "Itch.io"),
        ("modthesims.info", "ModTheSims"),
        ("simfileshare.net", "SimFileShare"),
        ("tumblr.com", "Tumblr"),
        ("postype.com", "포스타입 (Postype)"),
        ("blog.naver.com", "네이버 블로그 참고 (무료)"),
        ("dcinside.com", "디시인사이드"),
        ("drive.google.com", "Google Drive"),
        ("scumbumbomods.com", "Scumbumbo Mods"),
        ("wonderfulwhims.com", "WonderfulWhims"),
        ("deaderpool-mccc.com", "Deaderpool MCCC"),
        ("sims4ai.virtualconscious.com", "Sims 4 AI Official"),
        ("github.com", "GitHub"),
        ("sims4studio.com", "Sims 4 Studio"),
        ("seebee.de", "ichschaf Sims 4 Mods"),
        ("kemzimamods.com", "KemZima Mods"),
        ("pandasama.com", "Pandasama Official"),
        ("lot51.cc", "Lot 51"),
        ("twistedmexi.com", "TwistedMexi")
    ];

    public static readonly HashSet<string> IgnoredRootUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        "https://www.patreon.com",
        "https://patreon.com",
        "https://www.curseforge.com",
        "https://curseforge.com",
        "https://www.curseforge.com/sims4",
        "https://www.curseforge.com/sims4/mods",
        "https://postype.com",
        "https://www.postype.com",
        "https://github.com",
        "https://www.github.com",
        "https://www.tumblr.com",
        "https://tumblr.com",
        "https://modthesims.info",
        "https://www.modthesims.info",
        "https://db.modthesims.info",
        "https://itch.io",
        "https://www.dcinside.com",
        "https://dcinside.com",
        "https://gall.dcinside.com",
        "https://search.dcinside.com"
    };

    public static readonly string[] IgnoredInternalUrlPrefixes =
    [
        "http://www.w3.org",
        "https://www.w3.org",
        "http://purl.org",
        "https://purl.org",
        "http://adobe.com",
        "https://adobe.com",
        "http://ns.adobe.com",
        "https://ns.adobe.com",
        "https://integrate.api.nvidia.com",
        "https://generativelanguage.googleapis.com",
        "https://api.openai.com",
        "https://api.cloudflare.com",
        "https://router.huggingface.co",
        "http://127.0.0.1",
        "http://localhost",
        "https://simsafterdark.com",
        "https://discord.gg",
        "https://discord.com",
        "https://support.patreon.com",
        "https://frankkmods.com",
        "http://wonderf.cluster026.hosting.ovh.net",
        "http://twistedmexi.com/betterexceptions/knownexceptions.php",
        "https://twistedmexi.com/betterexceptions/knownexceptions.php",
        "https://mail.python.org",
        "http://mail.python.org",
        "https://bugs.python.org",
        "http://bugs.python.org",
        "https://docs.python.org",
        "http://docs.python.org",
        "https://www.python.org",
        "http://www.python.org",
        "https://peps.python.org"
    ];

    public static readonly HashSet<string> GenericInternalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__init__", "injector", "config", "enums", "commands", "reload", "utils",
        "version", "patches", "snippets", "main", "core", "common", "settings",
        "tuning", "helpers", "elements", "ctypes", "services", "sims4", "llamalogic",
        "modfilemanifest", "xml_injector", "_do_not_unzip_", "test", "tests"
    };

    private static readonly Regex NonAlnumKoRegex = new(@"[^a-zA-Z0-9가-힣]", RegexOptions.Compiled);

    private readonly List<CatalogItem> _items;

    public CatalogService()
    {
        _items = LoadEmbeddedCatalog();
    }

    public CatalogService(IEnumerable<CatalogItem> items)
    {
        _items = items.ToList();
    }

    public IReadOnlyList<CatalogItem> Items => _items;

    public CatalogMatchResult? CheckKnownCatalog(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return null;

        string nameLower = rawName.ToLowerInvariant();
        string clean = NonAlnumKoRegex.Replace(nameLower, "");

        foreach (CatalogItem item in _items)
        {
            string itemClean = NonAlnumKoRegex.Replace(item.Name.ToLowerInvariant(), "");
            bool matched = !string.IsNullOrEmpty(itemClean) && string.Equals(clean, itemClean, StringComparison.Ordinal);

            if (!matched)
            {
                foreach (string kw in item.Keywords)
                {
                    string kwLower = kw.ToLowerInvariant();
                    string cleanKw = NonAlnumKoRegex.Replace(kwLower, "");
                    if (nameLower.Contains(kwLower, StringComparison.Ordinal) ||
                        (cleanKw.Length >= 6 && clean.Contains(cleanKw, StringComparison.Ordinal)))
                    {
                        matched = true;
                        break;
                    }
                }
            }

            if (matched)
            {
                if (item.Links.Count == 0)
                    continue;

                return new CatalogMatchResult
                {
                    Found = true,
                    PrimaryUrl = item.Links[0].Url,
                    SourceName = item.Links[0].Label,
                    CandidateLinks = item.Links.ToList(),
                    MatchedCatalog = item.Name,
                    Creator = item.Creator,
                    ModTitle = string.IsNullOrWhiteSpace(item.ModTitle) ? item.Name : item.ModTitle,
                    OverrideModTitle = item.OverrideModTitle,
                    Version = item.Version,
                    IsPaid = item.IsPaid,
                    IsEarlyAccess = item.IsEarlyAccess,
                    ReleaseNotes = item.ReleaseNotes,
                    Query = rawName
                };
            }
        }

        return null;
    }

    public static string LabelForUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "공식 배포 페이지";

        string lower = url.ToLowerInvariant();
        foreach (var (domain, label) in ReliableDomains)
        {
            if (lower.Contains(domain, StringComparison.Ordinal))
                return label;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host;

        return "공식 배포 페이지";
    }

    public static string NormalizeDistributionUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        string cleaned = url.Trim().TrimEnd('.', ',', ';', '\'', '"', ')', '>', ']');
        string lower = cleaned.ToLowerInvariant();

        if (lower.Contains("dcinside.com", StringComparison.Ordinal))
        {
            if (Uri.TryCreate(cleaned, UriKind.Absolute, out Uri? dcUri))
            {
                var queryParams = ParseQueryString(dcUri.Query);
                queryParams.TryGetValue("id", out string? gid);
                queryParams.TryGetValue("no", out string? no);
                if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(no))
                    return $"{dcUri.Scheme}://{dcUri.Host}{dcUri.AbsolutePath}?id={gid}&no={no}";
            }
            return cleaned.Split('?')[0].TrimEnd('/');
        }

        if (lower.Contains("kemzimamods.com/", StringComparison.Ordinal) ||
            lower.Contains("simfileshare.net/", StringComparison.Ordinal))
        {
            string basePart = cleaned.Split('?')[0].TrimEnd('/');
            return $"{basePart}/";
        }

        if (lower.Contains("drive.google.com", StringComparison.Ordinal))
        {
            var mGd = Regex.Match(cleaned, @"/file/d/([a-zA-Z0-9_-]+)");
            if (mGd.Success)
                return $"https://drive.google.com/file/d/{mGd.Groups[1].Value}/view";
            return cleaned;
        }

        cleaned = cleaned.Split('?')[0].TrimEnd('/');
        cleaned = Regex.Replace(cleaned, @"/sitemap/?$", "", RegexOptions.IgnoreCase);

        if (cleaned.Contains("curseforge.com/sims4/mods/", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = Regex.Replace(
                cleaned,
                @"(/sims4/mods/[^/?#]+)/(?:files|gallery|relations|issues)(?:/.*)?$",
                "$1",
                RegexOptions.IgnoreCase);
        }

        return cleaned;
    }

    public static bool IsValidInternalUrl(string url, string fileName)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length < 14 ||
            (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
             !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string lower = url.ToLowerInvariant();
        if (IgnoredInternalUrlPrefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal)))
            return false;

        if (IgnoredRootUrls.Contains(lower.TrimEnd('/')))
            return false;

        if (lower.Contains("scumbumbomods.com/xml-injector", StringComparison.Ordinal) ||
            lower.Contains("curseforge.com/sims4/mods/xml-injector", StringComparison.Ordinal))
        {
            string fnLower = (fileName ?? string.Empty).ToLowerInvariant();
            if (!fnLower.Contains("xml", StringComparison.Ordinal) &&
                !fnLower.Contains("injector", StringComparison.Ordinal))
                return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               !string.IsNullOrEmpty(uri.Host) &&
               uri.Host.Contains('.', StringComparison.Ordinal);
    }

    public static Dictionary<string, string> ParseQueryString(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
            return dict;

        string trimmed = query.TrimStart('?');
        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            string k = Uri.UnescapeDataString(pair[..idx]);
            string v = Uri.UnescapeDataString(pair[(idx + 1)..]);
            dict[k] = v;
        }
        return dict;
    }

    private static List<CatalogItem> LoadEmbeddedCatalog()
    {
        var assembly = typeof(CatalogService).Assembly;
        string? resName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("known_catalog.json", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(resName))
        {
            using Stream? stream = assembly.GetManifestResourceStream(resName);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string json = reader.ReadToEnd();
                return JsonSerializer.Deserialize<List<CatalogItem>>(json) ?? new List<CatalogItem>();
            }
        }

        string diskPath = Path.Combine(AppContext.BaseDirectory, "Data", "known_catalog.json");
        if (File.Exists(diskPath))
        {
            string json = File.ReadAllText(diskPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<CatalogItem>>(json) ?? new List<CatalogItem>();
        }

        return new List<CatalogItem>();
    }
}
