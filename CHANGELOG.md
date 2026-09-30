# Changelog

All notable changes to AuraClean will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Fixed

- **Uninstall identity requires the original product key** — the MSI product GUID /
  product code from `RegistryKeyPath` is threaded through the trace scan, so a
  partially removed entry with a wiped DisplayName still matches by its known GUID.
- **Install-dir matching is boundary-aware** — path-like values qualify only when
  at or truly under the install location (`Acme Tools` no longer matches `Acme`);
  quoted paths and trailing args are stripped before comparing.
- **Value backups keep the 32-bit view** — the backup path is rebuilt with the
  `(32-bit)` / `(64-bit)` suffix so a 32-bit Run value's backup actually exports
  the 32-bit parent it deletes from.

- **Shared-registry-container deletes are value-scoped** — Run/RunOnce and MUI-cache
  hits now name the individual value (`KeyPath:value=Name`) and delete only that
  value after backup; the shared parent key is never deleted.
- **Uninstall-subtree hits require product identity** — an entry is offered for
  deletion only on exact display-name match, install-path/product-id evidence, or a
  path-like value under the original install location. Shared-word substrings
  (e.g. VS Code terms vs an installed Visual Studio) no longer qualify.

### Added

- **Uninstaller sorting + Drive column** — every column header sorts (click toggles
  direction, ▲/▼ glyph); Unknown sizes and unparseable dates sort last in both
  directions; new Drive column derived from the install location.
- **Revo-style deep leftover scan** — post-uninstall now also scans the surviving
  InstallLocation itself, one level deeper under AppData/ProgramData, Startup-folder
  entries (incl. `.lnk` targets under the install location), and registry value traces
  across Run/RunOnce, the Uninstall subtree, Services (install-path match required),
  scheduled tasks, App Paths, and MUI cache. Shared containers surface as explicit
  review hits instead of false-success deletes.
- **Dashboard disk + hardware tiles** — system-drive free-space % and hardware grade
  (shown once System Info has run; dashboard never blocks on it).

### Fixed

- **One health-score formula** — idle and Health Check paths share `ComputeHealthScore`
  (junk + threats + startup + browser + recency; missing inputs count 0), so the score
  no longer silently changes definition. Hero tooltip describes the real inputs.
- **Dashboard stops lying with zeros** — primes the Uninstaller load + Cleaner analysis
  on first show, shows "Not scanned yet" instead of 0, junk tile has a 3-state display.
- **Honest button labels** — "Analyze & Clean" → "Analyze" (it only analyzes),
  "Undo Clean" → "Open System Restore…" (it launches rstrui.exe).
- **Storage Map progress in huge folders** — `ReadDirectory` streams entries to a callback
  so item/byte/percentage updates and cancellation fire while a large flat directory is
  still being listed, instead of going silent until the whole listing is buffered.
  Applies to both the crawl path and the below-depth-limit summation path.

### Changed

- **OpenEval visual system (foundation)** — charcoal dark and white light palettes with a violet accent and semantic green / amber / red, blended (color-mix) surfaces for cards, sidebar, hover and active states, 12px cards, 8px actions, 6px controls, pill-shaped status badges, thin scrollbars, themed tooltips, and short `cubic-bezier(.2,0,0,1)` motion with a .96 press. All text colors meet WCAG AA on page and card surfaces in both themes.
- **Theme preference** — Auto (follows the Windows app theme live), Light, or Dark, chosen in Settings → Appearance or with the sidebar toggle; applies and saves immediately and themes the window title bar. Existing light-theme settings migrate automatically; new installs start in Dark.
- Honors Windows "Show animations": control transitions become instant when animations are off.
- **Every screen (OpenEval recipes)** — page headers with an accent icon tile, 24px semibold title and muted subtitle; every count, size, score and date in a monospaced tabular face; KPI numbers capped at 24px semibold (no bold or light weights); status badges as pills with a dot and tone border (threat severities, dry run, keep, unsaved changes, context-menu status); permanent-delete buttons in the red danger style; empty states as a dim icon tile with a short title; button labels in title case; color only where it carries meaning (metric icons teal, card-header icons dim).
- Dashboard: flat hero card (no glow), trend shown as an icon, stat tiles with uppercase eyebrow labels instead of colored side stripes.
- Storage map tiles are tinted by category with theme text, readable in both themes.
- **Clearer hierarchy** — group sections (duplicate groups, cleaner categories, threat categories, uninstall leftovers) have a shaded, accent-edged header band with a semibold title, so headings no longer blend into their rows; duplicate groups are titled by file name with a "N copies" pill; body text is one step softer than headings.
- **Buttons look like buttons** — secondary buttons have a filled surface, visible border and full-contrast text (they read as disabled before); dashboard actions are color-coded (Quick Clean violet, Health Check green, Boost RAM amber, Storage Map teal); new tone button styles for telling neighboring actions apart.
- **Status tags** — badges (severity, dry run, copies, wasted, unsaved changes, detection method) are squared tags with a solid tone fill and bold text instead of outlined ovals with dots; tag text meets WCAG AA in both themes. The sidebar shows administrator status as a shield icon and label.
- **Sidebar** — each section has its own color for its label and icons (Cleanup violet, Analyze teal, Optimize green, Utilities amber); icons are 18px.
- **Sidebar (OpenEval shell)** — gradient brand mark with tracked "SYSTEM UTILITY" subtitle, letter-spaced section labels (new `TrackedText` control; screen readers get normal casing), 16px icons that follow the item state, keyboard hints as kbd chips, an Administrator status pill, and version + theme toggle in the footer. Sidebar and status bar use the shell surface.
- **Normal mode can act on results** — Duplicate Finder and Large File Finder let you tick files and remove them in Normal mode too; removals go to the Recycle Bin (Advanced mode deletes permanently). Duplicate rows get a "Keep this" button (keeps that copy, marks the rest), a show-in-Explorer button and a live "Delete Selected (N)" count; nothing is pre-selected, and one-click "select all duplicates" stays Advanced-only. Confirmations warn when files look like program files or live in app folders (AppData, virtual environments, package caches), since apps need their own copy.
- Threat Scanner and System Cleaner show their selection checkboxes in Normal mode (quarantine and cleaning were already allowed, but you could not choose items). Startup Manager can re-enable items in Normal mode, not only disable them.
- File Shredder explains why shredding needs Advanced mode and offers a one-click switch.

### Fixed

- **Storage Map scans no longer look stuck** — past four folder levels the scan switched to an unreported recursive size pass, so the counter froze (e.g. at "Scanned 60,000 items") while a whole-drive scan kept running for minutes. The scan now reads sizes straight from each directory listing (no extra file lookup per file, one pass per folder), reports progress continuously with the item count, bytes counted and the folder being read, and shows an estimated percentage when scanning a whole drive. Deep files now count toward the file total and the Largest Files list, a folder that fails mid-listing keeps what was read instead of dropping its whole subtree, and a drive that errors while listing drives is skipped instead of breaking the page.
- **Sidebar selection** — only the current page is highlighted; previously one item stayed highlighted in every section you had visited.
- **Sidebar labels** — the keyboard-shortcut hint no longer draws on top of the item name on hover.
- **Collapsible group headers** rendered as a tiny centered box with the title missing (MaterialDesign's switch style was applied to the header toggle).
- **Threat Scanner scan-mode cards** (and history filter chips, storage map tiles, system info filter chips) were cut off at 32px because they picked up MaterialDesign's default button style.
- **Startup Manager** — the Status column's enabled/disabled icon was bound through a visibility converter, which blocked the icon style; it now shows the right icon.
- **Uninstaller** — the leftovers header showed a bare number instead of "Leftovers found (N items)", and the System Cleaner lock tooltip omitted "Locked by:" (format strings on object-typed properties are ignored).
- History and System Info filter chips now show which filter is active.
- **System info** — the grade letter on each hardware card was drawn at 15% opacity (the badge's fade applied to its text) and was nearly invisible.
- **Selection badges** ("N selected") and the disk fragmentation percentage used white text on light fills and were hard to read.
- **File shredder** — dragging non-file content (such as text) over the drop zone and away no longer crashes the app (it animated a frozen brush).
- Health score, trend arrow and hardware grade colors now follow the active theme instead of fixed hex values.

### Added

- **Cleaner exclusions** — a user-managed list of files/folders the system cleaner never
  touches (Settings → Advanced; also honored by headless `/autoclean` runs). Excluded scan
  results are deselected with an "Excluded" badge instead of being cleaned.
- **Per-category scheduled cleanup** — choose which junk categories `/autoclean` may clean
  (empty selection = the Normal-mode default set); unknown and review-only category names
  are dropped, never honored.
- **History trend** — the History view shows a 7-day per-day cleanup bar strip with a totals row.

### Fixed

- **Cleaner deletes are guarded** — single-file and best-effort loop deletes now skip
  unparseable paths, reparse points (links/junctions), and System-attributed files.
- **Settings can no longer be mutated by accident** — `Load()` returns a defensive copy,
  so a caller that forgets `Save()` cannot corrupt global state.
- **Quarantine expiry labels fixed** — an entry expiring right now reads "Expired" (was
  showing a day/hour count across the boundary), and sub-hour remainders show minutes.
- **Force delete never kills Office or databases** — Word/Excel/PowerPoint/Outlook and
  common DB/writer processes are denylisted; every killed process is logged.
- **Drive input validated** — optimizer/analyzer refuse anything that is not a single
  drive letter before invoking defrag.
- **Quarantine grid no longer hits disk per row** — retention days load once per
  enumeration instead of on every `IsExpired`/`ExpiresIn` get.
- **AuraWarning is amber again** — it was red (identical to AuraErr); error/destructive
  surfaces now use the new `AuraDanger` token, which also adapts to light theme.
- **Treemap colors follow the theme** — frozen brush cache with light/dark palettes,
  rebuilt on theme change.
- **Lazy navigation** — feature views and their ViewModels are built on first navigate
  instead of all 21 at startup (Dashboard stays eager); dead `MainViewModel.CurrentView`
  removed.
- **Test suite extended** — 73 new asserts (`SafetyHardeningTests`, `FeatureBatchDTests`);
  333 total, all passing.
- Removed 6 orphaned asset files (~640KB) that were bundled but referenced nowhere.

### Added

- **History CSV export** — History view exports the filtered records (or all) to a
  UTF-8-BOM CSV that opens directly in Excel: Timestamp, Operation, Items Cleaned,
  Space Freed (bytes), Dry Run, Details, with RFC-4180 quoting.

### Fixed

- **No more silent failures** — all ~86 empty catch blocks across Services/ and Helpers/
  now log context + exception via DiagnosticLogger (2 deliberate exceptions: a
  dispose-in-finally and the logger's own internals, both commented).
- **Honest card names** — `AuraGlassCard` family removed entirely (was solid rects, no
  blur); views now use `AuraCard` / `AuraStatCard` / `AuraDataCard` directly.
- **Sidebar regrouped** — the 9-item Utilities drawer is now Tools (4) + System (5);
  no section holds more than 5 items.
- **Off-thread UI writes fixed** — theme-change handler re-dispatches to the UI thread
  (Windows theme flips arrive on a pool thread); audit confirmed all other VMs already
  marshal correctly.

---

## [1.5.1] — 2026-09-27

### Fixed

- **App startup** — `App.xaml.cs` lived outside the project and was never compiled: crash handlers, theme-on-start, and the scheduled `/autoclean` mode now work. Single-instance activation, `--minimized`, and Explorer `--deep-uninstall` are handled.
- **Scheduled cleanup & launch at login** — now registered as Task Scheduler tasks with the highest run level (the elevated app could not start from a limited task or the Run key).
- **Force uninstall** — validates the program's install location before deleting and no longer auto-deletes every name-matched leftover; leftovers are listed for review.
- **Registry leftovers** — only keys named after the product are flagged (a single matching value used to flag whole shared keys such as `…\Run`); deletion requires a successful backup.
- **Threat scanner** — removed an "Emotet" signature that was the hash of an empty file and two "malicious" extension IDs that were Adobe Acrobat and AdBlock; hosts threats no longer quarantine the whole hosts file; browser hijacks no longer move the browser's Preferences file; Browser Scan mode is selectable again.
- **Browser cleaner** — IndexedDB, Local/Session Storage and other site data are no longer deleted as "cache" in Normal mode.
- **File recovery** — restores never overwrite a file that now exists at the original location.
- **Duplicate finder** — at least one copy of every group always survives; checkbox state reflects what will be deleted.
- **Settings** — saving no longer resets onboarding; values are validated.
- **Quick Clean** — only records a clean when something was actually cleaned.
- DISM output pipe deadlock, orphaned defrag on cancel, winget hanging on its first-run prompt, and many unguarded exceptions.

### Changed

- Temp files younger than 24 hours and logs younger than 7 days are kept during cleanup.
- Only the current user's Recycle Bin is cleaned.
- Heuristic threat findings start unselected; each item explains how it will be handled.
- Quarantined files are stored with a non-executable suffix; the manifest is written atomically.
- Downloaded installers must pass Authenticode verification.
- All destructive tools record their results in Cleanup History; the history page refreshes live.
- Sidebar selection follows keyboard shortcuts; dashboard scrolls and wraps at small window sizes.

---

## [1.5.0] — 2026-03-21

### Added

- **Health Score Trend Indicator** — Dashboard shows ↑ / ↓ / — arrow next to the health score reflecting change since last check, persisted across sessions via `SettingsService`
- **Collapsible Navigation Sections** — Sidebar groups (Cleanup, Analyze, Optimize, Utilities) with togglable headers and `ToggleSectionCommand`; collapsed state persisted per session
- **Keyboard Shortcut Badges** — Keyboard hints (e.g. `Ctrl+1`) appear on navigation items on hover via `ShowKeyboardShortcuts` toggle
- **ResultCard Notification Control** — New `ResultCard` UserControl (`Views/Controls/`) providing animated success/warning banners after clean, shred, and boost operations across Cleaner, BrowserCleaner, FileShredder, and MemoryBoost views
- **Animated Drag-and-Drop Feedback** — FileShredderView now shows a drop overlay with animated border color transitions (coral highlight, confirmation pulse on drop)
- **Dynamic AppVersion** — Version string auto-derived from assembly metadata; no hardcoded version in UI
- **Design System Documentation** — `docs/DESIGN_SYSTEM.md` documenting token architecture, card taxonomy, and usage guidelines
- **UX Research Docs** — `docs/ux/color-strategy-audit.md` and `docs/ux/delight-opportunities.md`

### Changed

- **Design System Extraction** — ~290 lines of inline design tokens and component styles extracted from `App.xaml` into dedicated `Resources/DesignTokens.xaml` (10 sections: color primitives, semantic brushes, gradients, overlays, badges, severity, fonts, typography, spacing, radius, layout) and `Resources/ComponentStyles.xaml` (nav buttons, primary/secondary/outlined/ghost buttons, cards, inputs, text, badges)
- **Theme Palette Overhaul** — Renamed "Obsidian Aurora" / "Aurora Light" to "Storm Dark" / "Storm Light" with completely revised hex palette (backgrounds, surfaces, text, borders, accents) in `ThemeService.cs`
- **Card Style Taxonomy** — Replaced monolithic card styles with purpose-driven hierarchy: `AuraCard` (container), `AuraStatCard` (metrics), `AuraActionCard` (interaction zones), `AuraDataCard` (lists/tables), `AuraHeroCard` (focal sections), `AuraSectionCard` (stacked); legacy `AuraGlassCard*` aliases preserved for backward compatibility
- **Accent Color Shift** — `AuraSecondaryButton` and `AuraOutlinedButton` changed from teal accent to violet accent (`AuraAccentPurple` / `AuraVioletDarkColor`)
- **GPU-Accelerated Animations** — New `AnimationHelper.cs` with attached behaviors (`FadeSlideOnVisible`, `EntranceAnimation`) using `RenderTransform` for composited animations; respects `SystemParameters.ClientAreaAnimation` reduced-motion preference
- **View Code-Behind Enhancements** — BrowserCleaner, Cleaner, FileShredder, and MemoryBoost views now subscribe to ViewModel property changes and trigger `ResultCard` notifications on operation completion with proper `Unloaded` cleanup
- **ViewModel Status Messages** — Refined status/progress messages across ~10 ViewModels for consistency and clarity
- **Font Family** — Migrated to `AuraFontFamily` resource reference throughout views
- **Navigation Layout** — Sidebar items reorganized into collapsible groups with section headers; branding area enlarged with gradient references

### Fixed

- **Health Check Resilience** — Each health-check step now wrapped in individual `try/catch` with `DiagnosticLogger.Error` to prevent a single failing metric from aborting the entire health check
- **Thread-Safe Collections** — Added `lock` guards around `ObservableCollection` access in `BrowserCleanerViewModel` and `CleanerViewModel` to prevent cross-thread mutation crashes
- **Diagnostic Logging** — Additional `DiagnosticLogger.Error` calls in `DiskOptimizerService`, `SystemInfoService`, and `ThreatScannerService` replacing silent exception swallowing

---

## [1.4.0] — 2026-03-19

### Added

- **Onboarding Wizard** — New 4-step first-run walkthrough overlay
  - `OnboardingViewModel`, `OnboardingView.xaml` — welcome screen, system facts, feature tour, configuration
  - Persists completion state via `HasCompletedOnboarding` setting in `SettingsService`
  - Full-window overlay in `MainWindow` with `Panel.ZIndex="100"`
- **Empty State Placeholders** — Informative empty-state panels shown before first scan or when no results match
  - Uninstaller — "No programs found" with search/refresh hint
  - Startup Manager — "No startup programs found" with system note
  - Software Updater — "Check for software updates" prompt
  - Driven by new `HasScanned` property on each ViewModel
- **Design Tokens** — 20+ new named resource brushes in `App.xaml`
  - Semi-transparent overlays: `AuraAccentPurpleSemi`, `AuraAccentTealSemi`, `AuraAmberSemi`, `AuraCoralSemi`, `AuraOverlayLight`, `AuraOverlaySubtle`, `AuraOverlayFaint`, `AuraTrackBg`
  - Badge backgrounds: `AuraAccentPurpleBadge`, `AuraAccentTealBadge`, `AuraAmberBadge`, `AuraBlueBadge`
  - Severity backgrounds: `AuraCriticalSeverity`, `AuraCoralSeverity`, `AuraAmberSeverity`, `AuraSuccessSeverity`
- **Card Styles** — Reusable `AuraGlassCardCompact` (stat chips/badges) and `AuraGlassCardFlush` (list containers) Border styles in `App.xaml`
- **AuraSecondaryButton** — New teal-accent button style for secondary actions
- **HealthScoreColorConverter** — New global converter registered in `App.xaml`

### Changed

- **Card Component Standardization** — Replaced `materialDesign:Card` with `Border` + design-system styles across 16 views (StartupManager, StorageMap, SystemInfo, ThreatScanner, Uninstaller, DiskOptimizer, FileRecovery, Cleaner, BrowserCleaner, Dashboard, DuplicateFinder, InstallMonitor, FileShredder, LargeFileFinder, AppInstaller, and more)
- **Converter Centralization** — Removed per-view `UserControl.Resources` converter declarations; all converters now registered globally in `App.xaml` (BoolToVis, FileSizeConverter, InverseBoolToVis, IntToVis, HexToBrush, ScoreToWidth, HealthColorConverter, TreemapColorConverter)
- **Color Token Migration** — Replaced hardcoded hex values with named resource references throughout views (e.g., `#08FFFFFF` → `AuraOverlaySubtle`, `#157C5CFC` → `AuraAccentPurpleBadge`)
- **ThreatScannerView** — Removed local `ThreatHighBrush`, `ThreatMediumBrush`, `ThreatLowBrush` in favor of global `AuraWarning`, `AuraAmber`, `AuraSuccess`
- **Consistent Page Margins** — Standardized view margins to `Margin="32"` across Uninstaller, Startup Manager, Threat Scanner, Browser Cleaner
- **ListView Text Overflow** — Name/Publisher columns now use `TextTrimming="CharacterEllipsis"` with `ToolTip` for overflow (Uninstaller, Startup Manager)

### Fixed

- **UninstallerViewModel** — Fixed `DispatcherTimer` leak: timer now initialized once in constructor instead of recreated per keystroke in `OnSearchTextChanged`
- **Selection Event Hook Ordering** — Hook selection events on master collections before filtering to prevent stale event subscriptions (`UninstallerViewModel`, `StartupManagerViewModel`, `LargeFileFinderViewModel`)
- **LargeFileFinderViewModel** — Batch delete now reports individual file failures and includes failure count in status message
- **ThreatScannerView** — Corrected `InvBoolToVis` → `InverseBoolToVis` converter key reference
- **Diagnostic Logging** — Replaced 12+ empty `catch {}` blocks with `DiagnosticLogger.Warn` calls across `DiskAnalyzerViewModel`, `DuplicateFinderViewModel`, `FileShredderViewModel`, `InstallMonitorViewModel`, `LargeFileFinderViewModel`, `StartupManagerViewModel`, `ThreatScannerViewModel`
- **FileShredderViewModel** — Removed incorrect `StatusMessage` assignment after folder drop that overwrote file count

### Performance

- **FileCleanerService** — Refactored sequential junk scanning to `Parallel.ForEachAsync` with `ConcurrentBag` and `MaxDegreeOfParallelism = 4`, reducing scan time on multi-core systems
- **ThreatScannerService** — Parallelized file heuristic analysis with `Parallel.ForEachAsync` (bounded at 4 threads), filters scannable extensions before enumeration

### Build

- **install.ps1** — Cleans stale publish output before build, kills running AuraClean instance before overwriting, desktop shortcut now set to Run as Administrator with UAC flag, shortcut icon linked to EXE
- **AuraClean.csproj** — Added `auraimage.png`, `icon2.png`, `icon.ico` as embedded resources
- **MainWindow** — Window icon changed from `icon2.png` to `icon.ico`

---

## [1.3.1] — 2026-03-18

### Added

- **One-click install script** (`install.ps1`) — fully automated setup pipeline
  - Detects and installs .NET 8 SDK automatically if missing (via official `dotnet-install.ps1`)
  - Publishes self-contained single-file EXE (~73 MB, no .NET runtime required on target)
  - Installs to `%LocalAppData%\AuraClean\` and creates a desktop shortcut
  - Launches as Administrator with UAC prompt
  - Flags: `-SkipLaunch` (install only), `-NoBuild` (reuse existing publish output)
- **Build helper script** (`build.ps1`) — streamlined developer build commands
  - `.\build.ps1` (Debug build), `-Release` (single-file publish), `-Run` (build + launch), `-Clean` (clean artifacts)

### Changed

- **README** — Rewritten "Getting Started" section with quick-install one-liner and developer build options
  - Removed manual prerequisite steps in favor of automated install script
  - Updated published EXE size from ~60 MB to ~73 MB

---

## [1.3.0] — 2026-03-18

### Added

- **Hardware Performance Rating System** — New weighted scoring engine for System Info page
  - `HardwareScoreService` — scores CPU (30%), Memory (25%), GPU (20%), Storage (20%), System (5%) with letter grades S–F
  - CPU scoring factors: core count (45%), clock speed (35%), L3 cache (15%), HT/SMT bonus
  - Memory scoring factors: capacity (70%), speed (30%) with DDR4/DDR5 tiers
  - GPU scoring: VRAM-based with discrete GPU detection and bonus, integrated GPU capping
  - Storage scoring: capacity (25%), free space health (30%), disk type SSD/NVMe (45%)
  - System scoring: OS build recency (70%), architecture 64-bit (30%)
  - Color-coded grade badges: S (cyan) → A (mint) → B (violet) → C (purple) → D (amber) → E (orange) → F (coral)
  - Score bar visualizations for each category
- **App Installer** — Bundle application installer page for streamlined software deployment
  - `AppInstallerService`, `AppInstallerViewModel`, `AppInstallerView.xaml`
  - `BundleApp` model for app bundle definitions
  - Sidebar navigation entry under Tools section
- **New Converters** — `HexToBrushConverter`, `ScoreToWidthConverter`, `ScoreToAngleConverter` for hardware rating UI

### Changed

- **SystemInfoService (GPU)** — Complete rewrite of GPU detection for accuracy
  - Reads VRAM from Windows registry (`HardwareInformation.qwMemorySize` QWORD) instead of WMI `AdapterRAM` (uint32) — fixes >4 GB GPUs showing incorrect VRAM (e.g., 16 GB GPU reported as 4 GB)
  - Multi-GPU sorting: discrete GPUs (NVIDIA/AMD/Intel Arc) sorted first by VRAM, then integrated GPUs — ensures the most powerful GPU is used for scoring
  - Queries `PNPDeviceID` from WMI to locate accurate registry VRAM values
- **SystemInfoView.xaml** — Major redesign with hardware score cards, circular gauge, category bars, and overall grade display
- **SystemInfoViewModel** — Extended with 20+ scoring properties for category-level score/grade/summary/color binding
- **MemoryManagerService** — Replaced per-process `WorkingSet64` enumeration with `GlobalMemoryStatusEx` P/Invoke for accurate physical memory usage stats
- **BrowserCleanerService** — Fixed process handle leak: `Process` objects from `GetProcessesByName` are now disposed
- **RegistryScannerService** — Fixed `reg.exe export` failing on display paths like `"HKLM (64-bit)\..."` — new `NormalizeKeyPath` strips bitness suffix; `ParseKeyPath` now handles `"HKLM "` prefix
- **StartupManagerService** — Fixed CSV column parsing for `/V` verbose output: task name moved from column 0 to 1, schedule type from column 8 to 18
- **SoftwareUpdaterService** — Fixed winget output parsing: new `CleanWingetOutput` splits on `\r` and `\n` to handle progress spinner carriage returns
- **ThreatScannerService** — Replaced hardcoded `C:\Windows\System32\` paths with `Environment.SpecialFolder.System` for cross-environment compatibility
- **ThreatSignatureDatabase** — Replaced hardcoded system paths with `Environment.GetFolderPath()` calls for `System32`, `SysWOW64`, `Windows`, `ProgramFiles`, `ProgramFilesX86`
- **MainViewModel** — Added `AppInstallerViewModel` property for sidebar navigation
- **MainWindow.xaml** — Added App Installer sidebar entry and content area

---

## [1.2.0] — 2026-03-16

### Added

- **WinSxS Component Store Cleanup** — New junk category (#15) integrated into System Cleaner
  - Scans `C:\Windows\WinSxS` via DISM `/AnalyzeComponentStore` to estimate reclaimable space
  - Cleanup runs DISM `/StartComponentCleanup` (official safe method, no manual file deletion)
  - `ParseDismSize` helper parses DISM output ("Reclaimable Packages : X.XX GB/MB/KB") into bytes
  - New `JunkType.WinSxS` enum value with "Component Store (WinSxS)" category label
- **Empty Folder Finder & Cleaner** — New full-featured tool page (sidebar → Tools section)
  - `EmptyFolderFinderService` — bottom-up recursive scanner; collapses nested empty trees into parent entries
  - Skips reparse points/junctions for safety
  - `EmptyFolderItem` model with Path, Name, ParentPath, LastModified, EmptySubfolderCount, DisplayInfo
  - `EmptyFolderFinderViewModel` — scan paths management, scan/cancel/delete commands, Select All toggle
  - `EmptyFolderFinderView.xaml` — Obsidian Aurora themed page with scan path management, DataGrid results, status bar
  - Deepest-first deletion to avoid parent-before-child issues
  - `GetDefaultScanPaths()` returns UserProfile, ProgramFiles, AppData paths
- **Disk Optimizer** — Drive analysis with TRIM, defragmentation, and optimization recommendations
  - `DiskOptimizerService`, `DiskOptimizerViewModel`, `DiskOptimizerView.xaml`
- **File Recovery** — Scan and recover recently deleted files
  - `FileRecoveryService`, `FileRecoveryViewModel`, `FileRecoveryView.xaml`
- **Software Updater** — View installed software with update status
  - `SoftwareUpdaterService`, `SoftwareUpdaterViewModel`, `SoftwareUpdaterView.xaml`
- **Notification Service** — Centralized toast notification helper (`NotificationService.cs`)
- **Scheduled Cleanup Service** — Automated cleanup on configurable schedule (`ScheduledCleanupService.cs`)
- **Scheduled Cleanup Settings** — New settings section for configuring scheduled cleanup (interval, categories, time)
- **Functional Test Suites** — Two new test suites in TestFeatures (78 total assertions, all passing)
  - Suite 4: WinSxS Parsing — `ParseDismSize` with GB/MB/KB/fractional/edge cases, `JunkType.WinSxS` category mapping
  - Suite 5: EmptyFolderFinderService — scan with nested empty/non-empty trees, tree collapsing, delete verification, cancellation, non-existent path handling

### Changed

- **AssemblyInfo.cs** — Added `InternalsVisibleTo("TestFeatures")` for internal method testing
- **FileCleanerService** — `ParseDismSize` changed from `private` to `internal` for testability
- **DiagnosticLogger** — Enhanced with additional logging methods and improved error handling
- **BrowserCleanerService** — Expanded browser detection and cleaning capabilities
- **MemoryManagerService** — Improved memory optimization with additional safety checks
- **SystemInfoService** — Enhanced hardware detection and reporting
- **MainViewModel** — Added navigation properties for 5 new pages (DiskOptimizer, EmptyFolderFinder, FileRecovery, SoftwareUpdater) + keyboard shortcuts
- **MainWindow.xaml** — Added sidebar entries and content areas for new pages
- **DashboardView.xaml** — Fixed `{StaticResource AuraAccent}` → `{StaticResource AuraAccentPurple}` (resource key did not exist)
- **SettingsView.xaml** — Added scheduled cleanup configuration section
- **SettingsService** — Added scheduled cleanup settings properties

### Fixed

- **SoftwareUpdaterView.xaml** — Fixed `InvertBoolConverter` → `InverseBoolConverter` (2 occurrences, lines 42 and 51) — was causing `XamlParseException` crash on startup
- **DashboardView.xaml** — Fixed missing `AuraAccent` resource reference → `AuraAccentPurple`
- **DuplicateFinderService** — Minor stability improvement

---

## [1.1.0] — 2026-03-14

### Added

- **Threat Scanner** — New full-featured heuristic security scanner with sidebar navigation
  - Scans files, running processes, startup entries, scheduled tasks, services, and hosts file
  - Threat signature database with known malware, adware, PUP, browser hijacker, and miner signatures
  - Threat severity levels (Low, Medium, High, Critical) with color-coded UI
  - One-click quarantine of detected threats with automatic Quarantine page refresh
  - `ThreatItem` model, `ThreatScannerService`, `ThreatSignatureDatabase`, `ThreatScannerViewModel`, `ThreatScannerView`
- **Theme Service** — Dynamic theme switching infrastructure (`ThemeService.cs`)
  - Sidebar and status bar now use `DynamicResource` instead of `StaticResource` for live theme changes
  - New `AuraSidebarGradient` dynamic brush resource
- **System Tray Integration** — Minimize-to-tray with `NotifyIcon` (WinForms interop)
  - Tray icon with context menu (Open / Exit)
  - Double-click to restore; close-to-tray when enabled in Settings
  - Respects `SettingsService.MinimizeToTray` preference
- **Keyboard Shortcuts** — Ctrl+1 through Ctrl+0 for sidebar navigation (Dashboard, Threat Scanner, Uninstaller, Cleaner, Memory, Browser, Storage Map, Startup, Shredder, Settings)
- **Undo Last Clean** — Dashboard button opens System Restore to revert the most recent cleanup operation (visible only when a restore point was created)
- **Drag-and-Drop** — File Shredder now accepts files/folders via drag-and-drop onto the view
- **Batch Selection** — Checkbox-based multi-select with Select All and selection count badges for:
  - Uninstaller (batch uninstall)
  - Startup Manager (batch toggle/delete)
  - File Shredder (batch shred)
  - Large File Finder (batch delete with size summary)
- **New Converters** — `InverseBoolToVisibilityConverter` and `IntToVisibilityConverter`
- **Quarantine Messaging** — `WeakReferenceMessenger` integration so ThreatScanner quarantine actions automatically refresh the Quarantine page
- **Cleanup History** — New `ThreatQuarantine` operation type for tracking threat-related quarantine events

### Changed

- **Window Defaults** — Default size increased to 1280×780 (min 1050×650) for better content visibility
- **Dashboard Info** — OS and CPU name text `MaxWidth` increased from 180 to 240 to prevent truncation
- **Large File Finder** — `LargeFileEntry` now implements `INotifyPropertyChanged` with `SelectionChanged` event for reactive checkbox binding; DataGrid switched to `SelectionMode="Extended"`
- **File Shredder** — `ShredFileItem` now extends `ObservableObject` with `IsSelected` property; shred command operates on checked files (falls back to all if none checked)
- **Startup Manager** — Toggle and delete commands now operate on all checked entries (falls back to single selected entry)
- **Uninstaller** — Uninstall command now operates on all checked programs (falls back to single selected program)
- **Quarantine View** — Auto-refreshes when navigating to the page; listens for external changes via `QuarantineChangedMessage`

### Dependencies

- Added `TaskScheduler` 2.11.0 (Windows Task Scheduler access for threat scanning)
- Added `UseWindowsForms` project flag for `System.Windows.Forms.NotifyIcon` system tray support

---

## [1.0.0] — 2026-03-13

### Added

- Initial release with 15 feature pages
- Obsidian Aurora v2 design system
- Deep uninstaller with registry + remnant scanning
- System cleaner with 14 junk categories
- RAM booster (EmptyWorkingSet + NtSetSystemInformation)
- Browser privacy cleaner with SQLite VACUUM
- Disk analyzer treemap
- Install monitor (before/after snapshot diffing)
- Startup manager (registry + shell:startup + Task Scheduler)
- Duplicate finder (3-pass: size → partial hash → full SHA-256)
- Large file finder with drive scanning
- File shredder (Quick Zero, Random, DoD 5220.22-M, Enhanced 7-pass)
- System info (WMI hardware inventory)
- Quarantine with restore and auto-purge
- Cleanup history with persistent logging
- Centralized settings
