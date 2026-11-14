namespace Sims4ModUpdater.Core.Models;

/// <summary>
/// .package 및 .ts4script 내부 바이너리/아카이브 메타데이터 추출 결과
/// </summary>
public sealed class ModMetadata
{
    public bool HasMetadata { get; set; }
    public string ManifestName { get; set; } = string.Empty;
    public List<string> Creators { get; set; } = new();
    public string InferredCreator { get; set; } = string.Empty;
    public string InferredMod { get; set; } = string.Empty;
    public List<string> DisplayNames { get; set; } = new();
    public List<string> Urls { get; set; } = new();
    public string Version { get; set; } = string.Empty;
    public List<string> RootTunings { get; set; } = new();
    public List<string> Modules { get; set; } = new();
    public bool IsAnimationAddon { get; set; }
}

/// <summary>
/// DBPF 및 스크립트 구조 분석 기반 모드 기능 유형 대분류 (4대 분류)
/// </summary>
public enum ModFunctionalCategory
{
    Pending = 0,
    ScriptCoreMod = 1,
    XmlGameplayMod = 2,
    ObjectModAndCc = 3,
    AnimationAndPose = 4
}

/// <summary>
/// 모드 기능 유형 분류 결과 (대분류, 세부 분류, DBPF/스크립트 판별 근거)
/// </summary>
public sealed class ModCategoryClassification
{
    public ModFunctionalCategory Category { get; set; } = ModFunctionalCategory.Pending;
    public string SubCategory { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
