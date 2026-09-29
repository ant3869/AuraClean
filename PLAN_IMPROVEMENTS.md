# AuraClean — Improvement Plan (2026-09-29)

Skills in play: vibe-coding-gate (verdict: REAL CODE — system utility, admin, deletes files;
disciplined loop, no vibe shortcuts), plan-first-one-shot (this file), context-engineering
(4 parallel builders, disjoint files), eval-chain + prove-it (build + 260 tests must stay green).

## Batch A — Backend safety hardening (owner: agent A)
Files: Services/FileCleanerService.cs, Services/SettingsService.cs (Load/Save logic ONLY —
do not touch the AppSettings properties), Services/QuarantineService.cs,
Services/ForceDeleteService.cs, Services/DiskOptimizerService.cs.
New: TestFeatures/SafetyHardeningTests.cs (new file, `public static int Run()`, follow the
Assert pattern in LogicTests.cs — read it first).
- A1: FileCleanerService file branch (~:341): add `PathSafety.IsSafeToDeleteFile` guard
  before `File.Delete`, same skip pattern as dirs (skipped++, IsLocked + message, log).
  Also check the best-effort dir-clean loop (~:471) covers files the same way.
- A2: SettingsService.Load() returns the live `_cached` ref — return a defensive copy
  (JSON round-trip). Grep Load() call sites for mutate-without-Save and fix to Save.
- A3: QuarantineService IsExpired/ExpiresIn (~:369-389) call Load() per property get —
  load retention-days once per enumeration and pass down (no per-row disk/lock).
- A4: ForceDeleteService denylist (~:333-348): add Office (WINWORD/EXCEL/POWERPNT/OUTLOOK)
  + common DB/writer processes; log every killed process name.
- A5: DiskOptimizerService (~:99-108): validate driveLetter is a single A–Z letter before
  interpolating into defrag args; refuse anything else.
- Explicitly NOT changing (accepted risk, noted): Authenticode revocation-off (offline
  scans by design), ThreatScanner recursive dir delete (confirm-gated by design).

## Batch B — UI bug fixes + asset hygiene (owner: agent B)
Files: Converters/FileSizeConverter.cs (may rename to ValueConverters.cs if no refs break),
Resources/DesignTokens.xaml, Resources/ComponentStyles.xaml,
Views/Controls/ResultCard.xaml.cs, App.xaml. May delete unused files under Assets/.
- B1: Delete dead `ScoreToAngleConverter` (~:226) + `HealthGlowBrushConverter` (~:258);
  unregister the latter in App.xaml (:33). Verify zero refs before deleting.
- B2: `AuraWarning` is red (#F85149, == AuraErr) while `AuraWarningBanner` is amber —
  same page shows both (FileShredderView). Fix: AuraWarning becomes amber (match AuraWarn),
  add `AuraDanger` = #F85149, update red-intended usages (ResultCard Danger mapping :44 +
  any error-intended of the ~25 refs — grep and classify each).
- B3: TreemapColorConverter (~:138-156): hardcoded palette, new unfrozen brush per call.
  Fix: frozen brush cache, theme-aware palette (rebuild on ThemeService.ThemeChanged),
  must not clash in light theme.
- B4: Delete orphan assets: Assets/dashboard.png, performance.png, iccon.ico, iocon3.ico.
  Check auraimage.png + icon2.png refs; if unreferenced, drop from csproj too.

## Batch C — Lazy navigation (owner: agent C)
Files: Views/MainWindow.xaml, Views/MainWindow.xaml.cs, ViewModels/MainViewModel.cs.
- C1: All 21 views are constructed eagerly (MainWindow.xaml ~:752-857) + all VMs `new()`'d
  (MainViewModel :105-125). Change to construct-on-first-navigate: keep the `_viewMap`
  + `ShowView` + `TransitionViews` flow, but back it with factory funcs + a cache, adding
  created views to the container on demand. Dashboard stays eager (default view).
- C2: Lazy child VMs (`Lazy<T>`); `IsAnyOperationRunning` must check `IsValueCreated`
  before touching `.Value` (must not force-init all VMs). Keep Quarantine/History
  refresh-on-navigate behavior. Drop/replace the :91 Uninstaller preload if it exists.
- C3: Delete dead `CurrentView` prop (MainViewModel.cs:19). Verify zero bindings first.

## Batch D — Features (owner: agent D)
Owns: Services/AutoCleanupRunner.cs, Services/CleanupHistoryService.cs (additive only),
Services/CleanupModePolicy.cs, ViewModels/SettingsViewModel.cs, Views/SettingsView.xaml,
ViewModels/CleanerViewModel.cs, Views/CleanerView.xaml (if needed),
ViewModels/CleanupHistoryViewModel.cs, Views/CleanupHistoryView.xaml.
New: Services/CleanerExcludeStore.cs, TestFeatures/FeatureBatchDTests.cs (same Run() pattern).
Must NOT touch: SettingsService.cs (agent A owns it — use Load/Save API only),
FileCleanerService.cs (lead wires service-level exclusion post-merge).
AppSettings.ScheduledCleanupCategories + CleanerExcludedPaths already exist (lead-added).
- D1: Cleaner exclude list. CleanerExcludeStore: `IsExcluded(path)` (case-insensitive;
  folder entry excludes subtree), backed by AppSettings.CleanerExcludedPaths via
  SettingsService. CleanerViewModel: filter/deselect excluded scan results with a reason
  badge. SettingsView: manage list (add file/folder via picker, remove).
- D2: Per-category scheduled cleanup. AutoCleanupRunner: when
  ScheduledCleanupCategories is non-empty, clean only those JunkTypes (validate names
  against CleanupModePolicy, fall back to Normal defaults when empty). SettingsView:
  category checkboxes. Record run in history as today.
- D3: History trend. CleanupHistoryService: add `GetDailyTrend(int days)` → per-day
  (date, ops, bytes) oldest-first. History view: simple themed bar strip (ItemsControl +
  themed rectangles, no new deps) + totals row. Must respect light/dark theme.

## Verify (lead, prove-it)
1. `dotnet build cleaner.sln` — 0 warnings, 0 errors. 2. Wire new test files into
TestFeatures Program.cs, `dotnet run` — all pass incl. new ones. 3. Wire
CleanerExcludeStore.IsExcluded into FileCleanerService remediation paths. 4. Update
CHANGELOG.md. Red build/tests → eval-chain refiner round on the owning agent's diff.
