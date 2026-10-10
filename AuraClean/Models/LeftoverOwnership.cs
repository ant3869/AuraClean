namespace AuraClean.Models;

/// <summary>How strongly a piece of evidence ties a leftover to the program being removed.</summary>
public enum EvidenceStrength
{
    /// <summary>A partial or single-word name match. A guess.</summary>
    Weak,
    /// <summary>The item's name equals the program's full display name.</summary>
    Medium,
    /// <summary>A path the program's own uninstall entry recorded (its InstallLocation).</summary>
    Strong
}

/// <summary>What the evaluator recommends for a leftover candidate.</summary>
public enum OwnershipVerdict
{
    /// <summary>Strong evidence and no contradictions. Still opt-in; never preselected.</summary>
    Recommended,
    /// <summary>Only name-based evidence. The user must judge.</summary>
    Review,
    /// <summary>Contradictory evidence, shared ownership, a failed safety check, or no evidence.
    /// Blocked items cannot be selected or removed.</summary>
    Blocked
}

/// <summary>One reason an item is believed to belong to the program.</summary>
public sealed record LeftoverEvidence(EvidenceStrength Strength, string Detail);

/// <summary>
/// The deterministic ownership verdict for one leftover candidate, with the evidence for it
/// and every reason it was blocked. Produced by <c>LeftoverOwnershipEvaluator</c>.
/// </summary>
public sealed record OwnershipAssessment(
    OwnershipVerdict Verdict,
    IReadOnlyList<LeftoverEvidence> Evidence,
    IReadOnlyList<string> BlockReasons)
{
    public bool IsBlocked => Verdict == OwnershipVerdict.Blocked;

    public EvidenceStrength? StrongestEvidence =>
        Evidence.Count == 0 ? null : Evidence.Max(e => e.Strength);

    /// <summary>One-line explanation for the UI.</summary>
    public string Summary => Verdict switch
    {
        OwnershipVerdict.Blocked => "Blocked: " + string.Join(" ", BlockReasons),
        OwnershipVerdict.Recommended => "Strong evidence: " + string.Join(" ",
            Evidence.Where(e => e.Strength == EvidenceStrength.Strong).Select(e => e.Detail)),
        _ => (StrongestEvidence == EvidenceStrength.Medium ? "Likely: " : "Name guess: ") +
             string.Join(" ", Evidence.Select(e => e.Detail))
    };
}
