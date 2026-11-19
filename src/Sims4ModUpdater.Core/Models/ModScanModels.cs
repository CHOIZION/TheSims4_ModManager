namespace Sims4ModUpdater.Core.Models;

/// <summary>
/// 1단계 모드 폴더 요약 정보
/// </summary>
public sealed class ModFolderSummary
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int PackageCount { get; set; }
    public int ScriptCount { get; set; }
    public int SubdirCount { get; set; }
    public string ModType { get; set; } = string.Empty;
    public bool IsUpdatable { get; set; }
    public int UnitCount { get; set; }
    public bool IsStandaloneRoot { get; set; }
}

/// <summary>
/// 개별 업데이트 점검 단위(Mod Unit)
/// </summary>
public sealed class ModUnit
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string InstallFolder { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string PrimaryFileName { get; set; } = string.Empty;
    public string PrimaryFilePath { get; set; } = string.Empty;
    public List<string> UnitFiles { get; set; } = new();
    public int PackageCount { get; set; }
    public int ScriptCount { get; set; }
    public int SubdirCount { get; set; }
    public string ModType { get; set; } = string.Empty;
    public bool IsStandalone { get; set; }
    public string GroupKey { get; set; } = string.Empty;
}

/// <summary>
/// 루트 단일 모드 파일 정보
/// </summary>
public sealed class StandaloneModFile
{
    public string Name { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

/// <summary>
/// Mods 폴더 전체 스캔 결과
/// </summary>
public sealed class ModsScanResult
{
    public bool Exists { get; set; }
    public string ModsFolder { get; set; } = string.Empty;
    public int PackageCount { get; set; }
    public int ScriptCount { get; set; }
    public int TotalCount => PackageCount + ScriptCount;
    public List<string> Warnings { get; set; } = new();
    public List<ModFolderSummary> FolderMods { get; set; } = new();
    public List<StandaloneModFile> StandaloneFiles { get; set; } = new();
    public List<ModUnit> ModUnits { get; set; } = new();
}

/// <summary>
/// 폴더 내 개별 모드 파일 상세 정보
/// </summary>
public sealed class ModFileItem
{
    public string Name { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string Location { get; set; } = ".";
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

/// <summary>
/// 하위 폴더별 파일 목록
/// </summary>
public sealed class SubfolderInfo
{
    public string FolderName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = ".";
    public string FullPath { get; set; } = string.Empty;
    public List<string> Files { get; set; } = new();
}

/// <summary>
/// 중복 파일 감지 항목
/// </summary>
public sealed class DuplicateFileInfo
{
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string DuplicatePath { get; set; } = string.Empty;
}

/// <summary>
/// 선택된 모드의 설치 및 중복 상태
/// </summary>
public sealed class ModInstalledInfo
{
    public bool Installed { get; set; }
    public bool HasPackage { get; set; }
    public bool HasScript { get; set; }
    public List<string> Packages { get; set; } = new();
    public List<string> Scripts { get; set; } = new();
    public List<string> Locations { get; set; } = new();
    public List<string> TargetDirs { get; set; } = new();
    public List<DuplicateFileInfo> Duplicates { get; set; } = new();
    public bool HasDuplicates => Duplicates.Count > 0;
}

/// <summary>
/// 스캔 진단 상세 데이터
/// </summary>
public sealed class ScanDiagnostics
{
    public bool Exists { get; set; }
    public List<ModFolderSummary> FolderMods { get; set; } = new();
    public List<ModUnit> ModUnits { get; set; } = new();
    public List<ExcludedTopFolderDiag> ExcludedTopFolders { get; set; } = new();
    public List<ExcludedSubfolderDiag> ExcludedSubfolders { get; set; } = new();
    public List<PackageOnlyFolderDiag> PackageOnlyFolders { get; set; } = new();
    public List<MultiScriptFolderDiag> MultiScriptFolders { get; set; } = new();
    public List<MergedUnitFilesDiag> MergedUnitFiles { get; set; } = new();
    public List<RootFileDiag> RootFilesDiagnostics { get; set; } = new();
    public List<UnaccountedFileDiag> UnaccountedFiles { get; set; } = new();
}

public sealed class ExcludedTopFolderDiag
{
    public string FolderName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int PackageCount { get; set; }
    public int ScriptCount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> SampleFiles { get; set; } = new();
}

public sealed class ExcludedSubfolderDiag
{
    public string FolderName { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int PackageCount { get; set; }
    public int ScriptCount { get; set; }
    public List<string> Files { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public sealed class PackageOnlyFolderDiag
{
    public string FolderName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int PackageCount { get; set; }
    public int ScriptCount { get; set; }
    public int UnitCount { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class MultiScriptFolderDiag
{
    public string FolderName { get; set; } = string.Empty;
    public int TotalScripts { get; set; }
    public string RepresentativeScript { get; set; } = string.Empty;
    public List<string> OtherScripts { get; set; } = new();
    public int OmittedInFolderViewCount { get; set; }
    public List<string> OmittedInUnitView { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public sealed class MergedUnitFilesDiag
{
    public string FolderName { get; set; } = string.Empty;
    public string PrimaryFile { get; set; } = string.Empty;
    public int TotalUnitFiles { get; set; }
    public int OmittedCount { get; set; }
    public List<string> OmittedScripts { get; set; } = new();
    public List<string> OmittedPackages { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public sealed class RootFileDiag
{
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class UnaccountedFileDiag
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
