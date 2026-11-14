using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Sims4ModUpdater.Core.Models;
using Sims4ModUpdater.Core.Services;

namespace Sims4ModUpdater.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Sims4ModUpdater.Core Automated Test Suite ===");
        Console.WriteLine();

        string tempRoot = Path.Combine(Path.GetTempPath(), $"Sims4ModUpdaterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            RunTest("Crc32Helper: Known vector & File CRC32", TestCrc32);
            RunTest("VersionUtils: Date, Token Extraction & Version Comparison", TestVersionUtils);
            RunTest("CatalogService: Embedded 80+ Entries & Normalization", TestCatalogService);
            RunTest("ConfigService: Save/Load & Path Resolution", () => TestConfigService(tempRoot));
            RunTest("ModMetadataExtractor: DBPF 2.1 Zlib Package, Ts4Script YAML/PYC & UI Cheats", () => TestMetadataExtractor(tempRoot));
            RunTest("ModScannerService: Grouping, CC Exclusion, Depth Warning, Duplicates & 0-Unaccounted", () => TestScanner(tempRoot));
            RunTest("ArchiveAndFileService: Validation, Zip Central Dir, Zip-Slip & OverwriteMode", () => TestArchives(tempRoot));
            await RunTestAsync("ArchiveAndFileService: VerifyLocalMatchesRemoteAsync via Range/CRC32", () => TestRemoteVerificationAsync(tempRoot));
            RunTest("BackupService: Session Backup, Restore & Confined Delete", () => TestBackupAndRestore(tempRoot));
            await RunTestAsync("ModUpdaterService: Pinning, End-to-End Mock Check & Update + Political World Cleanup", () => TestUpdaterEndToEndAsync(tempRoot));
            RunTest("DiagnosticReportService: Privacy Masking & 3-Section Scan Report", () => TestDiagnosticReport(tempRoot));
            RunTest("CreatorOrganizerService: Creator Resolution, Overrides & Safe Folder Organization", () => TestCreatorOrganizer(tempRoot));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Ignore temp cleanup contention
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total Passed: {_passed}, Failed: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunTest(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task RunTestAsync(string name, Func<Task> action)
    {
        try
        {
            await action();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}");
            Console.WriteLine($"       {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} (Expected: '{expected}', Actual: '{actual}')");
    }

    private static void TestCrc32()
    {
        byte[] standardVec = Encoding.ASCII.GetBytes("123456789");
        uint crc = Crc32Helper.Compute(standardVec);
        AssertEqual(0xCBF43926u, crc, "IEEE CRC32 of '123456789'");

        string tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmpFile, standardVec);
            uint fileCrc = Crc32Helper.ComputeFile(tmpFile);
            AssertEqual(0xCBF43926u, fileCrc, "ComputeFile IEEE CRC32");
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    private static void TestVersionUtils()
    {
        AssertEqual("2026.02.15", VersionUtils.ExtractDateFromFilename("MCCC_2026_02_15.zip"), "Date YYYY_MM_DD");
        AssertEqual("2025.09.20", VersionUtils.ExtractDateFromFilename("[20250920] Mod.package"), "Date YYYYMMDD");
        AssertEqual("2026.02.15", VersionUtils.ExtractDateFromFilename("Mod_15.02.2026.package"), "European DD.MM.YYYY");
        AssertEqual("", VersionUtils.ExtractDateFromFilename("MCCC_2026.6.0.zip"), "Semantic 2026.6.0 must not match date");

        AssertEqual("V98G8", VersionUtils.ExtractVersionFromFilename("KR_Translation_V98G8.zip"), "Barambyul/VxxGx token");
        AssertEqual("v60b", VersionUtils.ExtractVersionFromFilename("GameplayOverhaul_v60b.zip"), "Alphanumeric suffix version v60b");
        AssertEqual("v2026.6.0", VersionUtils.ExtractVersionFromFilename("MCCommandCenter_AllModules_2026.6.0.zip"), "Semantic dotted version 2026.6.0");

        Assert(VersionUtils.CompareVersions("2.4.3", "2.4.2") > 0, "2.4.3 > 2.4.2");
        Assert(VersionUtils.CompareVersions("V98G8", "V98G7") > 0, "V98G8 > V98G7");
        Assert(VersionUtils.CompareVersions("60b", "60a") > 0, "60b > 60a");
        Assert(VersionUtils.CompareVersions("a0.5", "a0.4") > 0, "a0.5 > a0.4");
        AssertEqual(0, VersionUtils.CompareVersions("v42", "v4.2"), "v42 equals dot-stripped v4.2");
        Assert(VersionUtils.CompareVersions("v43", "v4.2") > 0, "v43 > v4.2");

        var (codeNeed, _) = VersionUtils.CompareLocalAndRemoteVersion("2.4.2", "2.4.3");
        AssertEqual(ModUpdateStatusCode.UpdateNeeded, codeNeed, "2.4.2 vs 2.4.3 -> UpdateNeeded");

        var (codeSame, _) = VersionUtils.CompareLocalAndRemoteVersion("60b", "v60b");
        AssertEqual(ModUpdateStatusCode.UpToDate, codeSame, "60b vs v60b -> UpToDate");

        var (codeUnknown, _) = VersionUtils.CompareLocalAndRemoteVersion("", "2.4.3");
        AssertEqual(ModUpdateStatusCode.ManualCheck, codeUnknown, "missing local ver -> ManualCheck");
    }

    private static void TestCatalogService()
    {
        var catalog = new CatalogService();
        Assert(catalog.Items.Count >= 80, $"Expected >= 80 catalog items, got {catalog.Items.Count}");

        var uiCheats = catalog.CheckKnownCatalog("UI Cheats Extension");
        Assert(uiCheats is { Found: true }, "UI Cheats Extension should match known catalog");
        Assert(!string.IsNullOrEmpty(uiCheats!.PrimaryUrl), "UI Cheats should have PrimaryUrl");

        var mccc = catalog.CheckKnownCatalog("Deaderpool MCCC mc_cmd_center");
        Assert(mccc is { Found: true }, "MCCC should match known catalog");

        string cleanedPatreon = CatalogService.NormalizeDistributionUrl("https://www.patreon.com/posts/12345678?utm_source=foo&l=ko");
        AssertEqual("https://www.patreon.com/posts/12345678", cleanedPatreon, "Patreon tracking query normalization");
    }

    private static void TestConfigService(string tempRoot)
    {
        string appDir = Path.Combine(tempRoot, "cfg_test");
        Directory.CreateDirectory(appDir);
        var svc = new ConfigService(appDir);

        var cfg = svc.LoadConfig();
        Assert(File.Exists(svc.ConfigFilePath), "LoadConfig creates config.json if absent");

        string customModsDir = Path.Combine(tempRoot, "Mods");
        Directory.CreateDirectory(customModsDir);
        cfg.ModsFolder = customModsDir;
        cfg.BackupFolder = "custom_backups";
        cfg.ManifestPath = "custom_manifest.json";
        cfg.MaxConcurrentChecks = 6;
        svc.SaveConfig(cfg);

        var reloaded = svc.LoadConfig();
        AssertEqual(cfg.ModsFolder, reloaded.ModsFolder, "Reloaded ModsFolder");
        AssertEqual(6, reloaded.MaxConcurrentChecks, "Reloaded MaxConcurrentChecks");
        AssertEqual(Path.Combine(appDir, "custom_backups"), svc.ResolveBackupFolder(reloaded), "Resolved backup folder");
        AssertEqual(Path.Combine(appDir, "custom_manifest.json"), svc.ResolveManifestPath(reloaded), "Resolved manifest path");
    }

    private static void TestMetadataExtractor(string tempRoot)
    {
        string metaDir = Path.Combine(tempRoot, "meta_test");
        Directory.CreateDirectory(metaDir);
        var extractor = new ModMetadataExtractor(new CatalogService());

        // 1. Construct genuine DBPF 2.1 binary .package with zlib-compressed ModFileManifest XML
        string pkgPath = Path.Combine(metaDir, "CoolMod_v1.2.3.package");
        string xmlPayload =
            "<M n=\"ModFileManifest\">" +
            "<T n=\"name\">Cool Gameplay Mod</T>" +
            "<T n=\"version\">1.2.3</T>" +
            "<L n=\"creators\"><T>CoolCreator</T></L>" +
            "<T n=\"url\">https://www.patreon.com/posts/99887766</T>" +
            "</M>";
        WriteDbpfPackage(pkgPath, xmlPayload);

        ModMetadata pkgMeta = extractor.Extract(pkgPath);
        Assert(pkgMeta.HasMetadata, "DBPF package HasMetadata");
        AssertEqual("Cool Gameplay Mod", pkgMeta.ManifestName, "DBPF ManifestName");
        AssertEqual("1.2.3", pkgMeta.Version, "DBPF Version");
        Assert(pkgMeta.Creators.Contains("CoolCreator"), "DBPF Creator");
        Assert(pkgMeta.Urls.Any(u => u.Contains("99887766")), "DBPF URL");

        // 2. Construct .ts4script with YAML ModFileManifest & .pyc marshal length-prefixed URL
        string scrPath = Path.Combine(metaDir, "ScriptMod_v2.0.ts4script");
        using (var fs = File.Create(scrPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var manifestEntry = zip.CreateEntry("ModFileManifest.yml");
            using (var sw = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
            {
                sw.WriteLine("name: Script Gameplay Mod");
                sw.WriteLine("version: 2.0.5");
                sw.WriteLine("creators:");
                sw.WriteLine("  - ScriptAuthor");
                sw.WriteLine("url: https://simfileshare.net/download/555444/");
            }

            var pycEntry = zip.CreateEntry("scriptmod/core.pyc");
            using (var s = pycEntry.Open())
            {
                string[] embeddedUrls =
                [
                    "https://mail.python.org/pipermail/python-dev/2010-April/099672.html",
                    "https://bugs.python.org/issue14965",
                    "https://www.patreon.com/posts/77766655"
                ];
                foreach (string embeddedUrl in embeddedUrls)
                {
                    byte[] urlBytes = Encoding.ASCII.GetBytes(embeddedUrl);
                    byte[] prefix = [0xFA, (byte)urlBytes.Length];
                    s.Write(prefix);
                    s.Write(urlBytes);
                }
            }
        }

        ModMetadata scrMeta = extractor.Extract(scrPath);
        Assert(scrMeta.HasMetadata, "Ts4Script HasMetadata");
        AssertEqual("Script Gameplay Mod", scrMeta.ManifestName, "Ts4Script YAML ManifestName");
        AssertEqual("2.0.5", scrMeta.Version, "Ts4Script YAML Version");
        Assert(scrMeta.Creators.Contains("ScriptAuthor"), "Ts4Script YAML Creator");
        Assert(scrMeta.Urls.Any(u => u.Contains("77766655")), "Ts4Script PYC marshal URL");
        Assert(!scrMeta.Urls.Any(u => u.Contains("python.org")), "Python stdlib doc/bug URLs must be excluded");

        // Embedded URL priority: reliable distribution domains outrank unknown arbitrary hosts
        string customHostsPkg = Path.Combine(metaDir, "CustomUrlsOrder.package");
        WriteDbpfPackage(customHostsPkg, "<M n=\"ModFileManifest\"><T n=\"url\">https://example.org/docs</T><T n=\"funding_url\">https://www.patreon.com/posts/99988877</T></M>");
        var finder = new ModFinderService(new CatalogService(), extractor);
        ModPageDiscoveryResult discovery = finder.FindDirectModPageAsync("CustomUrlsOrder", customHostsPkg).GetAwaiter().GetResult();
        Assert(discovery.PrimaryUrl.Contains("patreon.com/posts/99988877", StringComparison.Ordinal),
            "Reliable domain URL must outrank non-catalog host in FindDirectModPageAsync");

        // 3. UI Cheats Extension size heuristic
        string uiCheatsPath = Path.Combine(metaDir, "UI_Cheats_Extension_Test.ts4script");
        using (var fs = File.Create(uiCheatsPath))
        {
            fs.SetLength(15_388_976);
        }
        ModMetadata uiMeta = extractor.Extract(uiCheatsPath);
        Assert("weerbesu".Equals(uiMeta.InferredCreator, StringComparison.OrdinalIgnoreCase), "UI Cheats inferred creator");
        AssertEqual("UI Cheats Extension", uiMeta.InferredMod, "UI Cheats inferred mod name");

        // 4. DBPF STBL hex hash decoding (0x2170DC9B -> StudioOne, 0x9EF85F95 -> StudioOne_ModelPoses_4)
        string stblPkgPath = Path.Combine(metaDir, "StudioOne_ModelPoses4.package");
        byte[] stblPayload = BuildStblPayload(
        [
            (0x2170DC9Bu, "StudioOne"),
            (0x9EF85F95u, "StudioOne_ModelPoses_4")
        ]);
        WriteDbpfEntries(
            stblPkgPath,
            [
                (0x545AC67Au, 0u, Encoding.UTF8.GetBytes("<I n=\"StudioOne:PosePack_1\"><T n=\"creator_name\">0x2170DC9B</T><T n=\"display_name\">0x9EF85F95</T></I>")),
                (0x220557DAu, 0x00000000u, stblPayload)
            ]);
        ModMetadata stblMeta = extractor.Extract(stblPkgPath);
        Assert(stblMeta.Creators.Contains("StudioOne"), "DBPF STBL resolves 0x2170DC9B creator hash to StudioOne");
        Assert(!stblMeta.Creators.Any(c => c.StartsWith("0x", StringComparison.OrdinalIgnoreCase)), "Raw 0x hex creator must never leak");
        Assert(stblMeta.DisplayNames.Contains("StudioOne_ModelPoses_4"), "DBPF STBL resolves display_name hash");

        // 5. Unresolved 0xDEADBEEF hash rejected + [0.6 Scaled] skipped + [CreatorTwo] preserved
        string creatorTwoPath = Path.Combine(metaDir, "[0.6 Scaled][CreatorTwo]DecorativeLamp.package");
        WriteDbpfPackage(creatorTwoPath, "<I n=\"NamespaceMod:Test\"/><T n=\"creator_name\">0xDEADBEEF</T>");
        ModMetadata creatorTwoMeta = extractor.Extract(creatorTwoPath);
        AssertEqual(0, creatorTwoMeta.Creators.Count, "Unresolved 0xDEADBEEF hash must be discarded");
        AssertEqual("CreatorTwo", creatorTwoMeta.InferredCreator, "Bracket [CreatorTwo] prioritized over [0.6 Scaled] and unresolved hex");

        // Package without THUM resource -> null thumbnail
        Assert(extractor.ExtractThumbnailBytes(creatorTwoPath) is null, "Package without THUM must return null thumbnail");

        // DBPF with multiple Object Catalog THUM chunks -> picks largest valid JPEG chunk
        byte[] smallJpeg = new byte[48];
        smallJpeg[0] = 0xFF; smallJpeg[1] = 0xD8; smallJpeg[2] = 0xFF; smallJpeg[3] = 0xE0;
        byte[] largeJpeg = new byte[128];
        largeJpeg[0] = 0xFF; largeJpeg[1] = 0xD8; largeJpeg[2] = 0xFF; largeJpeg[3] = 0xE0;
        largeJpeg[10] = 0x42;
        byte[] bogusThumb = Encoding.UTF8.GetBytes(new string('X', 256));

        string thumbPkgPath = Path.Combine(metaDir, "MultiThumb.package");
        WriteDbpfEntries(
            thumbPkgPath,
            [
                (0x3C2A8647u, 0u, smallJpeg),
                (0x3C2A8647u, 0u, bogusThumb),
                (0x3C2A8647u, 0u, largeJpeg)
            ]);
        byte[]? extractedThumb = extractor.ExtractThumbnailBytes(thumbPkgPath);
        Assert(extractedThumb is not null && extractedThumb.Length == 128 && extractedThumb[10] == 0x42,
            "DBPF THUM extractor picks largest valid JPEG thumbnail");

        // Unit fallback: PrimaryFile has no THUM, secondary .package has PNG THUM (0x5B282D45)
        byte[] pngThumb = new byte[64];
        pngThumb[0] = 0x89; pngThumb[1] = 0x50; pngThumb[2] = 0x4E; pngThumb[3] = 0x47;
        pngThumb[4] = 0x0D; pngThumb[5] = 0x0A; pngThumb[6] = 0x1A; pngThumb[7] = 0x0A;
        string pngPkgPath = Path.Combine(metaDir, "PngSecondary.package");
        WriteDbpfEntries(pngPkgPath, [(0x5B282D45u, 0u, pngThumb)]);
        var thumbFallbackUnit = new ModUnit
        {
            Name = "ThumbFallback",
            PrimaryFilePath = creatorTwoPath,
            PrimaryFileName = Path.GetFileName(creatorTwoPath),
            UnitFiles = [creatorTwoPath, pngPkgPath]
        };
        byte[]? fallbackBytes = extractor.ExtractThumbnailBytesForUnit(thumbFallbackUnit);
        Assert(fallbackBytes is not null && fallbackBytes.Length == 64 && fallbackBytes[1] == 0x50,
            "Unit thumbnail extraction falls back to secondary .package PNG THUM");

        // Functional classification tests (4 buckets)
        string scriptFile = Path.Combine(metaDir, "CoreMod.ts4script");
        File.WriteAllText(scriptFile, "zip");
        ModCategoryClassification scriptCat = extractor.ClassifyFunctionalCategory(new ModUnit
        {
            Name = "CoreMod",
            PrimaryFilePath = scriptFile,
            PrimaryFileName = Path.GetFileName(scriptFile),
            UnitFiles = [scriptFile]
        });
        AssertEqual(ModFunctionalCategory.ScriptCoreMod, scriptCat.Category, "ts4script unit must classify as ScriptCoreMod");

        string xmlPkgPath = Path.Combine(metaDir, "GameplayBuff.package");
        WriteDbpfEntries(xmlPkgPath, [(0xE882D22Fu, 0u, Encoding.UTF8.GetBytes("<I c=\"Interaction\" />"))]);
        ModCategoryClassification xmlCat = extractor.ClassifyFunctionalCategory(new ModUnit
        {
            Name = "GameplayBuff",
            PrimaryFilePath = xmlPkgPath,
            PrimaryFileName = Path.GetFileName(xmlPkgPath),
            UnitFiles = [xmlPkgPath]
        });
        AssertEqual(ModFunctionalCategory.XmlGameplayMod, xmlCat.Category, "XML tuning without CASP/COBJ must classify as XmlGameplayMod");

        string catStblPath = Path.Combine(metaDir, "KoreanTranslation.package");
        WriteDbpfEntries(catStblPath, [(0x220557DAu, 0u, BuildStblPayload([(1u, "한국어 번역")]))]);
        ModCategoryClassification stblCat = extractor.ClassifyFunctionalCategory(new ModUnit
        {
            Name = "KoreanTranslation",
            PrimaryFilePath = catStblPath,
            PrimaryFileName = Path.GetFileName(catStblPath),
            UnitFiles = [catStblPath]
        });
        AssertEqual(ModFunctionalCategory.XmlGameplayMod, stblCat.Category, "STBL translation pack must classify as XmlGameplayMod");
        Assert(stblCat.SubCategory.Contains("STBL", StringComparison.OrdinalIgnoreCase), "STBL translation sub-category must mention STBL");

        string functionalObjPath = Path.Combine(metaDir, "[MakerA] EspressoMachine.package");
        WriteDbpfEntries(
            functionalObjPath,
            [
                (0x319E4F1Du, 0u, [1, 2, 3, 4]), // COBJ
                (0xB61DE6B4u, 0u, Encoding.UTF8.GetBytes("<I c=\"GameObject\" />")),
                (0x7DF2169Cu, 0u, Encoding.UTF8.GetBytes("<I c=\"CustomAnimationPackage\" m=\"anim.utils_snippets\" />")),
                (0x6B20C4F3u, 0u, [5, 6, 7, 8]) // CLIP
            ]);
        ModCategoryClassification objCat = extractor.ClassifyFunctionalCategory(new ModUnit
        {
            Name = "[MakerA] EspressoMachine",
            PrimaryFilePath = functionalObjPath,
            PrimaryFileName = Path.GetFileName(functionalObjPath),
            UnitFiles = [functionalObjPath]
        });
        AssertEqual(ModFunctionalCategory.ObjectModAndCc, objCat.Category, "Functional COBJ object with GameObject XML + motion must stay in ObjectModAndCc");
        AssertEqual("작동 기능 오브젝트 (+모션)", objCat.SubCategory, "Functional object with CLIP/animation tag gets motion sub-category");

        string casCcPath = Path.Combine(metaDir, "PureHairCC.package");
        WriteDbpfEntries(
            casCcPath,
            [
                (0x034AEECBu, 0u, [1, 2, 3, 4]), // CASP
                (0x7DF2169Cu, 0u, Encoding.UTF8.GetBytes("<I c=\"CustomCASPartsPackage\" m=\"cas.utils_snippets\" />"))
            ]);
        ModCategoryClassification casCat = extractor.ClassifyFunctionalCategory(new ModUnit
        {
            Name = "PureHairCC",
            PrimaryFilePath = casCcPath,
            PrimaryFileName = Path.GetFileName(casCcPath),
            UnitFiles = [casCcPath]
        });
        AssertEqual(ModFunctionalCategory.ObjectModAndCc, casCat.Category, "CASP with CAS parts snippet must classify as ObjectModAndCc");
        AssertEqual("CAS CC", casCat.SubCategory, "Pure CASP item sub-category must be CAS CC");

        string customAnimPath = Path.Combine(metaDir, "Anim_Creator_DancePack.package");
        WriteDbpfEntries(
            customAnimPath,
            [
                (0x319E4F1Du, 0u, [1, 2, 3, 4]), // Stage COBJ without gameplay XML
                (0x7DF2169Cu, 0u, Encoding.UTF8.GetBytes("<I c=\"DanceAnimationPackage\" m=\"anim.utils_snippets\" />")),
                (0x6B20C4F3u, 0u, [5, 6, 7, 8]) // CLIP
            ]);
        ModCategoryClassification customAnimCat = extractor.ClassifyFunctionalCategory(new ModUnit
        {
            Name = "Anim_Creator_DancePack",
            PrimaryFilePath = customAnimPath,
            PrimaryFileName = Path.GetFileName(customAnimPath),
            UnitFiles = [customAnimPath]
        });
        AssertEqual(ModFunctionalCategory.AnimationAndPose, customAnimCat.Category, "Animation package without gameplay XML must classify as AnimationAndPose");
        AssertEqual("애니메이션 팩", customAnimCat.SubCategory, "Animation snippet + CLIP must label as 애니메이션 팩");

        string posePath = Path.Combine(metaDir, "Creator_PosePack.package");
        WriteDbpfEntries(
            posePath,
            [
                (0x7DF2169Cu, 0u, Encoding.UTF8.GetBytes("<I c=\"PosePackInstance\" />")),
                (0x6B20C4F3u, 0u, [1, 2, 3, 4]) // CLIP
            ]);
        ModCategoryClassification poseCat = extractor.ClassifyFunctionalCategory(new ModUnit
        {
            Name = "Creator_PosePack",
            PrimaryFilePath = posePath,
            PrimaryFileName = Path.GetFileName(posePath),
            UnitFiles = [posePath]
        });
        AssertEqual(ModFunctionalCategory.AnimationAndPose, poseCat.Category, "PosePack snippet must classify as AnimationAndPose");
        AssertEqual("포즈 팩", poseCat.SubCategory, "PosePack snippet sub-category must be 포즈 팩");
    }

    private static void TestScanner(string tempRoot)
    {
        string modsDir = Path.Combine(tempRoot, "Mods_Scan");
        Directory.CreateDirectory(modsDir);

        // MCCC folder with core + extra modules
        string mcccDir = Path.Combine(modsDir, "MC Command Center");
        Directory.CreateDirectory(mcccDir);
        File.WriteAllText(Path.Combine(mcccDir, "mc_cmd_center.ts4script"), "zip");
        File.WriteAllText(Path.Combine(mcccDir, "mc_cmd_center.package"), "DBPF");
        File.WriteAllText(Path.Combine(mcccDir, "mc_woohoo.ts4script"), "zip");

        // Standalone root files
        File.WriteAllText(Path.Combine(modsDir, "RootMod.package"), "DBPF");
        File.WriteAllText(Path.Combine(modsDir, "RootScript.ts4script"), "zip");

        // CC folder (excluded from updates)
        string ccDir = Path.Combine(modsDir, "[CAS] Hair Collection");
        Directory.CreateDirectory(ccDir);
        File.WriteAllText(Path.Combine(ccDir, "Hair01.package"), "DBPF");

        // Depth > 1 script warning + duplicate file inside MCCC tree
        string deepSub = Path.Combine(mcccDir, "SubLevel1", "SubLevel2");
        Directory.CreateDirectory(deepSub);
        File.WriteAllText(Path.Combine(deepSub, "too_deep.ts4script"), "zip");
        File.WriteAllText(Path.Combine(deepSub, "mc_cmd_center.package"), "DBPF");

        var scanner = new ModScannerService(modsDir, new CatalogService());
        ModsScanResult scan = scanner.Scan();

        Assert(scan.Exists, "Mods folder exists");
        AssertEqual(4, scan.PackageCount, "Total .package count");
        AssertEqual(4, scan.ScriptCount, "Total .ts4script count");
        Assert(scan.Warnings.Any(w => w.Contains("too_deep.ts4script", StringComparison.OrdinalIgnoreCase)),
            "Depth warning should report nested level-3 ts4script");

        Assert(scan.FolderMods.Any(f => f.Name == "MC Command Center" && f.IsUpdatable), "MC Command Center updatable");
        Assert(scan.FolderMods.Any(f => f.Name == "[CAS] Hair Collection" && !f.IsUpdatable), "CC folder excluded");

        ScanDiagnostics diag = scanner.CollectScanDiagnostics();
        AssertEqual(0, diag.UnaccountedFiles.Count, "100% file accounting -> 0 unaccounted files");

        ModInstalledInfo dupCheck = scanner.DetectModInstalled(new ModQuery
        {
            Name = "MC Command Center",
            FolderName = "MC Command Center",
            InstallFolder = "MC Command Center",
            DirectoryPath = mcccDir
        });
        Assert(dupCheck.HasDuplicates, "Duplicate mc_cmd_center.package detected across folder tree");

        // Regression guard: selecting a specific Unit in a subfolder containing an unrelated duplicate sibling must NOT blame the selected unit
        ModInstalledInfo unrelatedUnitCheck = scanner.DetectModInstalled(new ModQuery
        {
            Name = "mc_woohoo.ts4script",
            FolderName = "MC Command Center",
            InstallFolder = "MC Command Center",
            DirectoryPath = mcccDir,
            UnitFiles = [Path.Combine(mcccDir, "mc_woohoo.ts4script")]
        });
        Assert(!unrelatedUnitCheck.HasDuplicates, "Unrelated unit inside shared subfolder must not report sibling duplicate");

        ModInstalledInfo exactUnitDupCheck = scanner.DetectModInstalled(new ModQuery
        {
            Name = "mc_cmd_center.package",
            FolderName = "MC Command Center",
            InstallFolder = "MC Command Center",
            DirectoryPath = mcccDir,
            UnitFiles = [Path.Combine(mcccDir, "mc_cmd_center.package")]
        });
        Assert(exactUnitDupCheck.HasDuplicates, "Selected unit whose own file has duplicate still reports duplicate");

        HashSet<string> allDupPaths = scanner.CollectAllDuplicateFilePaths();
        Assert(allDupPaths.Contains(Path.Combine(mcccDir, "mc_cmd_center.package")),
            "CollectAllDuplicateFilePaths includes primary duplicate path");
        Assert(allDupPaths.Contains(Path.Combine(deepSub, "mc_cmd_center.package")),
            "CollectAllDuplicateFilePaths includes secondary duplicate path");
        Assert(!allDupPaths.Contains(Path.Combine(mcccDir, "mc_woohoo.ts4script")),
            "CollectAllDuplicateFilePaths excludes unique files");
        Assert(!allDupPaths.Contains(Path.Combine(modsDir, "RootMod.package")),
            "CollectAllDuplicateFilePaths excludes unique root files");
    }

    private static void TestArchives(string tempRoot)
    {
        string arcDir = Path.Combine(tempRoot, "archives");
        Directory.CreateDirectory(arcDir);

        // 1. HTML disguised as .package -> rejected
        string fakePkg = Path.Combine(arcDir, "not_a_package.package");
        File.WriteAllText(fakePkg, "<!DOCTYPE html><html><body>Login required</body></html>");
        bool threwHtml = false;
        try { ArchiveAndFileService.VerifyDownloadedModFile(fakePkg); }
        catch (InvalidOperationException) { threwHtml = true; }
        Assert(threwHtml, "HTML disguised as .package must be rejected");

        // 2. Valid DBPF .package -> accepted
        string realPkg = Path.Combine(arcDir, "valid.package");
        WriteDbpfPackage(realPkg, "<M n=\"Test\"/>");
        ArchiveAndFileService.VerifyDownloadedModFile(realPkg);

        // 3. Zip extraction + Zip-Slip protection + OverwriteMode.Single vs All
        string targetDir = Path.Combine(arcDir, "extracted");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "ModA.package"), "old_A");
        File.WriteAllText(Path.Combine(targetDir, "ModB.package"), "old_B");

        string zipPath = Path.Combine(arcDir, "bundle.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "ModA.package", "DBPF_new_A");
            WriteZipEntry(zip, "ModB.package", "DBPF_new_B");
            WriteZipEntry(zip, "../../evil_outside.package", "DBPF_evil");
        }

        ArchiveAndFileService.VerifyDownloadedModFile(zipPath);

        // Verify Zip Central Directory parser matches CRC32
        byte[] zipBytes = File.ReadAllBytes(zipPath);
        var entries = ArchiveAndFileService.ParseZipCentralDirectoryEntries(zipBytes);
        var entryA = entries.First(e => e.FileName == "ModA.package");
        AssertEqual(Crc32Helper.Compute(Encoding.UTF8.GetBytes("DBPF_new_A")), entryA.Crc32, "ZIP CD CRC32 matches ModA");

        // Single mode updating only ModA
        ArchiveAndFileService.ExtractArchive(zipPath, targetDir, "ModA.package", OverwriteMode.Single);
        AssertEqual("DBPF_new_A", File.ReadAllText(Path.Combine(targetDir, "ModA.package")), "Single overwrite updates ModA");
        AssertEqual("old_B", File.ReadAllText(Path.Combine(targetDir, "ModB.package")), "Single overwrite preserves ModB");
        Assert(!File.Exists(Path.Combine(arcDir, "evil_outside.package")), "Zip-slip entry must not escape targetDir");

        // All mode updates ModB too
        ArchiveAndFileService.ExtractArchive(zipPath, targetDir, overwriteMode: OverwriteMode.All);
        AssertEqual("DBPF_new_B", File.ReadAllText(Path.Combine(targetDir, "ModB.package")), "All overwrite updates ModB");
    }

    private static async Task TestRemoteVerificationAsync(string tempRoot)
    {
        string verifyDir = Path.Combine(tempRoot, "remote_verify");
        Directory.CreateDirectory(verifyDir);

        string localFile = Path.Combine(verifyDir, "CoolScript.ts4script");
        byte[] localBytes = Encoding.UTF8.GetBytes("DBPF_SAME_PAYLOAD_1234567890");
        File.WriteAllBytes(localFile, localBytes);

        // Create remote zip containing CoolScript.ts4script with identical bytes
        string remoteZipPath = Path.Combine(verifyDir, "remote_bundle.zip");
        using (var fs = File.Create(remoteZipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("CoolScript.ts4script");
            using var es = entry.Open();
            es.Write(localBytes);
        }
        byte[] remoteZipBytes = File.ReadAllBytes(remoteZipPath);

        using var stubHttp = new HttpClient(new StubHttpHandler((req, _) =>
        {
            if (req.RequestUri?.AbsoluteUri == "https://example.test/CoolScript_bundle.zip")
            {
                if (req.Method == HttpMethod.Head)
                {
                    var headResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                    headResp.Content.Headers.ContentLength = remoteZipBytes.Length;
                    return headResp;
                }
                if (req.Headers.Range is not null)
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                    {
                        Content = new ByteArrayContent(remoteZipBytes)
                    };
                    resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, remoteZipBytes.Length - 1, remoteZipBytes.Length);
                    return resp;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(remoteZipBytes) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var arcSvc = new ArchiveAndFileService(stubHttp);
        bool? isMatch = await arcSvc.VerifyLocalMatchesRemoteAsync(
            verifyDir,
            "https://example.test/CoolScript_bundle.zip",
            "CoolScript_bundle.zip",
            "CoolScript_bundle.zip",
            localFile);
        AssertEqual((bool?)true, isMatch, "CRC32 + uncompressed size remote verification should match identical local file");

        // Alter 1 byte locally -> should return false
        localBytes[0] = (byte)'X';
        File.WriteAllBytes(localFile, localBytes);
        bool? isMismatch = await arcSvc.VerifyLocalMatchesRemoteAsync(
            verifyDir,
            "https://example.test/CoolScript_bundle.zip",
            "CoolScript_bundle.zip",
            "CoolScript_bundle.zip",
            localFile);
        AssertEqual((bool?)false, isMismatch, "Modified local byte should fail remote CRC32 match");
    }

    private static void TestBackupAndRestore(string tempRoot)
    {
        string modsDir = Path.Combine(tempRoot, "Mods_Bk");
        string backupRoot = Path.Combine(tempRoot, "Backups_Bk");
        Directory.CreateDirectory(modsDir);

        string subFolder = Path.Combine(modsDir, "TestMod");
        Directory.CreateDirectory(subFolder);
        string pkgPath = Path.Combine(subFolder, "TestMod.package");
        File.WriteAllText(pkgPath, "ORIGINAL_CONTENT_V1");

        var bkSvc = new BackupService(modsDir, backupRoot);
        string session = bkSvc.CreateBackupSession("test");
        bkSvc.BackupItem(pkgPath, session);

        var list = bkSvc.ListBackups();
        AssertEqual(1, list.Count, "1 backup session recorded");
        AssertEqual(1, list[0].FileCount, "Backup has 1 file");

        // Overwrite with corrupted/newer file, then restore
        File.WriteAllText(pkgPath, "MODIFIED_V2");
        int restoredCount = bkSvc.RestoreBackup(session);
        AssertEqual(1, restoredCount, "RestoreBackup restores 1 file");
        AssertEqual("ORIGINAL_CONTENT_V1", File.ReadAllText(pkgPath), "Restored file content matches backup");

        // Delete outside backup root must throw security exception
        bool threwOutside = false;
        try { bkSvc.DeleteBackup(modsDir); }
        catch (InvalidOperationException) { threwOutside = true; }
        Assert(threwOutside, "DeleteBackup refuses paths outside backupRoot");
        Assert(bkSvc.DeleteBackup(session), "DeleteBackup deletes valid session inside backupRoot");
        AssertEqual(0, bkSvc.ListBackups().Count, "Backups empty after deletion");
    }

    private static async Task TestUpdaterEndToEndAsync(string tempRoot)
    {
        string modsDir = Path.Combine(tempRoot, "Mods_E2E");
        string backupRoot = Path.Combine(tempRoot, "Backups_E2E");
        string manifestPath = Path.Combine(tempRoot, "manifest_e2e.json");
        string modSubDir = Path.Combine(modsDir, "PoliticalWorld");
        Directory.CreateDirectory(modSubDir);

        string oldPkg = Path.Combine(modSubDir, "PoliticalWorld_Base.package");
        WriteDbpfPackage(oldPkg, "<M n=\"ModFileManifest\"><T n=\"version\">1.0.0</T></M>");
        string oldEdition = Path.Combine(modSubDir, "PoliticalWorld_CoreMod(RealisticEdition)_Required.package");
        WriteDbpfPackage(oldEdition, "<M n=\"Old\"/>");

        // Build remote zip containing updated Base + Fictional edition (mutually exclusive with RealisticEdition)
        using var zipMs = new MemoryStream();
        using (var zip = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(zip, "PoliticalWorld_Base.package", "DBPF_UPDATED_2.0");
            WriteZipEntry(zip, "PoliticalWorld_Fictional.package", "DBPF_FICTIONAL_2.0");
        }
        byte[] remoteZip = zipMs.ToArray();

        string postPageHtml = """
            <!DOCTYPE html>
            <html>
            <head><title>Political World v2.0.0 Update</title></head>
            <body>
              <h1>Political World v2.0.0</h1>
              <a href="https://files.example.test/PoliticalWorld_v2.0.0.zip">PoliticalWorld_v2.0.0.zip</a>
            </body>
            </html>
            """;

        using var stubHttp = new HttpClient(new StubHttpHandler((req, _) =>
        {
            string u = req.RequestUri?.AbsoluteUri ?? "";
            if (u.StartsWith("https://example.test/political-world", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(postPageHtml, Encoding.UTF8, "text/html")
                };
            }
            if (u == "https://files.example.test/PoliticalWorld_v2.0.0.zip")
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(remoteZip)
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                return resp;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var catalog = new CatalogService();
        var bkSvc = new BackupService(modsDir, backupRoot);
        var finder = new ModFinderService(catalog, new ModMetadataExtractor(catalog), stubHttp);
        var archives = new ArchiveAndFileService(stubHttp, finder);
        var updater = new ModUpdaterService(modsDir, manifestPath, bkSvc, catalog, finder, archives, stubHttp);

        var query = new ModQuery
        {
            Name = "PoliticalWorld_Base",
            FolderName = "PoliticalWorld",
            InstallFolder = "PoliticalWorld",
            DirectoryPath = modSubDir,
            FilePath = oldPkg,
            CurrentVersion = "1.0.0",
            Homepage = "https://example.test/political-world",
            UnitFiles = [oldPkg, oldEdition]
        };

        updater.PinVerifiedUrl(query, "https://example.test/political-world", "1.0.0", "PoliticalWorld_Base.package");
        AssertEqual("https://example.test/political-world", updater.GetPinnedUrl("PoliticalWorld_Base", "PoliticalWorld_Base.package"), "Pinning roundtrip");

        ModCheckResult check = await updater.CheckModUpdateAsync(query);
        AssertEqual(ModUpdateStatusCode.UpdateNeeded, check.StatusCode, "Check detects update 1.0.0 -> 2.0.0");
        Assert(check.HasUpdate, "HasUpdate true");
        AssertEqual("https://files.example.test/PoliticalWorld_v2.0.0.zip", check.DownloadUrl, "Direct zip download link resolved");

        List<string> updated = await updater.UpdateModAsync(query, check, OverwriteMode.All, skipBackup: false);
        Assert(updated.Count >= 1, "Update extracted files");
        AssertEqual("DBPF_UPDATED_2.0", File.ReadAllText(oldPkg), "Base package updated");
        Assert(!File.Exists(oldEdition), "Mutually exclusive Political World RealisticEdition removed when Fictional installed");
        Assert(File.Exists(Path.Combine(modSubDir, "PoliticalWorld_Fictional.package")), "New Fictional edition installed");
        AssertEqual(1, bkSvc.ListBackups().Count, "Auto-backup created before overwrite");
        Assert(updater.RemovePinnedUrl("PoliticalWorld_Base"), "RemovePinnedUrl succeeds");
    }

    private static void TestDiagnosticReport(string tempRoot)
    {
        string modsDir = Path.Combine(tempRoot, "Users_Alice_Mods");
        Directory.CreateDirectory(modsDir);
        File.WriteAllText(Path.Combine(modsDir, "Sample.package"), "DBPF");

        var scanner = new ModScannerService(modsDir, new CatalogService());
        var reporter = new DiagnosticReportService(tempRoot);
        string outPath = Path.Combine(tempRoot, "scan_report.txt");

        var output = reporter.GenerateReport(scanner, outputPath: outPath);
        Assert(File.Exists(output.ReportPath), "Report written to disk");
        Assert(!output.Content.Contains(modsDir, StringComparison.OrdinalIgnoreCase), "Report masks absolute Mods path");
        Assert(output.Content.Contains("[1]") && output.Content.Contains("[2]") && output.Content.Contains("[3]"),
            "Report contains all 3 sections");
    }

    private static void TestCreatorOrganizer(string tempRoot)
    {
        string modsDir = Path.Combine(tempRoot, "Mods_Org");
        string backupsDir = Path.Combine(tempRoot, "Backups_Org");
        string overridesPath = Path.Combine(tempRoot, "creator_overrides.json");
        Directory.CreateDirectory(modsDir);

        // 1. Metadata DBPF creator + script + companion .cfg inside an unorganized folder
        string unsortedDir = Path.Combine(modsDir, "RandomOldFolder");
        Directory.CreateDirectory(unsortedDir);
        string customPkg = Path.Combine(unsortedDir, "CoolMod.package");
        WriteDbpfPackage(customPkg, "<M n=\"ModFileManifest\"><T n=\"name\">Cool Mod</T><L n=\"creators\"><T>CoolCreator</T></L></M>");
        string customScr = Path.Combine(unsortedDir, "CoolMod.ts4script");
        File.WriteAllText(customScr, "zip");
        string companionCfg = Path.Combine(unsortedDir, "cool_settings.cfg");
        File.WriteAllText(companionCfg, "setting=1");

        // 2. Catalog-detected creator (Deaderpool) & Filename prefix ([PandaSama], ignoring [한글])
        string mcccDir = Path.Combine(modsDir, "MyMccc");
        Directory.CreateDirectory(mcccDir);
        File.WriteAllText(Path.Combine(mcccDir, "mc_cmd_center.ts4script"), "zip");
        File.WriteAllText(Path.Combine(modsDir, "[한글] [PandaSama] Toddler_Toy.package"), "DBPF");

        // 3. Unknown mod (remains unorganized when skipUnknown = true)
        string unknownFile = Path.Combine(modsDir, "Mystery_Item_01.package");
        File.WriteAllText(unknownFile, "DBPF");

        var catalog = new CatalogService();
        var extractor = new ModMetadataExtractor(catalog);
        var scanner = new ModScannerService(modsDir, catalog);
        var organizer = new CreatorOrganizerService(overridesPath, catalog);
        var backups = new BackupService(modsDir, backupsDir);

        List<ModUnit> units = scanner.ScanUpdatableModUnits();
        var metaCache = new Dictionary<string, ModMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (ModUnit u in units)
            metaCache[u.PrimaryFilePath] = extractor.Extract(u.PrimaryFilePath);

        ModUnit coolUnit = units.First(u => u.PrimaryFileName.Contains("CoolMod", StringComparison.OrdinalIgnoreCase));
        ModUnit mcccUnit = units.First(u => u.PrimaryFileName.Contains("mc_cmd_center", StringComparison.OrdinalIgnoreCase));
        ModUnit pandaUnit = units.First(u => u.PrimaryFileName.Contains("Toddler_Toy", StringComparison.OrdinalIgnoreCase));
        ModUnit mysteryUnit = units.First(u => u.PrimaryFileName.Contains("Mystery_Item_01", StringComparison.OrdinalIgnoreCase));

        AssertEqual("CoolCreator", organizer.ResolveCreator(coolUnit, metaCache[coolUnit.PrimaryFilePath]).Creator, "Metadata creator detected");
        AssertEqual("Deaderpool", organizer.ResolveCreator(mcccUnit, metaCache[mcccUnit.PrimaryFilePath]).Creator, "Catalog/Prefix creator Deaderpool");
        AssertEqual("PandaSama", organizer.ResolveCreator(pandaUnit, metaCache[pandaUnit.PrimaryFilePath]).Creator, "Tag creator PandaSama ignoring Korean tag");
        Assert(!organizer.ResolveCreator(mysteryUnit, metaCache[mysteryUnit.PrimaryFilePath]).IsKnown, "Mystery item initially unknown");

        // Test manual override persistence + removal
        organizer.SetCreatorOverride(mysteryUnit, "CustomAuthor");
        AssertEqual("CustomAuthor", organizer.ResolveCreator(mysteryUnit).Creator, "Manual creator override persisted");
        organizer.SetCreatorOverride(mysteryUnit, null);
        Assert(!organizer.ResolveCreator(mysteryUnit).IsKnown, "Removing override reverts to unknown");

        // URL domain creator inference
        AssertEqual("Kuttoe", CreatorOrganizerService.InferCreatorFromUrl("https://kuttoe.itch.io/mini-mods"), "URL domain creator Kuttoe");

        // Plan & Execute CreatorSingleFolder layout
        OrganizePlanSummary plan1 = organizer.BuildOrganizePlan(
            modsDir, units, CreatorOrganizeLayout.CreatorSingleFolder, skipUnknown: true, metadataCache: metaCache);
        AssertEqual(0, plan1.DepthWarningCount, "Zero script depth violations planned");
        AssertEqual(0, plan1.ConflictCount, "Zero filename conflicts in single folder test");
        Assert(plan1.TotalMoves >= 4, "Planned moves include mod files and companion cfg");

        OrganizeExecutionResult exec1 = organizer.ExecuteOrganizePlan(plan1, backups, createBackup: true);
        Assert(exec1.MovedFileCount >= 4, "Executed moves");
        Assert(File.Exists(Path.Combine(modsDir, "CoolCreator", "CoolMod.ts4script")), "CoolMod script moved to CoolCreator folder");
        Assert(File.Exists(Path.Combine(modsDir, "CoolCreator", "cool_settings.cfg")), "Companion .cfg moved with single-creator folder");
        Assert(File.Exists(Path.Combine(modsDir, "Deaderpool", "mc_cmd_center.ts4script")), "MCCC script moved to Deaderpool folder");
        Assert(File.Exists(unknownFile), "Unknown mod skipped and left untouched");
        Assert(!Directory.Exists(unsortedDir), "Empty source folder cleaned up");
        Assert(backups.ListBackups().Count >= 1, "Backup session created before moving");

        // Idempotent second check on CreatorSingleFolder -> 0 pending moves
        List<ModUnit> unitsAfterSingle = scanner.ScanUpdatableModUnits();
        metaCache.Clear();
        foreach (ModUnit u in unitsAfterSingle)
            metaCache[u.PrimaryFilePath] = extractor.Extract(u.PrimaryFilePath);
        OrganizePlanSummary planRepeat = organizer.BuildOrganizePlan(
            modsDir, unitsAfterSingle, CreatorOrganizeLayout.CreatorSingleFolder, skipUnknown: true, metadataCache: metaCache);
        AssertEqual(0, planRepeat.TotalMoves, "Second single-folder organize pass is idempotent (0 moves)");

        // Plan & Execute CreatorPrefixedModFolder layout ([Creator] Mod)
        OrganizePlanSummary planPrefixed = organizer.BuildOrganizePlan(
            modsDir, unitsAfterSingle, CreatorOrganizeLayout.CreatorPrefixedModFolder, skipUnknown: true, metadataCache: metaCache);
        AssertEqual(0, planPrefixed.DepthWarningCount, "Prefixed mod folder layout preserves 1-level script depth");
        OrganizeExecutionResult execPrefixed = organizer.ExecuteOrganizePlan(planPrefixed, backups, createBackup: false);
        Assert(execPrefixed.MovedFileCount >= 2, "Prefixed layout moves executed");

        // Conflict guard test: two different folders by same creator containing identical Shared.package filename
        string cMod1 = Path.Combine(modsDir, "ConflictA");
        string cMod2 = Path.Combine(modsDir, "ConflictB");
        Directory.CreateDirectory(cMod1);
        Directory.CreateDirectory(cMod2);
        File.WriteAllText(Path.Combine(cMod1, "SameFile.package"), "A");
        File.WriteAllText(Path.Combine(cMod2, "SameFile.package"), "B");
        var uA = new ModUnit { Name = "ModA", DisplayName = "ModA", FolderName = "ConflictA", DirectoryPath = cMod1, PrimaryFileName = "SameFile.package", PrimaryFilePath = Path.Combine(cMod1, "SameFile.package"), UnitFiles = [Path.Combine(cMod1, "SameFile.package")] };
        var uB = new ModUnit { Name = "ModB", DisplayName = "ModB", FolderName = "ConflictB", DirectoryPath = cMod2, PrimaryFileName = "SameFile.package", PrimaryFilePath = Path.Combine(cMod2, "SameFile.package"), UnitFiles = [Path.Combine(cMod2, "SameFile.package")] };
        organizer.SetCreatorOverride(uA, "ConflictAuthor");
        organizer.SetCreatorOverride(uB, "ConflictAuthor");

        OrganizePlanSummary conflictPlan = organizer.BuildOrganizePlan(modsDir, [uA, uB], CreatorOrganizeLayout.CreatorSingleFolder, skipUnknown: true);
        AssertEqual(2, conflictPlan.ConflictCount, "Duplicate filename across same creator flagged as conflict");
        OrganizeExecutionResult conflictExec = organizer.ExecuteOrganizePlan(conflictPlan, backups, createBackup: false);
        AssertEqual(0, conflictExec.MovedFileCount, "Conflicting files must not overwrite each other");
        AssertEqual("A", File.ReadAllText(Path.Combine(cMod1, "SameFile.package")), "Conflict source A intact");
        AssertEqual("B", File.ReadAllText(Path.Combine(cMod2, "SameFile.package")), "Conflict source B intact");

        // Regression assertions for creator heuristics & aliases (bracket skipping, Anim_/Pose_, -CUUPIDON-, byLorySims, Noir Studio, MOTHERLODESIMS)
        AssertEqual("CreatorTwo", organizer.ResolveCreator(new ModUnit { Name = "Lamp", FolderName = "Mods", PrimaryFileName = "[0.6 Scaled][CreatorTwo]DecorativeLamp.package" }).Creator, "[CreatorTwo] skips [0.6 Scaled]");
        AssertEqual("CreatorB", organizer.ResolveCreator(new ModUnit { Name = "PoseAnim", FolderName = "Mods", PrimaryFileName = "Anim_CreatorB_DancePack.package" }).Creator, "Anim_<Creator> extracts CreatorB");
        AssertEqual("LorySims", organizer.ResolveCreator(new ModUnit { Name = "Ford", FolderName = "Mods", PrimaryFileName = "FordF-150byLorySims.package" }).Creator, "150byLorySims extracts LorySims");
        AssertEqual("LorySims", organizer.ResolveCreator(new ModUnit { Name = "Jaguar", FolderName = "Mods", PrimaryFileName = "JaguarF-TypebyLorySims.package" }).Creator, "JaguarF-TypebyLorySims extracts LorySims");
        AssertEqual("Beto", organizer.ResolveCreator(new ModUnit { Name = "ByBeto", FolderName = "Mods", PrimaryFileName = "By Beto - Classic Jacket.package" }).Creator, "By Beto - Title normalizes to Beto");
        AssertEqual("Noir Studio", organizer.ResolveCreator(new ModUnit { Name = "Noir", FolderName = "Mods", PrimaryFileName = "[0.6 Scaled] Noir Desk Lamp - 01.package" }).Creator, "[0.6 Scaled] Noir merges into Noir Studio");
        AssertEqual("CUUPIDON", organizer.ResolveCreator(new ModUnit { Name = "Cuup", FolderName = "Mods", PrimaryFileName = "-CUUPIDON-PosePack.package" }).Creator, "-CUUPIDON- extracts CUUPIDON");
        AssertEqual("MakerC", organizer.ResolveCreator(new ModUnit { Name = "Start", FolderName = "Mods", PrimaryFileName = "MakerC_startingpack_wonderfulwhims.package" }).Creator, "MakerC_startingpack_wonderfulwhims not falsely attributed to TURBODRIVER");
        AssertEqual("MOTHERLODESIMS", organizer.ResolveCreator(new ModUnit { Name = "Pent", FolderName = "Mods", PrimaryFileName = "MOTHERLODESIMS_Pentagram.package" }, new ModMetadata { InferredCreator = "MOTHERLODESIMS" }).Creator, "MOTHERLODESIMS InferredCreator accepted cleanly");
    }

    private static byte[] BuildStblPayload(IReadOnlyList<(uint Key, string Text)> entries)
    {
        using var ms = new MemoryStream();
        ms.Write("STBL"u8);
        Span<byte> hdr = stackalloc byte[17];
        BinaryPrimitives.WriteUInt16LittleEndian(hdr[0..2], 5); // version 5
        hdr[2] = 0; // compressed flag
        BinaryPrimitives.WriteUInt64LittleEndian(hdr[3..11], (ulong)entries.Count);
        ms.Write(hdr);
        Span<byte> rec = stackalloc byte[7];
        foreach (var (key, text) in entries)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            BinaryPrimitives.WriteUInt32LittleEndian(rec[0..4], key);
            rec[4] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(rec[5..7], (ushort)utf8.Length);
            ms.Write(rec);
            ms.Write(utf8);
        }
        return ms.ToArray();
    }

    private static void WriteDbpfEntries(string path, IReadOnlyList<(uint TypeId, uint InstEx, byte[] RawPayload)> chunks)
    {
        var compList = new List<(uint TypeId, uint InstEx, byte[] Comp, uint UncompLen)>(chunks.Count);
        foreach (var (typeId, instEx, raw) in chunks)
        {
            using var compMs = new MemoryStream();
            using (var zlib = new ZLibStream(compMs, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }
            compList.Add((typeId, instEx, compMs.ToArray(), (uint)raw.Length));
        }

        const uint headerSize = 96;
        uint cursor = headerSize;
        var offsets = new uint[compList.Count];
        for (int i = 0; i < compList.Count; i++)
        {
            offsets[i] = cursor;
            cursor += (uint)compList[i].Comp.Length;
        }
        uint indexOffset = cursor;

        byte[] header = new byte[headerSize];
        header[0] = (byte)'D';
        header[1] = (byte)'B';
        header[2] = (byte)'P';
        header[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), (uint)compList.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64, 4), indexOffset);

        byte[] indexData = new byte[4 + compList.Count * 32];
        BinaryPrimitives.WriteUInt32LittleEndian(indexData.AsSpan(0, 4), 0); // flags = 0
        for (int i = 0; i < compList.Count; i++)
        {
            int ep = 4 + i * 32;
            BinaryPrimitives.WriteUInt32LittleEndian(indexData.AsSpan(ep, 4), compList[i].TypeId);
            BinaryPrimitives.WriteUInt32LittleEndian(indexData.AsSpan(ep + 8, 4), compList[i].InstEx);
            BinaryPrimitives.WriteUInt32LittleEndian(indexData.AsSpan(ep + 16, 4), offsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(indexData.AsSpan(ep + 20, 4), (uint)compList[i].Comp.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(indexData.AsSpan(ep + 24, 4), compList[i].UncompLen);
        }

        using var fs = File.Create(path);
        fs.Write(header);
        foreach (var item in compList)
            fs.Write(item.Comp);
        fs.Write(indexData);
    }

    private static void WriteDbpfPackage(string path, string xmlContent)
        => WriteDbpfEntries(path, [(0x545AC67Au, 0u, Encoding.UTF8.GetBytes(xmlContent))]);

    private static void WriteZipEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var es = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        es.Write(bytes);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request, cancellationToken));
    }
}
