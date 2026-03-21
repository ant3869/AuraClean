# AuraClean — Delight Opportunity Assessment

## Context

| Dimension | Value |
|-----------|-------|
| **Users** | Mainstream Windows users (primary), power users (secondary) |
| **Brand personality** | Precise, trustworthy, powerful — not gamery, not scamware |
| **Aesthetic** | Refined minimal with industrial precision |
| **Reference touchstones** | 1Password (trust, polish), Linear (clarity), CleanMyMac (guided utility), Windows Security (serious but approachable) |
| **Anti-references** | Crypto dashboards, cyberpunk themes, "system booster" scamware, AI slop (purple-cyan glow) |
| **Tech** | .NET 8 WPF, MaterialDesign XAML Toolkit, CommunityToolkit.Mvvm |

---

## Delight Strategy: **Quiet Confidence**

AuraClean's delight should feel like a well-made tool acknowledging your effort — not a game rewarding you with confetti. Think: the satisfying _click_ of a Leica shutter, the precise _thunk_ of a Mercedes door. Moments of reassurance, not applause.

### Three delight principles for AuraClean:

1. **Confirm with gravitas** — After destructive actions, acknowledge what happened with precision ("847 files removed · 2.3 GB recovered"), not cheerfulness ("Yay! You cleaned up!")
2. **Reward attention to detail** — Hide thoughtful touches for observant users (keyboard shortcuts, contextual hover info, progressive data reveals)
3. **Reduce anxiety, don't create excitement** — System cleaners operate in a space of user anxiety ("Am I breaking something?"). Every delight moment should build confidence, not hype

---

## Current Delight Baseline: 6.5/10

| View | Score | Strength | Gap |
|------|-------|----------|-----|
| Dashboard | 6/10 | Hero typography, staggered entrances | No celebration on health improvement |
| Cleaner | 5/10 | Empty state with CTA | Silent completion, no result summary |
| Onboarding | 8/10 | Personal greeting, safety messaging | No step transition animations |
| Memory Boost | 7/10 | FAB with glow, dry-run toggle | No completion feedback beyond text |
| File Shredder | 5/10 | Algorithm selector with visual hierarchy | No drag-drop feedback, silent destruction |
| Threat Scanner | 8/10 | Scan mode cards, semantic icons | No reassurance during/after scan |
| Quarantine | 6/10 | Summary metric cards | No empty state, no action feedback |
| MainWindow | 7/10 | Animated nav bar, gradient logo | View transitions could be smoother |

### What's already working well:
- AnimationHelper infrastructure: entrance animations, stagger, exit, scale pulse, progress — all GPU-accelerated, reduced-motion-aware
- NavButton hover/active animations with gradient bar — feels responsive
- Button micro-interactions: hover lift (1.02x), press compress (0.97x)
- Design token system: purposeful naming, semantic color, clear hierarchy
- Onboarding personalization: username + machine name + timeline

### What's systematically missing:
- **Post-action confirmation** — Actions complete silently. Users don't know if something worked.
- **Result summaries** — After scanning/cleaning/boosting, there's no "here's what happened" moment.
- **Empty states with personality** — Some views have good ones (Cleaner, Shredder), others have none (Quarantine).
- **Progressive data reveals** — Numbers appear fully formed. No counting up, no before/after.
- **Contextual hover intelligence** — Buttons don't preview their effect ("Clean 847 files · ~2.3 GB").

---

## Priority Delight Opportunities

### Tier 1: High Impact, Moderate Effort

These address the biggest experiential gaps — moments where users currently feel uncertain or underwhelmed.

---

#### 1. Post-Action Result Cards

**Where**: Cleaner, Memory Boost, File Shredder, Browser Cleaner, Threat Scanner
**Emotional moment**: Accomplishment + reassurance ("Did it work? What just happened?")

**Current state**: Actions complete, status text updates, but there's no distinct "result" moment.

**Proposed behavior**: After an action completes, a result summary card appears with an entrance animation (fade + slide from bottom, 400ms). The card shows:

- **Cleaner**: "847 files removed · 2.3 GB recovered" — green mint accent on the value, muted description below
- **Memory Boost**: "Freed 1.8 GB" with freed amount animating from 0 (number count-up over 600ms). Show before/after as a subtle bar visualization.
- **File Shredder**: "5 files permanently destroyed · 12.4 MB overwritten" — amber/coral accent. The word "permanently" carries the gravity.
- **Threat Scanner**: "Scan complete · 0 threats found" with a shield-checkmark icon that fades in with a subtle scale (1.0 → 1.03 → 1.0). If threats found: count in coral with severity breakdown.

**Design direction**:
- Cards use `AuraSurfaceLight` background with a thin left-border accent (mint for success, amber for warning, coral for danger)
- Typography: result value in `AuraFontSizeXL` (24pt), description in `AuraFontSizeS` (13pt) muted
- Entrance: AnimationHelper.PlayEntranceAnimation with stagger
- Auto-dismiss after 8 seconds OR click to dismiss
- No confetti. No fireworks. Just clean data with confident color.

**Why this matters**: CleanMyMac excels here — every action gets a clear "here's what happened" moment. It's the #1 reason it feels premium. AuraClean actions currently vanish into the void.

---

#### 2. Animated Number Reveals

**Where**: Dashboard health score, Memory Boost stats, Cleaner totals, Quarantine counts
**Emotional moment**: Anticipation + satisfaction ("Watch the number arrive")

**Current state**: Numbers appear instantly at their final value.

**Proposed behavior**: Key metrics animate from 0 (or previous value) to their target value over 600ms with QuarticEase deceleration. The number increments in steps, not linearly — fast at first, decelerating near the target.

- **Health score** (72pt): Count from 0 to value on first load, from previous to new value on refresh
- **Memory freed**: Count up from 0 after boost completes
- **Files found/removed**: Count up during scan completion
- **Quarantine count**: Animate when items are added/removed

**Implementation note**: Use `DoubleAnimation` targeting a bound double property, with a `StringFormat` converter for display. AnimationHelper already has `AnimateProgressValue` — extend this pattern to arbitrary number targets.

**Why this matters**: Linear does this beautifully with issue counts and cycle times. The animation communicates "this is live data, computed just now" — not a static label.

---

#### 3. Drag-and-Drop Feedback (File Shredder)

**Where**: FileShredderView drop zone
**Emotional moment**: Control + commitment ("I'm choosing to destroy these")

**Current state**: `AllowDrop="True"` is set, but no visual feedback during drag-over.

**Proposed behavior**:
- **Drag enters**: Drop zone border transitions from `AuraBorder` to `AuraAccentPurple` with dashed stroke (200ms). Inner text changes to "Drop to add" in accent color. Background shifts to `AuraSurfaceElevated`.
- **Drag leaves**: Reverses smoothly (150ms).
- **Drop**: Brief border flash (accent → white → accent, 120ms) then files appear in list with staggered entrance.
- **Invalid file type**: Border flashes coral briefly, text shows "Unsupported file type" for 2 seconds.

**Design direction**:
- Border: 2px dashed when active, 1px solid default
- Icon: Shield-lock icon subtly scales to 1.05x during drag-over
- No bounce, no elastic — smooth scale deceleration only

---

#### 4. Empty State Personality (Quarantine, Cleanup History)

**Where**: Views that can genuinely be empty
**Emotional moment**: Reassurance ("Nothing here means everything's fine")

**Current state**: Cleaner and Shredder have basic empty states (icon + text + CTA). Quarantine has none visible.

**Proposed empty states**:

- **Quarantine (empty)**: Shield icon (violet tint) + "No quarantined files" + "Suspicious files will appear here when detected during scans" + "Run Threat Scan" button
- **Cleanup History (empty)**: Clock icon (muted) + "No cleanup history yet" + "Results from your system scans and cleanups will appear here"
- **Duplicate Finder (empty/pre-scan)**: Files icon + "Ready to find duplicates" + "Select folders to scan for duplicate files" — note: "Ready to find" is active/forward-leaning, not "Nothing here"

**Design direction**:
- Icon: 48px, muted (`AuraTextMuted` opacity), no drop shadow
- Headline: `AuraFontSizeL` (18pt), `AuraTextPrimary`
- Subtitle: `AuraFontSizeS` (13pt), `AuraTextSecondary`, max-width 360px for comfortable reading
- CTA: `AuraButtonOutlined` style, not primary — the empty state shouldn't scream
- Entrance: Fade + slide (AnimateOnVisible), icon with 100ms delay before text

---

### Tier 2: Medium Impact, Lower Effort

These are polish details that compound into a feeling of care.

---

#### 5. Smart Button Labels

**Where**: Cleaner ("Clean Selected"), Shredder ("Shred Selected"), Quarantine actions
**Emotional moment**: Informed confidence ("I know exactly what's about to happen")

**Current state**: Buttons say "Clean Selected" regardless of what's selected.

**Proposed behavior**: Buttons contextually update to show impact:
- "Clean Selected" → "Clean 847 files · 2.3 GB"
- "Shred Selected" → "Shred 5 files"
- "Purge Expired" → "Purge 3 expired items"

When nothing is selected: "Select items to clean" (disabled state with helpful text, not just grayed out)

**Implementation**: Bind button content to a computed property in the ViewModel that formats count + size.

---

#### 6. Progress Bar Intelligence

**Where**: Cleaner analysis, Threat Scanner, File Shredder
**Emotional moment**: Reduced anxiety ("How long will this take? What's happening?")

**Current state**: Indeterminate progress bars or simple percentage bars.

**Proposed behavior**:
- Show current action label below progress: "Scanning temp files..." → "Scanning browser caches..." → "Checking registry..."
- For determinate progress: Show time-based estimate only after 25% complete (early estimates are unreliable)
- Progress bar fill uses a subtle animated gradient (not pulsing — a slow, smooth gradient movement left-to-right within the filled area)

**Design direction**:
- Action label: `AuraFontSizeXS` (10pt), `AuraTextSecondary`, below the progress bar
- No percentage text overlaid on the bar itself — let the bar communicate visually
- Gradient: accent violet that shifts subtly (not rainbow, not pulsing)

---

#### 7. Health Score Trend Indicator

**Where**: Dashboard health score
**Emotional moment**: Progress recognition ("My system is getting better over time")

**Current state**: Single health score number with color based on value.

**Proposed behavior**: Small trend arrow next to the score:
- ↑ (mint green) when score improved since last check
- ↓ (coral) when score decreased
- — (muted) when unchanged
- Arrow appears with a 200ms fade after the score finishes counting up

**Design direction**:
- Arrow: 14pt, positioned right of the score number
- Tooltip on hover: "Up 8 points since last scan" — specific, not vague
- No arrow on first-ever launch (no previous data to compare)

---

#### 8. Keyboard Shortcut Discovery

**Where**: MainWindow navigation (Ctrl+1–10 already exist)
**Emotional moment**: Power user discovery ("Oh, I can do that faster")

**Current state**: Keyboard shortcuts exist but aren't visible anywhere.

**Proposed behavior**:
- Nav items show shortcut badge on hover: faint "Ctrl+1" text appears right-aligned in the nav button (fade in 150ms, fade out on mouse leave)
- Settings accessibility could list all shortcuts
- Tooltip on buttons that have shortcuts: "Quick Clean (Ctrl+Shift+C)"

**Design direction**:
- Shortcut text: `AuraFontSizeXS` (10pt), `AuraTextMuted`, right-aligned within nav button
- Only appears on hover — never clutters the default nav view
- Monospace font for the shortcut key combination

---

### Tier 3: Hidden Touches (Low effort, cumulative effect)

These are details users may never consciously notice — but they'll feel the absence.

---

#### 9. Contextual Tooltip Enrichment

**Where**: Throughout
**Emotional moment**: Trust ("This app knows what it's doing")

**Current state**: Some tooltips exist, most elements have none.

**Proposed additions**:
- Health score tooltip: "Based on disk space, memory usage, startup load, and system hygiene"
- Algorithm radio buttons: Expand description on hover (already partially done — ensure consistency)
- Locked file indicators: "This file is in use by [ProcessName]. Close it to clean." (already exists — verify it's consistent)
- Quarantine items: "Quarantined [X days ago]. Will expire in [Y days]."

---

#### 10. View Transition Signatures

**Where**: MainWindow content area transitions
**Emotional moment**: Spatial awareness ("I'm moving through a coherent space")

**Current state**: AnimationHelper.TransitionViews exists (fade out → fade+slide in) but may not be used consistently.

**Proposed behavior**: Ensure ALL view transitions use TransitionViews consistently. Outgoing view fades (180ms), incoming view slides up 16px + fades in (400ms). The stagger on child elements (header first, then content cards) creates a cascade that communicates "this view is assembling itself for you."

---

#### 11. Onboarding Step Transitions

**Where**: OnboardingView step navigation
**Emotional moment**: Progress + polish ("This is a quality experience")

**Current state**: Steps toggle visibility (abrupt show/hide).

**Proposed behavior**:
- Outgoing step: fade + slide left (exiting view moves away) — 200ms
- Incoming step: fade + slide from right (new content arrives) — 300ms with 100ms delay
- Step dots: current dot scales to 1.3x with accent color, inactive dots scale to 1.0x muted
- "Get Started" final step: broader reveal (400ms) with the button having a subtle pulse once (scale 1.0 → 1.03 → 1.0, 300ms, once)

---

## Anti-Patterns to Avoid

Given AuraClean's brand personality and audience, these delight choices would be **wrong**:

| Pattern | Why it's wrong for AuraClean |
|---------|------------------------------|
| Confetti on clean completion | Feels like a mobile game. System cleaners should be calm. |
| Bouncing icons | Dated, tacky. Quartic deceleration only. |
| Sound effects on every action | Annoying at scale. Maybe one subtle sound for major completions, but likely skip entirely. |
| Humorous error messages | "Oops! Something went wrong 😅" undermines the "trustworthy" personality. Be specific and helpful instead. |
| Gamification (streaks, badges) | AuraClean isn't a habit-forming app. Users run it when needed. |
| Skeleton screens with shimmer | WPF doesn't render these naturally. Use opacity + slide instead. |
| Animated mascot | Wrong audience. This is a precision tool, not a consumer toy. |
| Loading message humor | "Herding pixels..." is wrong for a system utility. "Scanning registry…" is better. |

---

## Delight Moment Map (User Journey)

```
LAUNCH                          FIRST USE                        ONGOING USE
  │                                │                                │
  ▼                                ▼                                ▼
┌──────────┐  ┌──────────────┐  ┌──────────┐  ┌──────────────┐  ┌──────────────┐
│ Window   │→ │ Onboarding   │→ │ Dashboard│→ │ First Scan   │→ │ Result Card  │
│ appears  │  │ (personal    │  │ (score   │  │ (progress    │  │ (count-up,   │
│ (smooth  │  │  greeting,   │  │  counts  │  │  label shows │  │  left-accent  │
│  fade-in)│  │  step flow)  │  │  up)     │  │  what's      │  │  bar, precise │
│          │  │              │  │          │  │  happening)  │  │  summary)    │
└──────────┘  └──────────────┘  └──────────┘  └──────────────┘  └──────────────┘
                                     │
                                     ▼
                              ┌──────────────┐
                              │ Health trend │
                              │ arrow (↑↓)   │
                              │ shows        │
                              │ improvement  │
                              └──────────────┘

DEEP FEATURES                     ERROR/EDGE CASES
  │                                │
  ▼                                ▼
┌──────────────┐               ┌──────────────┐
│ Shredder:    │               │ Locked file: │
│ drag-drop    │               │ specific     │
│ feedback,    │               │ process name │
│ "permanently │               │ + suggestion │
│  destroyed"  │               │              │
└──────────────┘               └──────────────┘
┌──────────────┐               ┌──────────────┐
│ Keyboard     │               │ Empty states:│
│ shortcut     │               │ reassuring   │
│ discovery    │               │ "nothing     │
│ on hover     │               │  here" =     │
│              │               │  "all good"  │
└──────────────┘               └──────────────┘
```

---

## Accessibility Requirements for Delight Features

All delight additions MUST:

- [ ] Respect `SystemParameters.ClientAreaAnimation` (AnimationHelper already checks this)
- [ ] Provide content equivalents for animated numbers (screen reader announces final value, not counting)
- [ ] Ensure result cards are keyboard-dismissible (Escape key)
- [ ] Maintain WCAG AA contrast on all result summary text
- [ ] Ensure trend arrows have text equivalents ("↑" has tooltip "Improved by 8 points")
- [ ] Drag-and-drop feedback includes keyboard alternative ("Press Enter to add files")
- [ ] Smart button labels are screen-reader friendly (full text, not icon-only)
- [ ] No color-only communication (trend uses arrow + color, not color alone)

---

## For Figma Design Team

**Research artifacts ready:**
- Delight audit with per-view scoring: this document
- Brand personality + anti-patterns: this document
- Design context: `docs/ux/color-strategy-audit.md` + `task.md`

**Next steps:**
1. Design result summary cards (Tier 1, #1) — one card template with variants for clean/boost/shred/scan
2. Design animated number component — specify count-up curve + font rendering at 72pt
3. Design drag-drop states for File Shredder — default, drag-over, drop, invalid
4. Design empty state template — icon + headline + subtitle + optional CTA
5. Design smart button label states — default, with-count, disabled
6. Design health trend indicator — arrow placement, tooltip content, sizing

**Key success metric:** Users should feel informed after every action. The question "Did it work?" should never cross their mind.
