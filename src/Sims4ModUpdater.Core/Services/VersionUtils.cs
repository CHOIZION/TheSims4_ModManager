using System.Globalization;
using System.Text.RegularExpressions;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Core.Services;

/// <summary>
/// 심즈 4 모드 파일명·폴더명·원격 제목에서 날짜 및 버전 토큰을 추출하고 비교하는 유틸리티
/// (Semantic, 날짜, MCCC 2026.x.y, 알파벳 접미사 v60a/1.5b, 통합한글화 V98G8/P8/B8, 생략된 점 v42 vs v4.2 지원)
/// </summary>
public static partial class VersionUtils
{
    public static readonly Regex BarambyulPattern = new(
        @"^V(\d+)([GBPR])(\d+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StripModExtRegex = new(
        @"\.(?:package|ts4script|zip|rar|7z)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Ymd4Regex = new(
        @"(?:^|[\s_\-\[\(])(201[4-9]|202[0-9])[\.\-_](0[1-9]|1[0-2])[\.\-_](0[1-9]|[12][0-9]|3[01])(?=$|[^0-9])",
        RegexOptions.Compiled);

    private static readonly Regex Dmy4Regex = new(
        @"(?:^|[\s_\-\[\(])([0-3][0-9])[\.\-_]([0-3][0-9])[\.\-_](201[4-9]|202[0-9])(?=$|[^0-9])",
        RegexOptions.Compiled);

    private static readonly Regex TwoYearDateRegex = new(
        @"(?:^|[\s_\-\[\(])([0-3][0-9])[\.\-_]([0-3][0-9])[\.\-_]([0-3][0-9])(?=$|[^0-9.])",
        RegexOptions.Compiled);

    private static readonly Regex Compact8DateRegex = new(
        @"(?:^|[\s_\-\[\(])(201[4-9]|202[0-9])(0[1-9]|1[0-2])(0[1-9]|[12][0-9]|3[01])(?=$|[^0-9])",
        RegexOptions.Compiled);

    private static readonly Regex Compact6DateRegex = new(
        @"(?:[\[\(_\-])(2[0-9])(0[1-9]|1[0-2])(0[1-9]|[12][0-9]|3[01])(?=$|[\]\)\s_\-])",
        RegexOptions.Compiled);

    private static readonly Regex ExplicitVRegex = new(
        @"(?:^|[\s_\-\.\[\(]|(?<=[a-zA-Z]))(?:v|ver|version)\.?\s*([0-9]+(?:\.[0-9]+)*[a-z]?)(?=$|[^a-zA-Z0-9.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InlineBarambyulRegex = new(
        @"(?:^|[\s_\-\[\(])(V[0-9]+[GBPR][0-9]+)(?=$|[^a-zA-Z0-9.])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DottedVersionRegex = new(
        @"(?:^|[\s_\-\[\(])[ab]?([0-9]+\.[0-9]+(?:\.[0-9]+)*[a-wyz]?)(?=$|[^a-zA-Z0-9.xX가-힣])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlainOrderFolderRegex = new(
        @"^(?:[\d\-]+[\.\s_\-]*)?(?:GSM|GM|MOD)[\s_\-]*\[\s*\d+\s*\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BracketEndVersionRegex = new(
        @"\[\s*(?:v|ver|version)?\.?\s*([0-9]+(?:\.[0-9]+)*[a-z]?)\s*\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 파일명·폴더명 또는 제목에 날짜만 표기된 경우 표준 날짜('YYYY.MM.DD')로 변환해 반환합니다.
    /// </summary>
    public static string ExtractDateFromFilename(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // 1) YYYY.MM.DD / YYYY-MM-DD / YYYY_MM_DD (2자리 0패딩)
        var mYmd4 = Ymd4Regex.Match(text);
        if (mYmd4.Success)
            return $"{mYmd4.Groups[1].Value}.{mYmd4.Groups[2].Value}.{mYmd4.Groups[3].Value}";

        // 2) DD.MM.YYYY 또는 MM.DD.YYYY
        var mDmy4 = Dmy4Regex.Match(text);
        if (mDmy4.Success)
        {
            int n1 = int.Parse(mDmy4.Groups[1].Value, CultureInfo.InvariantCulture);
            int n2 = int.Parse(mDmy4.Groups[2].Value, CultureInfo.InvariantCulture);
            string yyyy = mDmy4.Groups[3].Value;
            if (n2 is >= 1 and <= 12 && n1 is >= 1 and <= 31)
                return $"{yyyy}.{n2:D2}.{n1:D2}";
            if (n1 is >= 1 and <= 12 && n2 is >= 1 and <= 31)
                return $"{yyyy}.{n1:D2}.{n2:D2}";
        }

        // 3) 2자리 연도 날짜: YY.MM.DD / DD.MM.YY / MM.DD.YY
        foreach (Match m2y in TwoYearDateRegex.Matches(text))
        {
            int a = int.Parse(m2y.Groups[1].Value, CultureInfo.InvariantCulture);
            int b = int.Parse(m2y.Groups[2].Value, CultureInfo.InvariantCulture);
            int c = int.Parse(m2y.Groups[3].Value, CultureInfo.InvariantCulture);
            if (b is >= 1 and <= 12)
            {
                if ((a > 29 || a < 14) && c is >= 14 and <= 29 && a is >= 1 and <= 31)
                    return $"20{c:D2}.{b:D2}.{a:D2}";
                if (a is >= 14 and <= 29 && c is >= 1 and <= 31)
                    return $"20{a:D2}.{b:D2}.{c:D2}";
            }
            if (a is >= 1 and <= 12 && b is >= 13 and <= 31 && c is >= 14 and <= 29)
                return $"20{c:D2}.{a:D2}.{b:D2}";
        }

        // 4) YYYYMMDD 8자리
        var m8 = Compact8DateRegex.Match(text);
        if (m8.Success)
            return $"{m8.Groups[1].Value}.{m8.Groups[2].Value}.{m8.Groups[3].Value}";

        // 5) 대괄호/구분자 뒤 6자리 YYMMDD
        var m6 = Compact6DateRegex.Match(text);
        if (m6.Success)
            return $"20{m6.Groups[1].Value}.{m6.Groups[2].Value}.{m6.Groups[3].Value}";

        return string.Empty;
    }

    /// <summary>
    /// 파일명·폴더명·제목에서 버전 토큰을 추출합니다. 없으면 빈 문자열 반환.
    /// </summary>
    public static string ExtractVersionFromFilename(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            return string.Empty;

        string stem = StripModExtRegex.Replace(filename.Trim(), "");

        // 1) 명시적 v / ver / version 접두사
        var mv = ExplicitVRegex.Match(stem);
        if (mv.Success)
        {
            string rawV = mv.Groups[1].Value.TrimStart('v', 'V');
            return $"v{rawV}";
        }

        // 1-b) 바람별 스타일 버전 (예: V98G8, V98B8, V98P8)
        var mvg = InlineBarambyulRegex.Match(stem);
        if (mvg.Success)
            return mvg.Groups[1].Value.ToUpperInvariant();

        // 2) 날짜 형식 버전
        string dateVer = ExtractDateFromFilename(stem);
        if (!string.IsNullOrEmpty(dateVer))
            return dateVer;

        // 3) 점이 포함된 일반 버전 숫자 (a0.2.4 포함, 1.75x 배수 표기 제외)
        var mDot = DottedVersionRegex.Match(stem);
        if (mDot.Success)
        {
            return $"v{mDot.Groups[1].Value}";
        }

        // 4) 끝에 대괄호로 표기된 버전 (GSM [1] 등 순번 제외)
        if (!PlainOrderFolderRegex.IsMatch(stem))
        {
            var mBracketEnd = BracketEndVersionRegex.Match(stem);
            if (mBracketEnd.Success)
                return $"v{mBracketEnd.Groups[1].Value}";
        }

        return string.Empty;
    }

    /// <summary>
    /// 버전 문자열 정규화 표기 (예: '60a' -> 'v60a', 'V98G8' -> 'V98G8', '2026.03.15' -> '2026.03.15')
    /// </summary>
    public static string FormatVersionToken(string? rawVersion)
    {
        string mVer = (rawVersion ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(mVer))
            return string.Empty;

        if (Regex.IsMatch(mVer, @"^20\d{2}\.\d{2}\.\d{2}$"))
            return mVer;

        if (BarambyulPattern.IsMatch(mVer))
            return mVer.ToUpperInvariant();

        return mVer.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? mVer : $"v{mVer}";
    }

    /// <summary>
    /// 두 버전 토큰을 비교합니다.
    /// 반환값: 음수(a &lt; b), 0(a == b), 양수(a &gt; b). 비교 불가시 null.
    /// </summary>
    public static int? CompareVersions(string? leftVersion, string? rightVersion)
    {
        if (string.IsNullOrWhiteSpace(leftVersion) || string.IsNullOrWhiteSpace(rightVersion))
            return null;

        string lClean = leftVersion.Trim();
        string rClean = rightVersion.Trim();

        // 1) 바람별 통합한글화모음 스타일 (V98G8 vs V99G1)
        var mLVg = BarambyulPattern.Match(lClean);
        var mRVg = BarambyulPattern.Match(rClean);
        if (mLVg.Success && mRVg.Success &&
            string.Equals(mLVg.Groups[2].Value, mRVg.Groups[2].Value, StringComparison.OrdinalIgnoreCase))
        {
            int lMajor = int.Parse(mLVg.Groups[1].Value, CultureInfo.InvariantCulture);
            int lMinor = int.Parse(mLVg.Groups[3].Value, CultureInfo.InvariantCulture);
            int rMajor = int.Parse(mRVg.Groups[1].Value, CultureInfo.InvariantCulture);
            int rMinor = int.Parse(mRVg.Groups[3].Value, CultureInfo.InvariantCulture);

            int majorCmp = lMajor.CompareTo(rMajor);
            return majorCmp != 0 ? majorCmp : lMinor.CompareTo(rMinor);
        }

        // 2) 접두사 제거 후 문자열 일치 / 점(.)만 생략된 동일 버전(예: v42 vs v4.2)
        string lNorm = TrimVersionPrefixes(lClean);
        string rNorm = TrimVersionPrefixes(rClean);
        if (string.Equals(lNorm, rNorm, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lNorm.Replace(".", ""), rNorm.Replace(".", ""), StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        // 3) 숫자 + 선택적 알파벳 접미사 (예: v60a vs v60b, 0.2.4 vs 0.2.5)
        var mLa = Regex.Match(lNorm, @"^(\d+(?:\.\d+)*)([a-zA-Z]*)$");
        var mRa = Regex.Match(rNorm, @"^(\d+(?:\.\d+)*)([a-zA-Z]*)$");
        if (mLa.Success && mRa.Success)
        {
            int segCmp = CompareNumericSegments(mLa.Groups[1].Value, mRa.Groups[1].Value);
            if (segCmp != 0)
                return segCmp;

            string lSuffix = mLa.Groups[2].Value.ToLowerInvariant();
            string rSuffix = mRa.Groups[2].Value.ToLowerInvariant();
            return string.Compare(lSuffix, rSuffix, StringComparison.Ordinal);
        }

        // 4) 다부분 토큰 비교 fallback
        return TryCompareGeneralTokens(lNorm, rNorm);
    }

    public static bool IsRemoteNewerOrEqual(string? localVersion, string? remoteVersion)
    {
        if (string.IsNullOrWhiteSpace(localVersion) || string.IsNullOrWhiteSpace(remoteVersion))
            return true;

        int? cmp = CompareVersions(remoteVersion, localVersion);
        return cmp is null or >= 0;
    }

    public static (ModUpdateStatusCode StatusCode, string StatusLabel) CompareLocalAndRemoteVersion(
        string? localVersion,
        string? remoteVersionLabel,
        string? detectedFilename = null,
        string? assetName = null)
    {
        string remoteTok = FirstNonEmpty(
            ExtractVersionFromFilename(remoteVersionLabel),
            ExtractVersionFromFilename(detectedFilename),
            ExtractVersionFromFilename(assetName));

        if (!string.IsNullOrWhiteSpace(localVersion) && !string.IsNullOrWhiteSpace(remoteTok))
        {
            int? cmp = CompareVersions(remoteTok, localVersion);
            if (cmp.HasValue)
            {
                return cmp.Value > 0
                    ? (ModUpdateStatusCode.UpdateNeeded, "업데이트 필요")
                    : (ModUpdateStatusCode.UpToDate, "최신 상태");
            }

            string lNorm = TrimVersionPrefixes(localVersion.Trim());
            string rNorm = TrimVersionPrefixes(remoteTok.Trim());
            return !string.Equals(rNorm, lNorm, StringComparison.OrdinalIgnoreCase)
                ? (ModUpdateStatusCode.UpdateNeeded, "업데이트 필요")
                : (ModUpdateStatusCode.UpToDate, "최신 상태");
        }

        return (ModUpdateStatusCode.ManualCheck, "배포처 확인 권장 (버전 미표기)");
    }

    public static string TrimVersionPrefixes(string value)
    {
        return value.TrimStart('v', 'V', 'a', 'A', 'b', 'B');
    }

    private static int CompareNumericSegments(string a, string b)
    {
        var partsA = a.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var partsB = b.Split('.', StringSplitOptions.RemoveEmptyEntries);
        int maxLen = Math.Max(partsA.Length, partsB.Length);
        for (int i = 0; i < maxLen; i++)
        {
            int va = i < partsA.Length && int.TryParse(partsA[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pa) ? pa : 0;
            int vb = i < partsB.Length && int.TryParse(partsB[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pb) ? pb : 0;
            if (va != vb)
                return va.CompareTo(vb);
        }
        return 0;
    }

    private static int? TryCompareGeneralTokens(string a, string b)
    {
        var tokensA = Regex.Split(a.ToLowerInvariant(), @"[.\-_]+");
        var tokensB = Regex.Split(b.ToLowerInvariant(), @"[.\-_]+");
        int maxLen = Math.Max(tokensA.Length, tokensB.Length);
        for (int i = 0; i < maxLen; i++)
        {
            string ta = i < tokensA.Length ? tokensA[i] : "0";
            string tb = i < tokensB.Length ? tokensB[i] : "0";
            bool na = int.TryParse(ta, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ia);
            bool nb = int.TryParse(tb, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ib);
            if (na && nb)
            {
                if (ia != ib) return ia.CompareTo(ib);
            }
            else
            {
                int sc = string.Compare(ta, tb, StringComparison.Ordinal);
                if (sc != 0) return sc;
            }
        }
        return 0;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
                return c.Trim();
        }
        return string.Empty;
    }
}
