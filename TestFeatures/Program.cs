using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AuraClean.Services;
using AuraClean.Models;

namespace TestFeatures;

class Program
{
    static int _pass;
    static int _fail;

    static void Assert(bool condition, string message)
    {
        if (condition)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  PASS: {message}");
            _pass++;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  FAIL: {message}");
            _fail++;
        }
        Console.ResetColor();
    }

    static async Task Main()
    {
        Console.WriteLine("╔════════════════════════════════════════╗");
        Console.WriteLine("║   AuraClean Feature Tests              ║");
        Console.WriteLine("╚════════════════════════════════════════╝\n");

        await TestSystemInfoService();
        await TestLargeFileFinderService();
        await TestFileShredderService();
        TestWinSxSParsing();
        await TestProtectedImageCleanup();
        TestUninstallerSafeMatching();
        await TestEmptyFolderFinder();

        Console.WriteLine("\n════════════════════════════════════════");
        Console.ForegroundColor = _fail == 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  RESULTS: {_pass} passed, {_fail} failed");
        Console.ResetColor();
        Console.WriteLine("════════════════════════════════════════");

        Environment.ExitCode = _fail > 0 ? 1 : 0;
    }

    static async Task TestSystemInfoService()
    {
        Console.WriteLine("═══ TEST SUITE 1: SystemInfoService ═══");

        try
        {
            var entries = await SystemInfoService.CollectAllAsync();

            Assert(entries != null, "CollectAllAsync returns non-null result");
            Assert(entries!.Count > 0, $"CollectAllAsync returned {entries.Count} entries (expected > 0)");

            // Check categories exist
            var categories = entries.Select(e => e.Category).Distinct().ToList();
            Assert(categories.Contains("Operating System"), "Contains 'Operating System' category");
            Assert(categories.Contains("Processor"), "Contains 'Processor' category");
            Assert(categories.Contains("Memory"), "Contains 'Memory' category");
            Assert(categories.Contains("Storage"), "Contains 'Storage' category");

            // Check entries have values
            var osName = entries.FirstOrDefault(e => e.Label.Contains("Name") && e.Category == "Operating System");
            Assert(osName != null && !string.IsNullOrEmpty(osName.Value), $"OS Name has value: '{osName?.Value}'");

            var cpuName = entries.FirstOrDefault(e => e.Label.Contains("Name") && e.Category == "Processor");
            Assert(cpuName != null && !string.IsNullOrEmpty(cpuName.Value), $"CPU Name has value: '{cpuName?.Value}'");

            // Test FormatAsText
            var text = SystemInfoService.FormatAsText(entries);
            Assert(!string.IsNullOrEmpty(text), $"FormatAsText produces text ({text.Length} chars)");
            Assert(text.Contains("Operating System"), "FormatAsText contains OS section");

            // Test with cancellation
            var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                await SystemInfoService.CollectAllAsync(ct: cts.Token);
                Assert(false, "Cancelled token should throw");
            }
            catch (OperationCanceledException)
            {
                Assert(true, "Cancellation token works correctly");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  EXCEPTION: {ex.Message}");
            Console.ResetColor();
            _fail++;
        }

        Console.WriteLine();
    }

    static async Task TestLargeFileFinderService()
    {
        Console.WriteLine("═══ TEST SUITE 2: LargeFileFinderService ═══");

        try
        {
            // Create temp test files
            var testDir = Path.Combine(Path.GetTempPath(), "AuraClean_LFF_Test");
            Directory.CreateDirectory(testDir);

            // Create a 2MB test file
            var bigFile = Path.Combine(testDir, "testlarge.dat");
            using (var fs = new FileStream(bigFile, FileMode.Create))
            {
                fs.SetLength(2 * 1024 * 1024); // 2MB
            }

            // Create a small file (should not be found with 1MB threshold)
            var smallFile = Path.Combine(testDir, "testsmall.txt");
            File.WriteAllText(smallFile, "small file");

            // Scan with 1MB threshold (1048576 bytes)
            var results = await LargeFileFinderService.ScanAsync(
                testDir,
                minimumSizeBytes: 1 * 1024 * 1024,
                maxResults: 100,
                includeSystemDirs: true,
                progress: new Progress<(int, int, string)>(),
                ct: CancellationToken.None);

            Assert(results.Files.Count >= 1, $"Found {results.Files.Count} large file(s) >= 1MB (expected >= 1)");

            var found = results.Files.FirstOrDefault(r => r.FileName == "testlarge.dat");
            Assert(found != null, "Found testlarge.dat in results");
            Assert(found?.SizeBytes >= 2 * 1024 * 1024, $"File size is correct: {found?.SizeBytes} bytes");
            Assert(!string.IsNullOrEmpty(found?.FormattedSize), $"FormattedSize works: '{found?.FormattedSize}'");
            Assert(found?.Category == "Other", $"Category for .dat is '{found?.Category}'");

            // Small file should not appear
            var foundSmall = results.Files.FirstOrDefault(r => r.FileName == "testsmall.txt");
            Assert(foundSmall == null, "Small file not in results (correct)");

            // Test cancellation
            var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                await LargeFileFinderService.ScanAsync(
                    testDir, 1024 * 1024, ct: cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            Assert(true, "Cancellation works without crash");

            // Cleanup
            Directory.Delete(testDir, true);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  EXCEPTION: {ex.Message}");
            Console.ResetColor();
            _fail++;
        }

        Console.WriteLine();
    }

    static async Task TestFileShredderService()
    {
        Console.WriteLine("═══ TEST SUITE 3: FileShredderService ═══");

        try
        {
            // Test each algorithm
            var algorithms = new[]
            {
                FileShredderService.ShredAlgorithm.QuickZero,
                FileShredderService.ShredAlgorithm.Random,
                FileShredderService.ShredAlgorithm.DoD3Pass,
                FileShredderService.ShredAlgorithm.Enhanced7Pass,
            };

            foreach (var algo in algorithms)
            {
                var testDir = Path.Combine(Path.GetTempPath(), $"AuraClean_Shred_{algo}");
                Directory.CreateDirectory(testDir);

                // Create test file with known content
                var testFile = Path.Combine(testDir, "secret.txt");
                File.WriteAllText(testFile, "This is secret data that should be securely deleted " +
                    new string('X', 10000));

                Assert(File.Exists(testFile), $"[{algo}] Test file created");

                var result = await FileShredderService.ShredFilesAsync(
                    new[] { testFile },
                    algo,
                    new Progress<(int, int, string)>(),
                    CancellationToken.None);

                Assert(result.FilesShredded == 1, $"[{algo}] File shredded (count={result.FilesShredded})");
                Assert(result.FilesFailed == 0, $"[{algo}] No failures (failed={result.FilesFailed})");
                Assert(!File.Exists(testFile), $"[{algo}] File no longer exists");
                Assert(result.TotalBytesOverwritten > 0, $"[{algo}] Bytes overwritten: {result.TotalBytesOverwritten}");
                Assert(result.Errors.Count == 0, $"[{algo}] No errors");

                // Cleanup
                if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
            }

            // Test with non-existent file
            var badResult = await FileShredderService.ShredFilesAsync(
                new[] { @"C:\nonexistent_file_auraclean_test.xyz" },
                FileShredderService.ShredAlgorithm.QuickZero,
                new Progress<(int, int, string)>(),
                CancellationToken.None);

            Assert(badResult.FilesFailed == 1, $"Non-existent file handled (failed={badResult.FilesFailed})");
            Assert(badResult.Errors.Count > 0, "Error message for missing file");

            // Test with empty list
            var emptyResult = await FileShredderService.ShredFilesAsync(
                Array.Empty<string>(),
                FileShredderService.ShredAlgorithm.Random,
                new Progress<(int, int, string)>(),
                CancellationToken.None);

            Assert(emptyResult.FilesShredded == 0, "Empty list returns 0 shredded");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  EXCEPTION: {ex.Message}");
            Console.ResetColor();
            _fail++;
        }

        Console.WriteLine();
    }

    static void TestWinSxSParsing()
    {
        Console.WriteLine("═══ TEST SUITE 4: WinSxS Parsing (ParseDismSize) ═══");

        try
        {
            // Test GB parsing
            var gbResult = FileCleanerService.ParseDismSize("Reclaimable Packages : 1.23 GB");
            Assert(gbResult == (long)(1.23 * 1_073_741_824),
                $"GB parsing: 1.23 GB => {gbResult} bytes (expected {(long)(1.23 * 1_073_741_824)})");

            // Test MB parsing
            var mbResult = FileCleanerService.ParseDismSize("Reclaimable Packages : 456 MB");
            Assert(mbResult == (long)(456 * 1_048_576),
                $"MB parsing: 456 MB => {mbResult} bytes (expected {(long)(456 * 1_048_576)})");

            // Test KB parsing
            var kbResult = FileCleanerService.ParseDismSize("Reclaimable Packages : 789 KB");
            Assert(kbResult == (long)(789 * 1024),
                $"KB parsing: 789 KB => {kbResult} bytes (expected {(long)(789 * 1024)})");

            // Test fractional MB
            var fracResult = FileCleanerService.ParseDismSize("Component Store Size : 2.50 MB");
            Assert(fracResult == (long)(2.50 * 1_048_576),
                $"Fractional MB: 2.50 MB => {fracResult} bytes");

            // Test empty string
            var emptyResult = FileCleanerService.ParseDismSize("");
            Assert(emptyResult == 0, $"Empty string => 0 (got {emptyResult})");

            // Test no colon
            var noColon = FileCleanerService.ParseDismSize("No colon here");
            Assert(noColon == 0, $"No colon => 0 (got {noColon})");

            // Test malformed size
            var badSize = FileCleanerService.ParseDismSize("Something : not-a-number GB");
            Assert(badSize == 0, $"Malformed size => 0 (got {badSize})");

            // Test JunkType.WinSxS category label
            var item = new JunkItem { Type = JunkType.WinSxS };
            Assert(item.Category == "Component Store (WinSxS)",
                $"JunkType.WinSxS category => '{item.Category}'");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  EXCEPTION: {ex.Message}");
            Console.ResetColor();
            _fail++;
        }

        Console.WriteLine();
    }

    static void TestUninstallerSafeMatching()
    {
        Console.WriteLine("═══ TEST SUITE 6: Safe Uninstaller Matching ═══");

        try
        {
            var terms = UninstallerService.BuildRemnantDirectorySearchTerms(
                "Google Chrome",
                "Google LLC");

            Assert(terms.Contains("googlechrome"), "Full product name is searchable");
            Assert(terms.Contains("chrome"), "Product-specific word is searchable");
            Assert(!terms.Contains("google"), "Publisher-only vendor term is not searchable");
            Assert(!UninstallerService.IsSafeRemnantDirectoryMatch("Google", terms),
                "Shared vendor folder does not match");
            Assert(UninstallerService.IsSafeRemnantDirectoryMatch("Chrome", terms),
                "Product folder matches");
            Assert(UninstallerService.IsSafeRemnantDirectoryMatch("Google Chrome", terms),
                "Full product folder matches");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  EXCEPTION: {ex.Message}");
            Console.ResetColor();
            _fail++;
        }

        Console.WriteLine();
    }

    static async Task TestEmptyFolderFinder()
    {
        Console.WriteLine("═══ TEST SUITE 7: EmptyFolderFinderService ═══");

        var testRoot = Path.Combine(Path.GetTempPath(), "AuraClean_EFF_Test");
        try
        {
            // Setup: create test directory structure
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);

            // Structure:
            //   root/
            //     emptyLeaf/           <- empty leaf
            //     emptyTree/           <- empty tree root
            //       childEmpty1/       <- empty child
            //       childEmpty2/       <- empty child
            //     nonEmpty/            <- has a file
            //       data.txt
            //     mixedParent/         <- has file + empty child
            //       keepMe.txt
            //       emptyChild/        <- empty leaf

            Directory.CreateDirectory(Path.Combine(testRoot, "emptyLeaf"));
            Directory.CreateDirectory(Path.Combine(testRoot, "emptyTree", "childEmpty1"));
            Directory.CreateDirectory(Path.Combine(testRoot, "emptyTree", "childEmpty2"));
            Directory.CreateDirectory(Path.Combine(testRoot, "nonEmpty"));
            File.WriteAllText(Path.Combine(testRoot, "nonEmpty", "data.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(testRoot, "mixedParent", "emptyChild"));
            File.WriteAllText(Path.Combine(testRoot, "mixedParent", "keepMe.txt"), "keep");

            // Test ScanAsync
            var results = await EmptyFolderFinderService.ScanAsync(
                new[] { testRoot },
                progress: null,
                ct: CancellationToken.None);

            Assert(results != null, "ScanAsync returns non-null");
            Assert(results.Count > 0, $"Found {results.Count} empty folder(s) (expected > 0)");

            // emptyLeaf should be found
            var leaf = results.FirstOrDefault(r => r.Name == "emptyLeaf");
            Assert(leaf != null, "Found 'emptyLeaf' as empty folder");
            Assert(leaf?.EmptySubfolderCount == 0, $"emptyLeaf subfolder count = {leaf?.EmptySubfolderCount} (expected 0)");

            // emptyTree should be found with child count 2 (collapsed)
            var tree = results.FirstOrDefault(r => r.Name == "emptyTree");
            Assert(tree != null, "Found 'emptyTree' as empty tree root");
            Assert(tree?.EmptySubfolderCount == 2, $"emptyTree subfolder count = {tree?.EmptySubfolderCount} (expected 2)");

            // childEmpty1/childEmpty2 should NOT be in results (collapsed into parent)
            var child1 = results.FirstOrDefault(r => r.Name == "childEmpty1");
            Assert(child1 == null, "childEmpty1 collapsed into emptyTree (not in results)");

            // nonEmpty should NOT be found
            var nonEmpty = results.FirstOrDefault(r => r.Name == "nonEmpty");
            Assert(nonEmpty == null, "'nonEmpty' (has file) not in results");

            // mixedParent should NOT be found (has a file)
            var mixed = results.FirstOrDefault(r => r.Name == "mixedParent");
            Assert(mixed == null, "'mixedParent' (has file) not in results");

            // But mixedParent/emptyChild SHOULD be found
            var emptyChild = results.FirstOrDefault(r => r.Name == "emptyChild");
            Assert(emptyChild != null, "Found 'emptyChild' inside mixedParent");

            // Test DisplayInfo
            Assert(leaf?.DisplayInfo == "Empty folder",
                $"Leaf DisplayInfo = '{leaf?.DisplayInfo}'");
            Assert(tree?.DisplayInfo == "Contains 2 empty subfolder(s)",
                $"Tree DisplayInfo = '{tree?.DisplayInfo}'");

            // Test DeleteAsync
            var selectedBefore = results.Where(r => r.IsSelected).ToList();
            Assert(selectedBefore.Count == 0, $"Empty folders default to unselected ({selectedBefore.Count} selected)");

            foreach (var item in results)
                item.IsSelected = true;

            var (deleted, failed) = await EmptyFolderFinderService.DeleteAsync(
                results,
                progress: null,
                ct: CancellationToken.None);

            Assert(deleted > 0, $"DeleteAsync deleted {deleted} folder(s)");
            Assert(failed == 0, $"DeleteAsync had {failed} failure(s) (expected 0)");

            // Verify actual deletion
            Assert(!Directory.Exists(Path.Combine(testRoot, "emptyLeaf")),
                "emptyLeaf actually deleted from disk");
            Assert(!Directory.Exists(Path.Combine(testRoot, "emptyTree")),
                "emptyTree actually deleted from disk");
            Assert(!Directory.Exists(Path.Combine(testRoot, "mixedParent", "emptyChild")),
                "emptyChild actually deleted from disk");

            // Non-empty should still exist
            Assert(Directory.Exists(Path.Combine(testRoot, "nonEmpty")),
                "nonEmpty still exists after delete");
            Assert(File.Exists(Path.Combine(testRoot, "nonEmpty", "data.txt")),
                "data.txt still exists after delete");
            Assert(File.Exists(Path.Combine(testRoot, "mixedParent", "keepMe.txt")),
                "keepMe.txt still exists after delete");

            // Test GetDefaultScanPaths
            var defaults = EmptyFolderFinderService.GetDefaultScanPaths();
            Assert(defaults.Count > 0, $"GetDefaultScanPaths returned {defaults.Count} paths (expected > 0)");
            Assert(defaults.All(Directory.Exists), "All default scan paths exist on disk");

            var broadRoots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            }.Where(p => !string.IsNullOrWhiteSpace(p))
             .Select(NormalizeRoot)
             .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert(defaults.Select(NormalizeRoot).All(p => !broadRoots.Contains(p)),
                "Default scan paths avoid broad user/profile/program roots");

            // Test cancellation
            var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                await EmptyFolderFinderService.ScanAsync(new[] { testRoot }, ct: cts.Token);
                Assert(false, "Cancelled scan should throw");
            }
            catch (OperationCanceledException)
            {
                Assert(true, "ScanAsync cancellation works correctly");
            }

            // Test scan on non-existent path (should not crash)
            var badResults = await EmptyFolderFinderService.ScanAsync(
                new[] { @"C:\NonExistent_AuraClean_Path_XYZ" },
                ct: CancellationToken.None);
            Assert(badResults.Count == 0, $"Non-existent path returns 0 results (got {badResults.Count})");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  EXCEPTION: {ex.Message}");
            Console.ResetColor();
            _fail++;
        }
        finally
        {
            // Cleanup
            try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { }
        }

        Console.WriteLine();
    }

    static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    static async Task TestProtectedImageCleanup()
    {
        Console.WriteLine("═══ TEST SUITE 5: Protected Image Cleanup ═══");

        var testRoot = Path.Combine(Path.GetTempPath(), "AuraClean_ImageProtection_Test");
        try
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
            Directory.CreateDirectory(testRoot);

            var screenshot = Path.Combine(testRoot, "screenshot.png");
            var tempFile = Path.Combine(testRoot, "cache.tmp");
            File.WriteAllBytes(screenshot, [1, 2, 3, 4]);
            File.WriteAllText(tempFile, "temporary data");

            var directoryItem = new JunkItem
            {
                Path = testRoot,
                Description = "Test directory with protected image",
                Type = JunkType.AbandonedFile,
                SizeBytes = 5,
                IsSelected = true
            };

            var (deleted, skipped, _, errors) = await FileCleanerService.CleanItemsAsync([directoryItem]);

            Assert(deleted == 1, $"Directory cleanup deleted non-image junk (deleted={deleted})");
            Assert(skipped == 1, $"Directory cleanup skipped protected image (skipped={skipped})");
            Assert(errors.Count == 0, "Directory cleanup had no errors");
            Assert(File.Exists(screenshot), "Protected screenshot still exists");
            Assert(!File.Exists(tempFile), "Temporary file was deleted");
            Assert(Directory.Exists(testRoot), "Directory remains because it contains protected media");
            Assert(directoryItem.IsLocked, "Directory item remains flagged when protected media is skipped");

            var photo = Path.Combine(testRoot, "photo.jpg");
            File.WriteAllBytes(photo, [5, 6, 7, 8]);
            var photoItem = new JunkItem
            {
                Path = photo,
                Description = "Direct photo file",
                Type = JunkType.TempFile,
                SizeBytes = 4,
                IsSelected = true
            };

            var (photoDeleted, photoSkipped, _, photoErrors) =
                await FileCleanerService.CleanItemsAsync([photoItem]);

            Assert(photoDeleted == 0, $"Direct photo was not deleted (deleted={photoDeleted})");
            Assert(photoSkipped == 1, $"Direct photo was skipped (skipped={photoSkipped})");
            Assert(photoErrors.Count == 0, "Direct photo cleanup had no errors");
            Assert(File.Exists(photo), "Direct photo still exists");
            Assert(photoItem.IsLocked, "Direct photo item is flagged as protected");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  EXCEPTION: {ex.Message}");
            Console.ResetColor();
            _fail++;
        }
        finally
        {
            try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { }
        }

        Console.WriteLine();
    }
}
