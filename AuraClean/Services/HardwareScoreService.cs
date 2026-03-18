namespace AuraClean.Services;

/// <summary>
/// Computes weighted hardware performance scores based on real-world benchmarks.
/// Each subsystem is scored 0–100 with a letter grade, and a weighted overall score is computed.
/// </summary>
public static class HardwareScoreService
{
    // ── Weight distribution (must sum to 1.0) ──
    private const double CpuWeight = 0.30;
    private const double MemoryWeight = 0.25;
    private const double GpuWeight = 0.20;
    private const double StorageWeight = 0.20;
    private const double SystemWeight = 0.05;

    public record CategoryScore(string Category, int Score, string Grade, string Summary, string Color);
    public record OverallResult(int OverallScore, string OverallGrade, List<CategoryScore> Categories);

    /// <summary>
    /// Compute all hardware scores from collected system info entries.
    /// </summary>
    public static OverallResult ComputeScores(List<SystemInfoService.InfoEntry> entries)
    {
        var categories = new List<CategoryScore>();

        int cpuScore = ScoreCpu(entries, out string cpuSummary);
        categories.Add(new CategoryScore("Processor", cpuScore, ToGrade(cpuScore), cpuSummary, GradeColor(cpuScore)));

        int memScore = ScoreMemory(entries, out string memSummary);
        categories.Add(new CategoryScore("Memory", memScore, ToGrade(memScore), memSummary, GradeColor(memScore)));

        int gpuScore = ScoreGpu(entries, out string gpuSummary);
        categories.Add(new CategoryScore("Graphics", gpuScore, ToGrade(gpuScore), gpuSummary, GradeColor(gpuScore)));

        int storageScore = ScoreStorage(entries, out string storageSummary);
        categories.Add(new CategoryScore("Storage", storageScore, ToGrade(storageScore), storageSummary, GradeColor(storageScore)));

        int sysScore = ScoreSystem(entries, out string sysSummary);
        categories.Add(new CategoryScore("System", sysScore, ToGrade(sysScore), sysSummary, GradeColor(sysScore)));

        double weighted = cpuScore * CpuWeight
                        + memScore * MemoryWeight
                        + gpuScore * GpuWeight
                        + storageScore * StorageWeight
                        + sysScore * SystemWeight;

        int overall = Math.Clamp((int)Math.Round(weighted), 0, 100);
        return new OverallResult(overall, ToGrade(overall), categories);
    }

    // ═══════════════════════════════════════════
    //                 CPU SCORING
    // ═══════════════════════════════════════════
    private static int ScoreCpu(List<SystemInfoService.InfoEntry> entries, out string summary)
    {
        int cores = ParseInt(FindValue(entries, "Processor", "Cores"));
        int threads = ParseInt(FindValue(entries, "Processor", "Logical Processors"));
        double clockGhz = ParseClockSpeed(FindValue(entries, "Processor", "Max Speed"));
        long l3Bytes = ParseCacheBytes(FindValue(entries, "Processor", "L3 Cache"));
        string cpuName = FindValue(entries, "Processor", "Name");

        // Core count score: real-world scaling (diminishing returns past 16)
        double coreScore = cores switch
        {
            <= 1 => 5,
            2 => 18,
            4 => 38,
            6 => 52,
            8 => 68,
            10 => 75,
            12 => 82,
            14 => 86,
            16 => 90,
            <= 24 => 94,
            <= 32 => 97,
            _ => 100
        };

        // Clock speed score: modern CPUs typically 3.0-5.8 GHz
        double speedScore = clockGhz switch
        {
            <= 0 => 0,
            < 1.5 => 10,
            < 2.0 => 20,
            < 2.5 => 30,
            < 3.0 => 42,
            < 3.5 => 55,
            < 4.0 => 68,
            < 4.5 => 78,
            < 5.0 => 86,
            < 5.5 => 93,
            _ => 100
        };

        // L3 cache score: 4MB→low, 64MB→excellent
        double cacheScore = l3Bytes switch
        {
            <= 0 => 15,
            < 4 * 1024 * 1024L => 25,
            < 8 * 1024 * 1024L => 40,
            < 16 * 1024 * 1024L => 55,
            < 32 * 1024 * 1024L => 70,
            < 64 * 1024 * 1024L => 85,
            _ => 100
        };

        // Hyperthreading/SMT bonus
        double htBonus = (threads > cores && cores > 0) ? 5 : 0;

        double raw = coreScore * 0.45 + speedScore * 0.35 + cacheScore * 0.15 + htBonus;
        int score = Math.Clamp((int)Math.Round(raw), 0, 100);

        summary = !string.IsNullOrEmpty(cpuName)
            ? $"{cores}C/{threads}T @ {clockGhz:F1} GHz"
            : $"{cores} cores, {clockGhz:F1} GHz";

        return score;
    }

    // ═══════════════════════════════════════════
    //               MEMORY SCORING
    // ═══════════════════════════════════════════
    private static int ScoreMemory(List<SystemInfoService.InfoEntry> entries, out string summary)
    {
        long totalBytes = ParseMemoryBytes(FindValue(entries, "Memory", "Total Physical"));
        double totalGb = totalBytes / (1024.0 * 1024 * 1024);

        // Gather RAM speed from slots
        int maxSpeed = 0;
        foreach (var e in entries.Where(e => e.Category == "Memory" && e.Label.Contains("Speed")))
        {
            int spd = ParseInt(e.Value.Replace("MHz", "").Trim());
            if (spd > maxSpeed) maxSpeed = spd;
        }

        // Capacity score: 8GB minimum for modern use, 64GB+ enthusiast
        double capScore = totalGb switch
        {
            <= 0 => 0,
            < 4 => 15,
            < 8 => 30,
            < 12 => 42,
            < 16 => 55,
            < 24 => 65,
            < 32 => 76,
            < 48 => 84,
            < 64 => 90,
            < 128 => 96,
            _ => 100
        };

        // Speed score: DDR4 2133-3600, DDR5 4800-7200+
        double speedScore = maxSpeed switch
        {
            <= 0 => 30, // unknown speed, assume basic
            < 2133 => 20,
            < 2400 => 30,
            < 2666 => 38,
            < 3000 => 45,
            < 3200 => 52,
            < 3600 => 60,
            < 4000 => 68,
            < 4800 => 75,
            < 5200 => 82,
            < 5600 => 88,
            < 6000 => 93,
            < 6400 => 96,
            _ => 100
        };

        double raw = capScore * 0.70 + speedScore * 0.30;
        int score = Math.Clamp((int)Math.Round(raw), 0, 100);

        summary = maxSpeed > 0
            ? $"{totalGb:F0} GB @ {maxSpeed} MHz"
            : $"{totalGb:F0} GB";

        return score;
    }

    // ═══════════════════════════════════════════
    //                GPU SCORING
    // ═══════════════════════════════════════════
    private static int ScoreGpu(List<SystemInfoService.InfoEntry> entries, out string summary)
    {
        // Find the best (primary) GPU — the first entry is already the best
        // because SystemInfoService sorts discrete GPUs first by VRAM.
        // Only score the primary GPU (no prefix = first GPU).
        string primaryName = FindValue(entries, "Graphics", "Name");
        long primaryVram = ParseVram(entries, prefixFilter: "");
        string resolution = FindValue(entries, "Graphics", "Resolution");

        // If there was no un-prefixed match, try any GPU entry as fallback
        if (string.IsNullOrEmpty(primaryName))
            primaryName = entries.FirstOrDefault(e => e.Category == "Graphics" && e.Label.Contains("Name"))?.Value ?? "";

        bool isDiscrete = IsDiscreteGpu(primaryName);
        double vramGb = primaryVram / (1024.0 * 1024 * 1024);

        // VRAM score - primary indicator of GPU capability
        double vramScore = vramGb switch
        {
            <= 0 => 5,
            < 1 => 15,
            < 2 => 25,
            < 3 => 35,
            < 4 => 45,
            < 6 => 55,
            < 8 => 68,
            < 10 => 75,
            < 12 => 82,
            < 16 => 88,
            < 20 => 93,
            < 24 => 96,
            _ => 100
        };

        // Discrete GPU bonus
        double discreteBonus = isDiscrete ? 20 : 0;

        // Integrated GPUs: cap at 45 unless they have significant shared memory
        double raw;
        if (!isDiscrete && vramGb < 2)
        {
            raw = Math.Min(vramScore * 0.6 + discreteBonus, 45);
        }
        else
        {
            raw = vramScore * 0.75 + discreteBonus * 0.25;
        }

        int score = Math.Clamp((int)Math.Round(raw), 0, 100);

        summary = !string.IsNullOrEmpty(primaryName)
            ? $"{primaryName.Trim()}"
            : "Unknown GPU";
        if (vramGb >= 1)
            summary += $" ({vramGb:F0} GB)";

        return score;
    }

    // ═══════════════════════════════════════════
    //              STORAGE SCORING
    // ═══════════════════════════════════════════
    private static int ScoreStorage(List<SystemInfoService.InfoEntry> entries, out string summary)
    {
        long totalCapacity = 0;
        long totalFree = 0;
        bool hasSsd = false;
        bool hasNvme = false;

        // Parse drive entries
        foreach (var e in entries.Where(e => e.Category == "Storage"))
        {
            // e.g. "123.4 GB free of 500.0 GB"
            if (e.Value.Contains("free of", StringComparison.OrdinalIgnoreCase))
            {
                var parts = e.Value.Split("free of", StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    totalFree += ParseStorageSize(parts[0]);
                    totalCapacity += ParseStorageSize(parts[1]);
                }
            }

            // Detect SSD/NVMe from interface or media type
            if (e.Label.Contains("Interface") || e.Label.Contains("Type"))
            {
                string val = e.Value.ToUpperInvariant();
                if (val.Contains("SSD") || val.Contains("SOLID"))
                    hasSsd = true;
                if (val.Contains("NVME") || val.Contains("NVM"))
                    hasNvme = true;
            }

            // Also detect from disk model names
            if (e.Label.StartsWith("Disk"))
            {
                string val = e.Value.ToUpperInvariant();
                if (val.Contains("SSD") || val.Contains("NVME") || val.Contains("NVM"))
                {
                    hasSsd = true;
                    if (val.Contains("NVME") || val.Contains("NVM"))
                        hasNvme = true;
                }
            }
        }

        double totalGb = totalCapacity / (1024.0 * 1024 * 1024);
        double freePercent = totalCapacity > 0 ? totalFree * 100.0 / totalCapacity : 0;

        // Capacity score
        double capScore = totalGb switch
        {
            <= 0 => 0,
            < 128 => 15,
            < 256 => 30,
            < 512 => 45,
            < 1024 => 60,
            < 2048 => 75,
            < 4096 => 88,
            _ => 100
        };

        // Free space health
        double freeScore = freePercent switch
        {
            < 5 => 10,
            < 10 => 25,
            < 20 => 45,
            < 30 => 60,
            < 50 => 75,
            < 70 => 88,
            _ => 100
        };

        // Disk type score
        double typeScore;
        if (hasNvme) typeScore = 100;
        else if (hasSsd) typeScore = 80;
        else typeScore = 30; // HDD

        double raw = capScore * 0.25 + freeScore * 0.30 + typeScore * 0.45;
        int score = Math.Clamp((int)Math.Round(raw), 0, 100);

        string diskType = hasNvme ? "NVMe" : hasSsd ? "SSD" : "HDD";
        summary = $"{totalGb:F0} GB total, {freePercent:F0}% free ({diskType})";

        return score;
    }

    // ═══════════════════════════════════════════
    //              SYSTEM SCORING
    // ═══════════════════════════════════════════
    private static int ScoreSystem(List<SystemInfoService.InfoEntry> entries, out string summary)
    {
        string osName = FindValue(entries, "Operating System", "Name");
        string build = FindValue(entries, "Operating System", "Build");
        string arch = FindValue(entries, "Operating System", "Architecture");
        string uptimeStr = FindValue(entries, "Operating System", "Uptime");

        int buildNum = ParseInt(build);
        bool is64 = arch.Contains("64");

        // Build recency (Windows 10 starts ~10240, Win11 starts 22000+)
        double buildScore = buildNum switch
        {
            <= 0 => 50,
            < 10240 => 20,   // Pre-Win10
            < 17763 => 35,   // Old Win10
            < 19041 => 50,   // Win10 2004-era
            < 19045 => 60,   // Win10 22H2
            < 22000 => 65,   // Late Win10
            < 22621 => 78,   // Win11 22H2
            < 22631 => 85,   // Win11 23H2
            < 26100 => 92,   // Win11 24H2
            _ => 100          // Latest
        };

        double archScore = is64 ? 100 : 40;

        double raw = buildScore * 0.70 + archScore * 0.30;
        int score = Math.Clamp((int)Math.Round(raw), 0, 100);

        summary = !string.IsNullOrEmpty(osName) ? osName.Trim() : "Unknown OS";

        return score;
    }

    // ═══════════════════════════════════════════
    //              GRADE HELPERS
    // ═══════════════════════════════════════════

    public static string ToGrade(int score) => score switch
    {
        >= 90 => "S",
        >= 80 => "A",
        >= 70 => "B",
        >= 60 => "C",
        >= 50 => "D",
        >= 35 => "E",
        _ => "F"
    };

    public static string GradeColor(int score) => score switch
    {
        >= 90 => "#00E5C3",  // Cyan/mint - exceptional
        >= 80 => "#5BF0D7",  // Mint - excellent
        >= 70 => "#7C5CFC",  // Violet - very good
        >= 60 => "#A78BFA",  // Light purple - good
        >= 50 => "#FFB74D",  // Amber - average
        >= 35 => "#FF8A65",  // Orange - below average
        _ => "#FF6B8A"       // Coral - needs upgrade
    };

    public static string GradeLabel(int score) => score switch
    {
        >= 90 => "Exceptional",
        >= 80 => "Excellent",
        >= 70 => "Very Good",
        >= 60 => "Good",
        >= 50 => "Average",
        >= 35 => "Below Average",
        _ => "Needs Upgrade"
    };

    // ═══════════════════════════════════════════
    //              PARSE HELPERS
    // ═══════════════════════════════════════════

    private static string FindValue(List<SystemInfoService.InfoEntry> entries, string category, string labelContains)
    {
        return entries
            .Where(e => e.Category == category && e.Label.Contains(labelContains, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value)
            .FirstOrDefault() ?? string.Empty;
    }

    private static int ParseInt(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        // Extract first number from string
        var match = System.Text.RegularExpressions.Regex.Match(val, @"(\d+)");
        return match.Success ? int.TryParse(match.Value, out int r) ? r : 0 : 0;
    }

    private static double ParseClockSpeed(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(val, @"([\d.]+)\s*GHz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ghz))
            return ghz;
        // Try MHz
        match = System.Text.RegularExpressions.Regex.Match(val, @"([\d.]+)\s*MHz", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mhz))
            return mhz / 1000.0;
        return 0;
    }

    private static long ParseCacheBytes(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(val, @"([\d.]+)\s*(GB|MB|KB)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return 0;
        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double num))
            return 0;
        return match.Groups[2].Value.ToUpperInvariant() switch
        {
            "GB" => (long)(num * 1024 * 1024 * 1024),
            "MB" => (long)(num * 1024 * 1024),
            "KB" => (long)(num * 1024),
            _ => 0
        };
    }

    private static long ParseMemoryBytes(string val) => ParseCacheBytes(val);

    private static long ParseVram(List<SystemInfoService.InfoEntry> entries, string prefixFilter = "")
    {
        foreach (var e in entries.Where(e => e.Category == "Graphics" && e.Label.Contains("VRAM")))
        {
            // When prefixFilter is empty, match only the primary GPU (label = "VRAM", not "GPU 2 VRAM")
            if (prefixFilter == "" && e.Label != "VRAM") continue;

            long bytes = ParseCacheBytes(e.Value);
            if (bytes > 0) return bytes;
        }
        return 0;
    }

    private static long ParseStorageSize(string val)
    {
        if (string.IsNullOrWhiteSpace(val)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(val.Trim(), @"([\d.]+)\s*(TB|GB|MB)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return 0;
        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double num))
            return 0;
        return match.Groups[2].Value.ToUpperInvariant() switch
        {
            "TB" => (long)(num * 1024L * 1024 * 1024 * 1024),
            "GB" => (long)(num * 1024 * 1024 * 1024),
            "MB" => (long)(num * 1024 * 1024),
            _ => 0
        };
    }

    private static bool IsDiscreteGpu(string gpuName)
    {
        if (string.IsNullOrWhiteSpace(gpuName)) return false;
        string upper = gpuName.ToUpperInvariant();
        // Known discrete GPU indicators
        return upper.Contains("GEFORCE") || upper.Contains("RADEON") ||
               upper.Contains("RTX") || upper.Contains("GTX") ||
               upper.Contains("QUADRO") || upper.Contains("TESLA") ||
               upper.Contains("ARC ") || upper.Contains("FIREPRO") ||
               upper.Contains("RX ") || upper.Contains("TITAN");
    }
}
