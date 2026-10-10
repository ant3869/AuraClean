using System.IO;
using AuraClean.Helpers;
using AuraClean.Models;

namespace AuraClean.Services;

/// <summary>
/// Decides, deterministically, whether an uninstall leftover candidate belongs to the program
/// being removed, and explains why. Fail-closed rules:
/// <list type="bullet">
/// <item>Structural conflicts always block: a failed path-safety check, AuraClean's own data
/// folder, or a candidate that contains or sits inside another installed program's
/// install folder.</item>
/// <item>A name that also matches another installed program blocks, unless the candidate has
/// strong path evidence (it is inside the target's own recorded install folder).</item>
/// <item>No evidence blocks (abstain). Name-only evidence is "Review"; strong path evidence
/// with no conflict is "Recommended". Nothing is preselected by this class.</item>
/// </list>
/// Registry candidates are already identity-matched by <see cref="RegistryScannerService"/>
/// and backed up before deletion, so they are rated Review here and never blocked.
/// </summary>
public static class LeftoverOwnershipEvaluator
{
    private static readonly Lazy<string?> AuraCleanDataDir = new(() => PathSafety.Normalize(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraClean")));

    /// <summary>Assesses every item and clears the selection of any blocked item.</summary>
    public static void Annotate(
        IEnumerable<JunkItem> items, InstalledProgram target, IEnumerable<InstalledProgram> installedPrograms)
    {
        var others = OtherPrograms(target, installedPrograms);
        foreach (var item in items)
        {
            item.Ownership = Assess(item, target, others);
            if (item.Ownership.IsBlocked)
                item.IsSelected = false;
        }
    }

    public static OwnershipAssessment Assess(
        JunkItem item, InstalledProgram target, IEnumerable<InstalledProgram> installedPrograms)
    {
        if (item.Type == JunkType.OrphanedRegistryKey)
        {
            return new OwnershipAssessment(OwnershipVerdict.Review,
                [new LeftoverEvidence(EvidenceStrength.Medium,
                    "Registry entry matched by product identity; it is backed up before removal.")],
                []);
        }

        return AssessPath(item.Path, isDirectory: item.Type == JunkType.RemnantDirectory,
            target, OtherPrograms(target, installedPrograms));
    }

    /// <summary>Assesses a file or directory candidate. <paramref name="otherPrograms"/> must
    /// exclude the target (see <see cref="OtherPrograms"/>).</summary>
    public static OwnershipAssessment AssessPath(
        string candidatePath, bool isDirectory, InstalledProgram target, IReadOnlyList<InstalledProgram> otherPrograms)
    {
        var evidence = new List<LeftoverEvidence>();
        var structural = new List<string>();
        var contention = new List<string>();

        var path = LongPathHelper.Canonicalize(candidatePath);
        if (path == null)
            return Blocked(evidence, "The path is empty or not fully qualified.");

        // ── Structural safety (always blocks) ──
        var safe = isDirectory
            ? PathSafety.IsSafeToDeleteDirectory(path, out var reason)
            : PathSafety.IsSafeToDeleteFile(path, out reason);
        if (!safe)
            structural.Add(reason);

        if (AuraCleanDataDir.Value is { } own && PathSafety.IsSameOrUnder(path, own))
            structural.Add("It is AuraClean's own data folder.");

        foreach (var other in otherPrograms)
        {
            foreach (var otherRoot in OwnedRoots(other))
            {
                if (PathSafety.IsSameOrUnder(otherRoot, path))
                {
                    structural.Add($"It contains the install folder of installed program '{other.DisplayName}'.");
                    break;
                }
                if (PathSafety.IsSameOrUnder(path, otherRoot))
                {
                    structural.Add($"It is inside the install folder of installed program '{other.DisplayName}'.");
                    break;
                }
            }
        }

        // ── Evidence for the target ──
        var targetInstall = UsableInstallLocation(target);
        if (targetInstall != null && PathSafety.IsSameOrUnder(path, targetInstall))
        {
            evidence.Add(new LeftoverEvidence(EvidenceStrength.Strong,
                $"It is inside the install folder recorded by the program's uninstall entry ({targetInstall})."));
        }

        var name = isDirectory ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var normalizedName = UninstallerService.NormalizeForTrace(name);
        var nameEvidence = NameEvidence(name, normalizedName, target);
        if (nameEvidence != null)
            evidence.Add(nameEvidence);

        // ── Name contention with other installed programs ──
        if (nameEvidence != null)
        {
            foreach (var other in otherPrograms)
            {
                if (NameMatchesProgram(normalizedName, other))
                    contention.Add($"The name '{name}' also matches installed program '{other.DisplayName}', so it may be shared.");
            }
        }

        if (structural.Count > 0)
            return new OwnershipAssessment(OwnershipVerdict.Blocked, evidence, structural.Concat(contention).ToList());

        var hasStrong = evidence.Any(e => e.Strength == EvidenceStrength.Strong);
        if (contention.Count > 0 && !hasStrong)
            return new OwnershipAssessment(OwnershipVerdict.Blocked, evidence, contention);

        if (evidence.Count == 0)
            return Blocked(evidence, "No evidence links it to the program.");

        return new OwnershipAssessment(hasStrong ? OwnershipVerdict.Recommended : OwnershipVerdict.Review,
            evidence, []);
    }

    /// <summary>Every installed program except the target's own uninstall entry.</summary>
    public static IReadOnlyList<InstalledProgram> OtherPrograms(
        InstalledProgram target, IEnumerable<InstalledProgram> installedPrograms) =>
        installedPrograms.Where(p => !IsSameEntry(p, target)).ToList();

    internal static bool IsSameEntry(InstalledProgram a, InstalledProgram b) =>
        ReferenceEquals(a, b) ||
        (!string.IsNullOrWhiteSpace(a.RegistryKeyPath) &&
         a.RegistryKeyPath.Equals(b.RegistryKeyPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every folder an installed program demonstrably occupies: its InstallLocation plus the
    /// folders of its DisplayIcon and uninstaller executables. Many entries leave
    /// InstallLocation blank, so the executable folders are what reveal a program living
    /// inside a shared vendor folder. Unusable locations (Windows dir, protected roots,
    /// e.g. msiexec in System32) are dropped.
    /// </summary>
    internal static IEnumerable<string> OwnedRoots(InstalledProgram program)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (UsableInstallLocation(program) is { } install)
            roots.Add(install);
        foreach (var command in new[] { program.DisplayIcon, program.UninstallString, program.QuietUninstallString })
        {
            var exe = ExtractExecutablePath(command);
            var dir = exe == null ? null : Path.GetDirectoryName(exe);
            if (UsableLocation(dir) is { } usable)
                roots.Add(usable);
        }
        return roots;
    }

    /// <summary>Pulls the executable path out of a DisplayIcon ("x.exe,0") or command line.</summary>
    internal static string? ExtractExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var s = command.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
            return s[..(exeIdx + 4)];
        var comma = s.LastIndexOf(',');
        return comma > 0 ? s[..comma] : s;
    }

    /// <summary>
    /// An install location usable as ownership evidence. Locations that are drive roots,
    /// protected roots (e.g. "C:\Program Files" from a badly authored entry) or inside the
    /// Windows directory prove nothing and are ignored.
    /// </summary>
    internal static string? UsableInstallLocation(InstalledProgram program) => UsableLocation(program.InstallLocation);

    private static string? UsableLocation(string? rawPath)
    {
        var location = LongPathHelper.Canonicalize(rawPath);
        if (location == null || PathSafety.IsProtectedRoot(location) || PathSafety.IsWithinWindowsDirectory(location))
            return null;
        return location;
    }

    private static LeftoverEvidence? NameEvidence(string name, string normalizedName, InstalledProgram target)
    {
        if (normalizedName.Length == 0)
            return null;

        var fullName = UninstallerService.NormalizeForTrace(target.DisplayName);
        if (fullName.Length >= 4 && normalizedName == fullName)
            return new LeftoverEvidence(EvidenceStrength.Medium,
                $"The name '{name}' matches the full program name '{target.DisplayName}'.");

        var terms = UninstallerService.BuildRemnantDirectorySearchTerms(target.DisplayName, target.Publisher);
        if (terms.Contains(normalizedName))
            return new LeftoverEvidence(EvidenceStrength.Weak,
                $"The name '{name}' matches one word of '{target.DisplayName}'.");

        if (UninstallerService.MatchesAnySearchTerm(name, terms))
            return new LeftoverEvidence(EvidenceStrength.Weak,
                $"The name '{name}' contains part of '{target.DisplayName}'.");

        return null;
    }

    private static bool NameMatchesProgram(string normalizedName, InstalledProgram program)
    {
        if (normalizedName.Length == 0)
            return false;
        if (normalizedName == UninstallerService.NormalizeForTrace(program.DisplayName))
            return true;
        var terms = UninstallerService.BuildRemnantDirectorySearchTerms(program.DisplayName, program.Publisher);
        return terms.Contains(normalizedName);
    }

    private static OwnershipAssessment Blocked(List<LeftoverEvidence> evidence, string reason) =>
        new(OwnershipVerdict.Blocked, evidence, [reason]);
}
