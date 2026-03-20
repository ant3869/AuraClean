# AuraClean — Frontend Design & Quality Audit Report

> **Audit Date:** March 19, 2026  
> **Scope:** 22 XAML views, 21 ViewModels, 31 Services, Converters, App-level resources  
> **Framework:** WPF (.NET 8.0) + MaterialDesignThemes 5.1.0  
> **Design System:** "Obsidian Aurora" custom dark theme with light mode support

---

## Anti-Patterns Verdict

**Verdict: MIXED — Some AI-slop tells present, but overall intentionality saves it.**

| Anti-Pattern Check | Status | Details |
|---|---|---|
| **AI Color Palette** (cyan-on-dark, purple-to-blue gradients) | **FAIL** | Primary palette is violet (#7C5CFC) + cyan (#00E5C3) on near-black. This is the canonical AI color scheme. |
| **Gradient text** | **WARN** | Brand logo uses gradient text (lines 72-73 MainWindow.xaml). Acceptable for branding but flagged. |
| **Dark mode with glowing accents** | **FAIL** | Default theme is dark (#080810) with purple DropShadowEffect glows on buttons and score text. |
| **Glassmorphism everywhere** | **PASS** | Despite having a style named "AuraGlassCard," no blur/backdrop effects are used. Cards use solid backgrounds. |
| **Hero metric layout** | **WARN** | Dashboard health gauge (big number center, supporting stats right) matches the hero metric template pattern. |
| **Identical card grids** | **WARN** | Dashboard stat cards follow identical structure: icon badge (44x44) + label + value, repeated 3x. |
| **Bounce/elastic easing** | **PASS** | No bounce or elastic animations found anywhere. |
| **Generic fonts** | **PASS** | Uses MaterialDesign font family consistently. Not Inter/Roboto/Arial. |
| **Nested cards** | **PASS** | Single-level card nesting only. No card-in-card anti-patterns. |
| **Rounded rectangles with drop shadows** | **WARN** | CornerRadius="14" + subtle borders is the primary card pattern. Generic but well-executed. |
| **Gradient text for "impact"** | **PASS** | No gradient text on metrics or headings (only brand logo). |

**Bottom line:** The violet-on-dark-with-cyan-accents color scheme is the strongest AI tell. A redesign of the accent palette toward more distinctive, less "AI-coded" colors (warm earth tones, muted jewel tones, editorial monochrome) would dramatically improve perceived originality.

---

## Executive Summary

| Severity | Count |
|----------|-------|
| **Critical** | 5 |
| **High** | 11 |
| **Medium** | 14 |
| **Low** | 8 |
| **Total** | **38** |

### Top 5 Critical Issues

1. **Zero accessibility markup** — Not a single `AutomationProperties` attribute across 22 views. Screen reader users are completely locked out.
2. **Mouse-only scan mode selection** — ThreatScannerView uses `MouseLeftButtonDown` events on 4 scan mode cards with no keyboard alternative.
3. **Event handler memory leaks** — MainWindow, ThreatScannerView, and SystemInfoView subscribe to `PropertyChanged` without ever unsubscribing.
4. **No FocusVisualStyle** — Custom-templated buttons, radio buttons, and interactive elements all lack keyboard focus indicators.
5. **Hard-coded colors bypass theme system** — 40+ inline hex values across views won't update on theme switch, breaking light mode.

### Overall Quality Score

| Dimension | Score | Notes |
|-----------|-------|-------|
| Visual Design | 7/10 | Cohesive dark theme, good hierarchy, but AI-coded color palette |
| Accessibility | 2/10 | Catastrophic — no ARIA, no focus styles, mouse-only interactions |
| Performance | 7/10 | Generally good; few DropShadowEffects; some threading concerns |
| Theming | 5/10 | Token system exists but 40+ hard-coded values break light mode |
| Responsiveness | 6/10 | Desktop-appropriate with MinWidth, but some fixed-width panels |
| Code Quality | 6/10 | Good MVVM structure; 50+ empty catch blocks; some memory leaks |

---

## Detailed Findings by Severity

---

### Critical Issues

#### CRIT-01: Complete Absence of Accessibility Markup
- **Location:** All 22 XAML view files
- **Category:** Accessibility
- **Description:** Zero instances of `AutomationProperties.Name`, `AutomationProperties.HelpText`, `AutomationProperties.LabeledBy`, or `AutomationProperties.LiveSetting` across the entire codebase. Interactive elements (buttons, checkboxes, toggles, data grids) have no screen reader support.
- **Impact:** Application is completely inaccessible to users relying on screen readers (Narrator, NVDA, JAWS). Violates WCAG 2.1 Level A.
- **WCAG:** 1.3.1 Info and Relationships, 4.1.2 Name/Role/Value
- **Recommendation:** Add `AutomationProperties.Name` to all interactive controls. Add `AutomationProperties.LiveSetting="Polite"` to status messages and progress updates.
- **Suggested command:** Dedicated accessibility hardening pass

#### CRIT-02: Mouse-Only Scan Mode Selection
- **Location:** [ThreatScannerView.xaml](AuraClean/Views/ThreatScannerView.xaml#L74), lines 74, 105, 136, 167
- **Category:** Accessibility
- **Description:** Four scan mode cards (Quick, Full, Custom, Browser) use `MouseLeftButtonDown` event handlers. These Border elements are not focusable and have no keyboard event handlers.
- **Impact:** Keyboard-only users cannot select a scan mode — the most critical function of the threat scanner. Complete feature lockout.
- **WCAG:** 2.1.1 Keyboard (Level A)
- **Recommendation:** Replace `MouseLeftButtonDown` on Borders with `Command` bindings on proper Button controls, or make Borders focusable with `KeyDown` handlers.
- **Suggested command:** Accessibility fix pass

#### CRIT-03: No Keyboard Focus Indicators
- **Location:** [App.xaml](AuraClean/App.xaml#L84) (AuraNavButton, AuraPrimaryButton, AuraOutlinedButton, AuraGhostButton styles)
- **Category:** Accessibility
- **Description:** All four custom button/radio-button styles use custom `ControlTemplate` but define no `FocusVisualStyle` or focus trigger. When tabbing through the UI, there is zero visual feedback indicating which element is focused.
- **Impact:** Keyboard navigation is effectively invisible. Users cannot tell where they are in the interface.
- **WCAG:** 2.4.7 Focus Visible (Level AA)
- **Recommendation:** Add `<Trigger Property="IsKeyboardFocused" Value="True">` triggers with visible border/glow to all custom control templates. Or set `FocusVisualStyle` on each style.
- **Suggested command:** Accessibility fix pass

#### CRIT-04: Event Handler Memory Leaks
- **Location:** [MainWindow.xaml.cs](AuraClean/Views/MainWindow.xaml.cs#L26), [ThreatScannerView.xaml.cs](AuraClean/Views/ThreatScannerView.xaml.cs#L20), [SystemInfoView.xaml.cs](AuraClean/Views/SystemInfoView.xaml.cs#L18)
- **Category:** Performance
- **Description:** `PropertyChanged` event handlers subscribed in constructors/Loaded events without corresponding unsubscribe in Closing/Unloaded events. Anonymous lambda handlers in ThreatScannerView and SystemInfoView can never be unsubscribed.
- **Impact:** Prevents garbage collection of large ViewModel graphs. Memory usage will grow with navigation between views. Can cause cascading memory pressure in long-running sessions.
- **Recommendation:** Store handler references; unsubscribe in `Unloaded`/`Closing`. For MainWindow, unsubscribe in `Closing`. For views, unsubscribe in `DataContextChanged` (when `NewValue` is null) or `Unloaded`.
- **Suggested command:** Performance optimization pass

#### CRIT-05: Potential Logic Bug in Threat Deletion
- **Location:** [ThreatScannerViewModel.cs](AuraClean/ViewModels/ThreatScannerViewModel.cs#L281)
- **Category:** Performance / Correctness
- **Description:** `DeleteSelectedAsync()` filters deleted items using `t.IsQuarantined` instead of tracking which items were actually deleted. This incorrectly removes quarantined items from the UI and leaves deleted items visible.
- **Impact:** Data integrity issue — users may believe threats are deleted when they're still present, and see quarantined items disappear unexpectedly.
- **Recommendation:** Track successfully deleted items by identity comparison with the service response rather than relying on the `IsQuarantined` property.
- **Suggested command:** Bug fix

---

### High-Severity Issues

#### HIGH-01: Hard-Coded Colors Bypassing Theme System (40+ Instances)
- **Location:** Multiple views — DashboardView (lines 91, 116, 144, 171), SystemInfoView (lines 17, 118-120, 135, 232-458), ThreatScannerView (lines 457-639), CleanupHistoryView (lines 254, 265), StorageMapView (line 203), StartupManagerView (line 257), SettingsView (lines 58, 359), and more
- **Category:** Theming
- **Description:** 40+ inline hex color values that duplicate design token colors but with alpha modifications (e.g., `#0E7C5CFC`, `#157C5CFC`, `#0E00E5C3`, `#33FF4444`). These won't respond to theme changes.
- **Impact:** Light mode is broken for these elements. Users switching to light theme will see dark-themed accent overlays on light backgrounds — creating illegible or jarring color combinations.
- **Recommendation:** Extract alpha-variant brushes to App.xaml as design tokens (e.g., `AuraVioletTint`, `AuraCyanTint`) with both dark and light variants managed by ThemeService.
- **Suggested command:** Theme normalization pass

#### HIGH-02: DataGrid Keyboard Navigation Undocumented
- **Location:** UninstallerView, LargeFileFinderView, DiskOptimizerView, FileRecoveryView, EmptyFolderFinderView, SoftwareUpdaterView, QuarantineView, StartupManagerView (8 views)
- **Category:** Accessibility
- **Description:** DataGrid controls with multi-selection, checkboxes, and row actions have no help text or instructions for keyboard navigation. Combined with missing focus indicators, these complex controls are nearly impossible to use without a mouse.
- **Impact:** Power users and accessibility-dependent users cannot efficiently navigate data-intensive views.
- **WCAG:** 2.1.1 Keyboard, 3.3.2 Labels or Instructions
- **Recommendation:** Add keyboard shortcut hints (e.g., "Space to select, Ctrl+A to select all") in view headers or as tooltips. Ensure row focus is visible.
- **Suggested command:** Accessibility fix pass

#### HIGH-03: Checkbox/RadioButton Touch Targets Below WCAG Minimum
- **Location:** All views using default WPF CheckBox and RadioButton controls
- **Category:** Accessibility / Responsive
- **Description:** Default WPF CheckBox hit area is approximately 18x18px. WCAG 2.5.8 recommends 44x44px minimum touch targets. RadioButtons in the nav sidebar have adequate height (42px) but checkboxes throughout lists are undersized.
- **Impact:** Difficult to interact with on touch-enabled Windows devices (Surface, convertible laptops). Frequent mis-taps on pen/touch input.
- **WCAG:** 2.5.8 Target Size (Level AAA), 2.5.5 Target Size (Level AA — 24px minimum)
- **Recommendation:** Wrap checkboxes in hit-test-transparent padding containers or increase the checkbox template's clickable area.
- **Suggested command:** Accessibility fix pass

#### HIGH-04: Icon-Only Buttons Without Accessible Names
- **Location:** FileShredderView "Remove" button (PackIcon.Kind="Close"), various icon-only actions in DataGrid rows
- **Category:** Accessibility
- **Description:** Multiple buttons display only a PackIcon with no text content and no `ToolTip` or `AutomationProperties.Name`. Screen readers will announce these as "button" with no context.
- **Impact:** Functionally invisible to screen reader users.
- **WCAG:** 4.1.2 Name, Role, Value (Level A)
- **Recommendation:** Add `ToolTip` and `AutomationProperties.Name` to all icon-only interactive elements.
- **Suggested command:** Accessibility fix pass

#### HIGH-05: No Transition Animations
- **Location:** All views
- **Category:** UX / Motion
- **Description:** Zero Storyboard, DoubleAnimation, ThicknessAnimation, or ColorAnimation elements found. View switching is instant with no entrance/exit transitions. State changes (scanning → complete, selected → deselected) have no visual feedback beyond property changes.
- **Impact:** The interface feels static and mechanical. States change without visual continuity, making it harder to track what changed. Missed opportunity for high-impact delight moments per the design skill guidelines.
- **Recommendation:** Add a single well-orchestrated page-entrance animation with staggered reveals. Add opacity transition to view switching. Use `VisualStateManager` for interactive state feedback.
- **Suggested command:** Motion/animation enhancement pass

#### HIGH-06: ObservableCollection Cross-Thread Updates
- **Location:** [CleanerViewModel.cs](AuraClean/ViewModels/CleanerViewModel.cs#L199) (Categories.Remove), [BrowserCleanerViewModel.cs](AuraClean/ViewModels/BrowserCleanerViewModel.cs#L75) (BrowserResults.Add), [SoftwareUpdaterViewModel.cs](AuraClean/ViewModels/SoftwareUpdaterViewModel.cs#L40) (Programs.Add)
- **Category:** Performance / Stability
- **Description:** ObservableCollection modifications inside async methods may execute on background thread continuations. WPF requires UI-bound collection changes on the dispatcher thread.
- **Impact:** Intermittent `InvalidOperationException` crashes ("This type of CollectionView does not support changes to its SourceCollection from a thread different from the Dispatcher thread").
- **Recommendation:** Wrap collection modifications in `Application.Current.Dispatcher.Invoke()` or use `BindingOperations.EnableCollectionSynchronization()`.
- **Suggested command:** Stability fix pass

#### HIGH-07: DispatcherTimer Resource Leak
- **Location:** [UninstallerViewModel.cs](AuraClean/ViewModels/UninstallerViewModel.cs#L44)
- **Category:** Performance
- **Description:** `OnSearchTextChanged` creates a new `DispatcherTimer` on every keystroke without disposing the previous one. Old timers are stopped but not disposed, and their event handlers remain subscribed.
- **Impact:** Memory accumulation during active search usage. Each keystroke leaks a timer + event handler reference.
- **Recommendation:** Reuse a single timer instance, resetting its interval on each keystroke. Or properly dispose the old timer before creating a new one.
- **Suggested command:** Performance optimization pass

#### HIGH-08: 50+ Empty Catch Blocks
- **Location:** Across 15+ files — BrowserCleanerService (6), FileCleanerService (10), DuplicateFinderService (6), ForceDeleteService (3), HeuristicScannerService (5), ViewModels (7+), others
- **Category:** Code Quality / Observability
- **Description:** Over 50 bare `catch { }` blocks silently swallow exceptions. While some are annotated with comments explaining intentional suppression (locked files, cleanup errors), many have no justification.
- **Impact:** Bugs are hidden. Users experience silent failures with no feedback. Debugging production issues becomes extremely difficult. A `DiagnosticLogger.Warn()` helper exists but is underutilized.
- **Recommendation:** Replace bare catch blocks with `catch (Exception ex) { DiagnosticLogger.Warn("context", ex); }` at minimum. Keep intentional suppression only for truly expected failures (file locks, cleanup of temp files).
- **Suggested command:** Code quality pass

#### HIGH-09: StaticResource vs DynamicResource for Theme Switching
- **Location:** All views — majority use `{StaticResource AuraTextBright}`, `{StaticResource AuraSurface}`, etc.
- **Category:** Theming
- **Description:** Most brush references use `StaticResource` instead of `DynamicResource`. While ThemeService modifies brush colors in-place (which works for non-frozen brushes), any frozen brushes that get replaced with new instances won't be picked up by StaticResource bindings.
- **Impact:** Theme switching may produce inconsistent results — some elements update, others don't, depending on whether the original brush was frozen. The sidebar gradient explicitly handles this case but other brushes may not.
- **Recommendation:** Audit which brushes are frozen at startup. For maximum reliability, switch critical bindings to `DynamicResource`, or ensure all brushes are created unfrozen.
- **Suggested command:** Theme normalization pass

#### HIGH-10: Color-Only Status Indicators
- **Location:** StartupManagerView impact badges, ThreatScannerView severity levels, DiskOptimizerView fragmentation percentages, SystemInfoView score grades
- **Category:** Accessibility
- **Description:** Status information is conveyed primarily through color (green=good, red=bad, amber=warning). While text labels exist alongside colors in most cases, some indicators rely solely on color differentiation.
- **Impact:** Users with color vision deficiency (8% of males) may not be able to distinguish severity levels.
- **WCAG:** 1.4.1 Use of Color (Level A)
- **Recommendation:** Add text labels, icons, or patterns alongside color indicators. E.g., prepend severity badges with "Critical:", "High:", etc.
- **Suggested command:** Accessibility fix pass

#### HIGH-11: Unsafe Type Conversion in Converters
- **Location:** [FileSizeConverter.cs](AuraClean/Converters/FileSizeConverter.cs#L155) (ScoreToWidthConverter, ScoreToAngleConverter), [HexToBrushConverter](AuraClean/Converters/FileSizeConverter.cs#L173)
- **Category:** Stability
- **Description:** `ScoreToWidthConverter` and `ScoreToAngleConverter` use `System.Convert.ToDouble(value)` which throws `InvalidCastException` or `FormatException` on invalid input. `HexToBrushConverter` can throw `FormatException` on malformed hex values.
- **Impact:** Binding errors during data loading could crash the converter pipeline, causing blank/missing UI elements.
- **Recommendation:** Use `double.TryParse()` with fallback values. Wrap hex parsing in try-catch.
- **Suggested command:** Stability fix pass

---

### Medium-Severity Issues

#### MED-01: AI Color Palette (Purple + Cyan on Dark)
- **Location:** [App.xaml](AuraClean/App.xaml#L26) — AuraVioletColor (#7C5CFC), AuraCyanColor (#00E5C3), AuraBgColor (#080810)
- **Category:** Anti-Pattern (Design)
- **Description:** The primary accent pair (purple + cyan/teal on near-black background) is the most stereotypically "AI-generated" color palette per the frontend-design skill. Combined with purple DropShadowEffect glows, this reads as generic sci-fi/tech aesthetic from 2024-2025 AI tools.
- **Impact:** Reduces perceived originality. Users familiar with AI-generated interfaces will immediately pattern-match this design.
- **Recommendation:** Consider shifting the primary accent to a more distinctive choice: warm amber/terracotta, muted sage/olive, editorial high-contrast monochrome with a single accent, or jewel-toned emerald/ruby. Retain the dark theme but re-tint the palette.
- **Suggested command:** Design refresh/rebrand pass

#### MED-02: Hero Metric Dashboard Pattern
- **Location:** [DashboardView.xaml](AuraClean/Views/DashboardView.xaml#L48) — large circular gauge with score number, supporting stat cards
- **Category:** Anti-Pattern (Design)
- **Description:** Dashboard follows the "hero metric" template: big number center stage (60px font, health score), supporting stats in cards to the right, action buttons below. This is a heavily-used AI dashboard pattern.
- **Impact:** Design feels templated despite good execution. Lacks the "unforgettable" quality the design skill demands.
- **Recommendation:** Consider alternative dashboard compositions: editorial/magazine layout with asymmetry, timeline-based health history, or a more organic/contextual information architecture that surfaces what matters most right now.
- **Suggested command:** Design refresh pass

#### MED-03: MainViewModel Does Not Implement IDisposable
- **Location:** [MainViewModel.cs](AuraClean/ViewModels/MainViewModel.cs#L50)
- **Category:** Performance
- **Description:** Creates 19+ child ViewModels as auto-properties but never disposes them. If any child ViewModel holds event subscriptions, timers, or file handles, they'll leak.
- **Impact:** Long-running sessions accumulate unreleased resources.
- **Recommendation:** Implement `IDisposable` on MainViewModel. Dispose child ViewModels in the `Dispose` method. Call from MainWindow.Closing.
- **Suggested command:** Performance optimization pass

#### MED-04: No Live Region Announcements for Status Changes
- **Location:** DashboardView (HealthCheckProgress), CleanerView (clean status), ThreatScannerView (scan progress), MemoryBoostView (boost result)
- **Category:** Accessibility
- **Description:** Status messages that update during operations (scanning, cleaning, boosting) have no live region equivalent. Screen readers won't announce progress changes.
- **Impact:** Accessibility users have no feedback during long-running operations.
- **WCAG:** 4.1.3 Status Messages (Level AA)
- **Recommendation:** Use `AutomationProperties.LiveSetting="Polite"` on status TextBlocks that update asynchronously.
- **Suggested command:** Accessibility fix pass

#### MED-05: Contrast Ratio Concerns for Dim/Muted Text
- **Location:** App.xaml — AuraTextDimColor (#7B7BA0) on AuraBgColor (#080810), AuraTextMutedColor (#3D3D5C) on AuraSurfaceColor (#0F0F1E)
- **Category:** Accessibility
- **Description:** Calculated contrast ratios:
  - `AuraTextDim` (#7B7BA0) on `AuraBg` (#080810): ~5.2:1 — passes AA for normal text
  - `AuraTextMuted` (#3D3D5C) on `AuraSurface` (#0F0F1E): ~2.4:1 — **fails WCAG AA (4.5:1 required)**
  - `AuraTextMuted` (#3D3D5C) on `AuraBg` (#080810): ~2.7:1 — **fails WCAG AA**
- **Impact:** Muted text (section labels like "CLEANUP", "ANALYZE", version number) is illegible for users with low vision.
- **WCAG:** 1.4.3 Contrast (Minimum) Level AA
- **Recommendation:** Increase `AuraTextMutedColor` brightness to at least #6060A0 (~4.5:1 ratio on dark surfaces). Or change these labels to `AuraTextSecondary`.
- **Suggested command:** Accessibility fix pass

#### MED-06: Identical Stat Card Pattern (Dashboard)
- **Location:** [DashboardView.xaml](AuraClean/Views/DashboardView.xaml#L104) — "Junk Found", "Programs Installed", "Last Cleaned" cards
- **Category:** Anti-Pattern (Design)
- **Description:** Three stat cards follow identical layout: 44x44 icon badge (rounded, tinted background) + label + large value. Same structure, same spacing, same sizes. Varies only by color and icon.
- **Impact:** Creates visual monotony. The eye scans past identical shapes without registering distinct information.
- **Recommendation:** Vary card sizes, layouts, or styles. Make the most important stat larger. Use different visual treatments for different data types (counters vs. dates vs. sizes).
- **Suggested command:** Design refresh pass

#### MED-07: Redundant BoolToVisibilityConverter Declarations
- **Location:** DashboardView.Resources, MainWindow.Resources, plus global declaration in App.xaml
- **Category:** Code Quality
- **Description:** `BoolToVisibilityConverter` is declared as a global resource (`BoolToVis`) in App.xaml, but also re-declared locally in DashboardView and MainWindow with the same key.
- **Impact:** Minor — functional duplication. Creates confusion about which converter instance is being used.
- **Recommendation:** Remove local declarations; use global `{StaticResource BoolToVis}` consistently.
- **Suggested command:** Code cleanup pass

#### MED-08: DropShadowEffect on Text (Performance)
- **Location:** [DashboardView.xaml](AuraClean/Views/DashboardView.xaml#L91) — health score number, [MemoryBoostView.xaml](AuraClean/Views/MemoryBoostView.xaml#L145) — boost button
- **Category:** Performance
- **Description:** `DropShadowEffect` with `BlurRadius="30"` on the health score TextBlock forces software rendering for that visual subtree. The blur is re-calculated on every property change (score updates).
- **Impact:** Minor GPU/CPU overhead during score transitions. More significant on low-end hardware.
- **Recommendation:** Use `CacheMode="BitmapCache"` on the parent container to reduce re-rendering. Or limit the effect to initial render only.
- **Suggested command:** Performance optimization pass

#### MED-09: Fixed Panel Widths
- **Location:** MainWindow sidebar (Width="260"), FileShredderView right panel (Width="320"), StorageMapView right panel (Width="280")
- **Category:** Responsive
- **Description:** Fixed pixel widths on panels. MinWidth="1050" ensures they fit, but on wider displays (2560px+), the sidebar stays narrow while content stretches. Disproportionate space allocation.
- **Impact:** Wasted space on high-resolution displays. Sidebar items could benefit from wider labels at 4K.
- **Recommendation:** Consider using MinWidth/MaxWidth with proportional sizing, or allow sidebar collapse/expand.
- **Suggested command:** Layout optimization pass

#### MED-10: SettingsView Unsaved Indicator Uses Hard-Coded Color
- **Location:** [SettingsView.xaml](AuraClean/Views/SettingsView.xaml#L58) — `Background="#30FFB74D"`
- **Category:** Theming
- **Description:** The "unsaved changes" indicator uses an inline hex color with alpha that won't respond to theme changes.
- **Impact:** The amber tint may clash with the light theme's background.
- **Recommendation:** Extract to a theme-aware resource.
- **Suggested command:** Theme normalization pass

#### MED-11: Non-Standard Font Sizes
- **Location:** Multiple views — FontSize="13.5" (MainWindow nav), FontSize="26" (most view headers), FontSize="60" (health score)
- **Category:** Design
- **Description:** Font sizes don't follow a modular type scale. Values like 10, 11, 11.5, 12, 13, 13.5, 14, 15, 16, 17, 18, 22, 26, 28, 36, 60 are used — 16 distinct sizes with no clear mathematical relationship.
- **Impact:** Subtle typographic inconsistency. Makes it harder to maintain a predictable visual rhythm.
- **Recommendation:** Adopt a modular scale (e.g., 1.25 ratio: 10, 12.5, 15.6, 19.5, 24.4, 30.5) or a Material Design type scale with defined roles (caption, body2, body1, subtitle2, subtitle1, h6...h1).
- **Suggested command:** Design normalization pass

#### MED-12: No Empty State Designs
- **Location:** UninstallerView (empty program list), CleanerView (no junk found), DuplicateFinderView (no duplicates), all list/grid views
- **Category:** UX
- **Description:** When lists or grids have no data (before first scan, after cleaning), users see blank space. No "nothing here yet" illustrations, instructions, or calls-to-action.
- **Impact:** Users may think the feature is broken. Missed opportunity to guide users ("Run a scan to get started").
- **WCAG/Best Practice:** Empty states should teach the interface.
- **Recommendation:** Add empty state content with an icon, message, and primary action button for each list/grid view.
- **Suggested command:** UX enhancement pass

#### MED-13: CleanupHistoryView Filter Buttons Use Non-Semantic Elements
- **Location:** [CleanupHistoryView.xaml](AuraClean/Views/CleanupHistoryView.xaml#L82)
- **Category:** Accessibility
- **Description:** Filter tabs are implemented as styled `Border` + `TextBlock` elements instead of `Button` or `ToggleButton` controls. They lack proper keyboard focus, tab order, and button semantics.
- **Impact:** Keyboard users cannot activate filter tabs. Screen readers won't announce them as interactive.
- **Recommendation:** Replace with styled `ToggleButton` or `RadioButton` controls.
- **Suggested command:** Accessibility fix pass

#### MED-14: Sidebar Gradient Separator Colors Not Tokenized
- **Location:** [MainWindow.xaml](AuraClean/Views/MainWindow.xaml#L72) — brand logo gradient (#F0F0FF → #A090FF), separator gradient
- **Category:** Theming
- **Description:** Brand gradient and sidebar separators use inline hex colors that duplicate design tokens but aren't referencing them. #F0F0FF matches AuraTextBrightColor, but #A090FF is a unique non-token color.
- **Impact:** Won't adapt to theme changes. Minor visual inconsistency.
- **Recommendation:** Reference tokens or extract new ones.
- **Suggested command:** Theme normalization pass

---

### Low-Severity Issues

#### LOW-01: StorageMapView Hard-Coded Foreground Color
- **Location:** [StorageMapView.xaml](AuraClean/Views/StorageMapView.xaml#L203) — `Foreground="#CCCCCC"`
- **Category:** Theming
- **Description:** Single instance of a non-token gray text color in treemap tooltips.
- **Impact:** Won't adapt to theme changes. Minor.
- **Recommendation:** Replace with `{StaticResource AuraTextPrimary}`.

#### LOW-02: StartupManagerView Hard-Coded Foreground
- **Location:** [StartupManagerView.xaml](AuraClean/Views/StartupManagerView.xaml#L257) — `Foreground="#FFB74D"`
- **Category:** Theming
- **Description:** Amber foreground color hard-coded instead of using `{StaticResource AuraAmber}`.
- **Recommendation:** Replace with token reference.

#### LOW-03: AlternatingRowBackground Hard-Coded Across DataGrids
- **Location:** DiskOptimizerView (#06FFFFFF), FileRecoveryView (#06FFFFFF), LargeFileFinderView (#06FFFFFF), SoftwareUpdaterView (#08FFFFFF)
- **Category:** Theming
- **Description:** Alternating row backgrounds use consistent but hard-coded alpha-white values.
- **Recommendation:** Extract to `AuraAlternatingRowBg` token.

#### LOW-04: Duplicate Sidebar Gradient Colors in ThemeService
- **Location:** [ThemeService.cs](AuraClean/Services/ThemeService.cs#L104) — Hard-coded strings "#0C0C1A", "#08080F", "#F0F0F8", "#E8E8F0"
- **Category:** Code Quality
- **Description:** ThemeService re-declares sidebar gradient colors as string literals instead of referencing a central constant or the same dictionary.
- **Recommendation:** Centralize color definitions.

#### LOW-05: No Hover Cursor on Interactive Cards
- **Location:** ThreatScannerView scan mode cards — missing `Cursor="Hand"` is actually present, but DashboardView stat cards (which are display-only) don't need cursor changes.
- **Category:** UX
- **Description:** Minor inconsistency — some interactive-looking elements are display-only but lack visual distinction from interactive ones.
- **Recommendation:** Ensure interactive elements consistently use `Cursor="Hand"` and non-interactive elements don't.

#### LOW-06: Version String Hard-Coded in Sidebar
- **Location:** [MainWindow.xaml](AuraClean/Views/MainWindow.xaml) — `<TextBlock Text="v1.3.1"`
- **Category:** Code Quality
- **Description:** Version string is manually written rather than bound to assembly version.
- **Recommendation:** Bind to `Assembly.GetExecutingAssembly().GetName().Version` via ViewModel property.

#### LOW-07: CornerRadius Inconsistency
- **Location:** Various — CornerRadius="14" (cards), CornerRadius="12" (system info strip), CornerRadius="10" (buttons, icon badges), CornerRadius="4" (small badges)
- **Category:** Design
- **Description:** Four distinct corner radius values used with no clear system. Not a major problem but adds visual noise.
- **Recommendation:** Standardize to 2-3 corner radius tokens: small (6), medium (12), large (16).

#### LOW-08: Converters Create New SolidColorBrush Per Call
- **Location:** HealthScoreColorConverter, TreemapColorConverter, HexToBrushConverter
- **Category:** Performance
- **Description:** Each converter call allocates a new `SolidColorBrush` instance. During rapid updates or list virtualization, this can cause GC pressure.
- **Impact:** Minimal for current usage patterns. Could matter for large lists with virtualized items.
- **Recommendation:** Cache commonly used brushes in the converter.

---

## Patterns & Systemic Issues

### Recurring Issues

| Pattern | Occurrences | Description |
|---------|-------------|-------------|
| Hard-coded alpha-blended colors | 40+ | `#0E7C5CFC`, `#1500E5C3`, `#33FF4444` etc. duplicating token colors with alpha prefixes |
| Empty catch blocks | 50+ | Silent exception swallowing across services and ViewModels |
| Missing AutomationProperties | 22 views | Zero accessibility markup in the entire application |
| Missing FocusVisualStyle | 4 styles | All custom control templates lack keyboard focus indicators |
| Mouse-only event handlers | 4 handlers | ThreatScannerView scan mode selection |
| No transition animations | 22 views | Instant state changes with no visual transitions |
| Inconsistent StaticResource/DynamicResource usage | 22 views | Theme-switching may produce partial updates |

### Design System Gaps

The "Obsidian Aurora" design system is well-structured but incomplete:

| Has | Missing |
|-----|---------|
| Core color tokens (15 colors) | Alpha-variant tokens (tints/overlays) |
| Surface brush tokens | Elevation shadow tokens |
| Text color hierarchy (4 levels) | Typography scale tokens |
| Border tokens (2 levels) | Spacing tokens (margins, padding) |
| Gradient accent brushes | Focus state tokens |
| 4 button styles | Corner radius tokens |
| 1 card style | Transition/animation tokens |
| Nav button style | Empty state components |

---

## Positive Findings

### What's Working Well

1. **Cohesive dark theme** — The "Obsidian Aurora" system provides a unified visual language. Surface/border/text hierarchies are well-defined and consistently applied.

2. **Token system exists** — 15 Color resources + 14 Brush resources in App.xaml. Most views reference these tokens rather than raw values. The foundation for proper theming is solid.

3. **Light mode support** — ThemeService implements a complete light theme palette with proper brush-in-place modifications. Material Design base theme also toggles. The infrastructure is there even if hard-coded colors break it.

4. **Good MVVM architecture** — Clean separation of concerns. CommunityToolkit.Mvvm used consistently with `[ObservableProperty]` and `[RelayCommand]`. ViewModels don't reference views.

5. **Keyboard shortcuts** — MainWindow defines Ctrl+1 through Ctrl+0 for navigation. Good power-user feature.

6. **Icon + text navigation pattern** — Sidebar uses MaterialDesign PackIcons consistently paired with descriptive text labels. Well-organized with section headers.

7. **Touch targets on icon badges** — Dashboard stat card icon badges are 44x44px — meeting WCAG touch target guidelines.

8. **Proper event unhook pattern** — UninstallerViewModel and StartupManagerViewModel correctly pre-unsubscribe before re-subscribing to PropertyChanged events (`program.PropertyChanged -= handler; program.PropertyChanged += handler`).

9. **Good converter null handling** — FileSizeConverter properly checks for null/wrong types before processing. BoolToVisibility converters are defensive.

10. **ScrollViewer on sidebar** — Sidebar navigation uses a ScrollViewer, correctly handling overflow when the window is short.

11. **No glassmorphism or blur abuse** — Despite having an "AuraGlassCard" style, no actual blur effects are used. Cards use solid backgrounds, keeping rendering performance good.

12. **No bounce/elastic animations** — The codebase avoids tacky easing curves entirely.

13. **Section grouping in navigation** — Sidebar organizes 17 navigation items into logical sections (Cleanup, Analyze, Tools, Settings) with labels.

14. **HighDpiMode=PerMonitorV2** — Proper DPI awareness configured in the project file.

---

## Recommendations by Priority

### 1. Immediate (Critical Blockers)

| # | Action | Issues Addressed |
|---|--------|-----------------|
| 1 | Add `AutomationProperties.Name` to all interactive controls | CRIT-01, HIGH-04 |
| 2 | Replace MouseLeftButtonDown with Commands on focusable controls (ThreatScannerView) | CRIT-02 |
| 3 | Add keyboard focus visual triggers to all custom control templates | CRIT-03 |
| 4 | Fix event handler memory leaks (unsubscribe in Closing/Unloaded) | CRIT-04 |
| 5 | Fix ThreatScannerViewModel deletion logic bug | CRIT-05 |

### 2. Short-Term (This Sprint)

| # | Action | Issues Addressed |
|---|--------|-----------------|
| 6 | Extract 40+ hard-coded alpha colors to theme-aware tokens | HIGH-01, MED-10, MED-14, LOW-01-03 |
| 7 | Add keyboard navigation help to DataGrid views | HIGH-02 |
| 8 | Wrap ObservableCollection updates in Dispatcher.Invoke | HIGH-06 |
| 9 | Fix DispatcherTimer leak in UninstallerViewModel | HIGH-07 |
| 10 | Add try-catch to converter type conversions | HIGH-11 |
| 11 | Increase AuraTextMutedColor brightness for contrast compliance | MED-05 |

### 3. Medium-Term (Next Sprint)

| # | Action | Issues Addressed |
|---|--------|-----------------|
| 12 | Add entrance/exit animations to view switching | HIGH-05 |
| 13 | Replace empty catch blocks with DiagnosticLogger calls | HIGH-08 |
| 14 | Audit and fix StaticResource vs DynamicResource for theme-switching | HIGH-09 |
| 15 | Add color-independent status indicators | HIGH-10 |
| 16 | Design and implement empty states for all list views | MED-12 |
| 17 | Fix CleanupHistoryView filter tabs to use Button semantics | MED-13 |
| 18 | Standardize typography scale | MED-11 |
| 19 | Implement IDisposable on MainViewModel | MED-03 |

### 4. Long-Term (Nice-to-Haves)

| # | Action | Issues Addressed |
|---|--------|-----------------|
| 20 | Consider accent palette rebrand away from purple+cyan | MED-01 |
| 21 | Redesign dashboard layout with more originality | MED-02 |
| 22 | Standardize corner radius to 2-3 tokens | LOW-07 |
| 23 | Cache converter brush instances | LOW-08 |
| 24 | Bind version string to assembly version | LOW-06 |

---

## Suggested Commands for Fixes

| Command | Issues Addressed | Count |
|---------|-----------------|-------|
| **Accessibility hardening pass** | CRIT-01, CRIT-02, CRIT-03, HIGH-02, HIGH-03, HIGH-04, HIGH-10, MED-04, MED-05, MED-13 | 10 |
| **Theme normalization pass** | HIGH-01, HIGH-09, MED-10, MED-14, LOW-01, LOW-02, LOW-03, LOW-04 | 8 |
| **Performance/stability fix pass** | CRIT-04, HIGH-06, HIGH-07, HIGH-11, MED-03, MED-08, LOW-08 | 7 |
| **Code quality pass** | CRIT-05, HIGH-08, MED-07, LOW-06 | 4 |
| **Motion/animation enhancement** | HIGH-05 | 1 |
| **UX enhancement pass** | MED-12 | 1 |
| **Design refresh (optional rebrand)** | MED-01, MED-02, MED-06, MED-11, LOW-07 | 5 |

---

*End of audit. This report documents issues only — no fixes have been applied. Use the priority table above to plan implementation in the order that maximizes user impact.*
