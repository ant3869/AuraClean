using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AuraClean.Services;

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
}
