using AuraClean.Models;

namespace AuraClean.Services;

public static class CleanupModePolicy
{
    public static bool IsNormalModeJunkType(JunkType type) => type switch
    {
        JunkType.TempFile or
        JunkType.WindowsUpdateCache or
        JunkType.Prefetch or
        JunkType.CrashDump or
        JunkType.BranchCache or
        JunkType.ThumbnailCache or
        JunkType.BrowserCache or
        JunkType.DeliveryOptimization or
        JunkType.WindowsErrorReporting or
        JunkType.FontCache or
        JunkType.LogFile => true,
        _ => false
    };
}
