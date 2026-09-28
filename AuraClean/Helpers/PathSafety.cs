using System.IO;

namespace AuraClean.Helpers;

/// <summary>
/// Central guard rails for every destructive file-system operation.
/// Prevents deleting drive roots, OS folders, user-profile roots, and known folders,
/// and provides enumeration options that never follow junctions or symlinks.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// Recursive enumeration that skips reparse points (junctions / symlinks) and
    /// silently ignores inaccessible directories instead of aborting the whole walk.
    /// </summary>
    public static readonly EnumerationOptions RecursiveNoReparse = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    /// <summary>Single-level variant of <see cref="RecursiveNoReparse"/>.</summary>
    public static readonly EnumerationOptions TopLevelNoReparse = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    private static readonly Lazy<string> WindowsDirectoryLazy = new(() =>
        Normalize(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\Windows");

    private static readonly Lazy<HashSet<string>> ProtectedRootsLazy = new(BuildProtectedRoots);

    private static readonly Lazy<string[]> SystemCriticalDirectoriesLazy = new(BuildSystemCriticalDirectories);

    /// <summary>The Windows installation directory (e.g. C:\Windows), normalized.</summary>
    public static string WindowsDirectory => WindowsDirectoryLazy.Value;

    /// <summary>
    /// Returns a fully-qualified path without trailing separators, or null when the
    /// input is empty, relative, or malformed.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var trimmed = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(trimmed))
                return null;

            var full = Path.GetFullPath(trimmed);
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root) && full.Length <= root.Length)
                return root;

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="path"/> equals or is nested inside <paramref name="parent"/>.</summary>
    public static bool IsSameOrUnder(string path, string parent)
    {
        var p = Normalize(path);
        var root = Normalize(parent);
        if (p == null || root == null)
            return false;

        if (p.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for drive roots such as C:\ or UNC share roots.</summary>
    public static bool IsDriveRoot(string path)
    {
        var p = Normalize(path);
        if (p == null)
            return false;

        var root = Path.GetPathRoot(p);
        return !string.IsNullOrEmpty(root) &&
               p.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the path is the Windows directory or anything beneath it.</summary>
    public static bool IsWithinWindowsDirectory(string path) => IsSameOrUnder(path, WindowsDirectory);

    /// <summary>
    /// True when the path is (or lives inside) a location that must never be bulk-scanned for
    /// deletion candidates: the Windows directory, Program Files, ProgramData\Microsoft,
    /// System Volume Information, and the recycle bin store.
    /// </summary>
    public static bool IsSystemCriticalLocation(string path)
    {
        var p = Normalize(path);
        if (p == null)
            return true;

        foreach (var dir in SystemCriticalDirectoriesLazy.Value)
        {
            if (IsSameOrUnder(p, dir))
                return true;
        }

        var name = Path.GetFileName(p);
        return name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("$WinREAgent", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Recovery", StringComparison.OrdinalIgnoreCase) && IsDriveRoot(Path.GetDirectoryName(p) ?? string.Empty);
    }

    /// <summary>
    /// True when the path is a protected root (drive root, OS folder, profile root, known folder)
    /// or an ancestor of one. Deleting such a path would damage the OS or wipe user data.
    /// </summary>
    public static bool IsProtectedRoot(string path)
    {
        var p = Normalize(path);
        if (p == null || IsDriveRoot(p))
            return true;

        foreach (var root in ProtectedRootsLazy.Value)
        {
            if (p.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;

            // An ancestor of a protected root (e.g. C:\Users) is equally dangerous.
            if (IsSameOrUnder(root, p))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that a directory may be deleted recursively.
    /// Rejects relative paths, protected roots, anything inside the Windows directory,
    /// and reparse points (which would redirect the delete elsewhere).
    /// </summary>
    public static bool IsSafeToDeleteDirectory(string path, out string reason)
    {
        var p = Normalize(path);
        if (p == null)
        {
            reason = "Path is empty or not fully qualified.";
            return false;
        }

        if (IsProtectedRoot(p))
        {
            reason = $"'{p}' is a protected system or user folder.";
            return false;
        }

        if (IsWithinWindowsDirectory(p))
        {
            reason = $"'{p}' is inside the Windows directory.";
            return false;
        }

        try
        {
            if (Directory.Exists(p) && new DirectoryInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reason = $"'{p}' is a junction or symbolic link.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"'{p}' could not be inspected: {ex.Message}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Validates that a single file may be deleted or overwritten by a user-driven tool.
    /// Blocks OS files and files marked with the System attribute.
    /// </summary>
    public static bool IsSafeToDeleteFile(string path, out string reason)
    {
        var p = Normalize(path);
        if (p == null)
        {
            reason = "Path is empty or not fully qualified.";
            return false;
        }

        if (IsWithinWindowsDirectory(p))
        {
            reason = $"'{Path.GetFileName(p)}' is a Windows system file.";
            return false;
        }

        try
        {
            if (File.Exists(p) && File.GetAttributes(p).HasFlag(FileAttributes.System))
            {
                reason = $"'{Path.GetFileName(p)}' is marked as a system file.";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"'{Path.GetFileName(p)}' could not be inspected: {ex.Message}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static HashSet<string> BuildProtectedRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            var normalized = Normalize(path);
            if (normalized != null)
                roots.Add(normalized);
        }

        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.SystemX86,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.CommonProgramFiles,
                     Environment.SpecialFolder.CommonProgramFilesX86,
                     Environment.SpecialFolder.CommonApplicationData,
                     Environment.SpecialFolder.CommonDocuments,
                     Environment.SpecialFolder.CommonDesktopDirectory,
                     Environment.SpecialFolder.CommonStartMenu,
                     Environment.SpecialFolder.CommonPrograms,
                     Environment.SpecialFolder.CommonStartup,
                     Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos,
                     Environment.SpecialFolder.Favorites,
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.StartMenu,
                     Environment.SpecialFolder.Programs,
                     Environment.SpecialFolder.Startup,
                     Environment.SpecialFolder.Templates,
                     Environment.SpecialFolder.Fonts,
                     Environment.SpecialFolder.AdminTools,
                 })
        {
            Add(Environment.GetFolderPath(folder));
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            Add(Path.Combine(profile, "Downloads"));
            Add(Path.Combine(profile, "AppData"));
            Add(Path.Combine(profile, "AppData", "LocalLow"));
            Add(Path.Combine(profile, "OneDrive"));
            Add(Path.GetDirectoryName(profile)); // C:\Users
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            Add(Path.Combine(localAppData, "Programs"));
            Add(Path.Combine(localAppData, "Temp"));
            Add(Path.Combine(localAppData, "Microsoft"));
        }

        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roamingAppData))
            Add(Path.Combine(roamingAppData, "Microsoft"));

        Add(Environment.GetEnvironmentVariable("PUBLIC"));
        Add(Path.GetTempPath());

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
            Add(Path.Combine(windows, "Temp"));

        return roots;
    }

    private static string[] BuildSystemCriticalDirectories()
    {
        var dirs = new List<string>();

        void Add(string? path)
        {
            var normalized = Normalize(path);
            if (normalized != null)
                dirs.Add(normalized);
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
        {
            Add(Path.Combine(programData, "Microsoft"));
            Add(Path.Combine(programData, "Packages"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            Add(Path.Combine(localAppData, "Packages"));
            Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
        }

        return [.. dirs.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static readonly HashSet<string> AppBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".exe", ".sys", ".pyd", ".so", ".node", ".jar", ".ocx", ".msi", ".cab", ".dylib", ".lib", ".bin",
    };

    // Folder segments that mark app installs, environments and package caches.
    private static readonly string[] AppFolderMarkers =
    [
        @"\AppData\", @"\site-packages\", @"\node_modules\", @"\venv\", @"\.venv\", @"\env\Lib\",
        @"\.cargo\", @"\.nuget\", @"\.gradle\", @"\.m2\", @"\go\pkg\", @"\.git\", @"\Steam\steamapps\",
    ];

    /// <summary>
    /// True when a file looks like part of an installed app or development environment (program
    /// binaries, or anything inside AppData, virtual environments, package caches, …). Identical
    /// copies of such files are usually required by each app that ships them, so deleting a
    /// "duplicate" can break that app. Used to warn before deletion; never blocks on its own.
    /// </summary>
    public static bool IsLikelyAppDependency(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (AppBinaryExtensions.Contains(Path.GetExtension(path)))
            return true;

        var normalized = path.Replace('/', '\\');
        return AppFolderMarkers.Any(marker => normalized.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
