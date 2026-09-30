# Plan: Dashboard + Uninstaller batch (2026-09-30)

Skills: plan-first-one-shot (this file), vibe-coding-gate verdict = REAL CODE
(system utility, deletes files; disciplined diffs, no vibe shortcuts). prove-it at the
end: build 0/0 + full suite green. context-engineering: 2 builders, disjoint files.

## U1 — Uninstaller sortable columns + Drive column (builder 1)

Files: Models/InstalledProgram.cs (additive: DriveLetter + InstallDateParsed, both
derived, no behavior change), ViewModels/UninstallerViewModel.cs (sort state +
ApplySort, keep ApplyFilter), Views/UninstallerView.xaml (clickable headers).
Do NOT touch: UninstallerService.cs, RegistryScannerService.cs.

- InstalledProgram: `DriveLetter` (from InstallLocation root, else DisplayIcon path
  root, else "" — pure getter, never throws); `InstallDateParsed` (parse yyyyMMdd /
  yyyy-MM-dd / MM/dd/yyyy, else null — pure getter). Tests for both in Program.cs
  additions (pure logic, use sample strings, no registry).
- VM: SortColumn (enum or string: Name/Publisher/Version/Size/Installed/Drive) +
  SortDirection; ApplySort after ApplyFilter (stable: tie-break DisplayName). Default:
  Name ascending (today's behavior). Size sort uses EstimatedSizeKB with Unknown (<=0)
  LAST in both directions (a 0-size app is not the smallest — it's unknown). Date sort
  uses InstallDateParsed with unparseable LAST. Headers show ▲/▼ via the bound
  SortColumn/Direction (converter or DataTrigger — follow existing theme resources).
- XAML: replace plain Header="..." strings with clickable header buttons bound to a
  SortCommand + parameter; keep widths; add Drive column (Width 60, mono font like
  Size). Keep checkbox/select-all, search debounce, selection events untouched.

## U2 — Revo-style deep leftover scan (builder 1)

Files: Services/UninstallerService.cs (extend PostUninstallScanAsync +
ScanForRemnantFilesAsync), Services/RegistryScannerService.cs (additive: new scan
method, do not change existing ScanForOrphanedKeysAsync), TestFeatures additions.
Do NOT touch: InstallMonitorService.cs (read-only reference), VM/XAML.

- ScanForRemnantFilesAsync additions: (a) scan InstallLocation itself when it still
  exists post-uninstall (it's the highest-signal leftover); (b) recurse ONE level
  deeper in LocalAppData/Roaming/ProgramData (vendor\product nesting); (c) scan
  Startup folder entries matching terms. All behind PathSafety guards + existing
  IsSafeRemnantDirectoryMatch; restore-point + dry-run behavior unchanged.
- RegistryScannerService: new ScanForProgramTracesAsync(displayName, publisher,
  installLocation) covering Run/RunOnce values, Uninstall key itself, Services keys,
  scheduled-task names, App Paths, MUI cache — VALUE-name matching (not just key
  names), each hit a JunkItem with .reg backup path intact via existing
  BackupRegistryKeyAsync/DeleteRegistryKeyAsync. Called from PostUninstallScanAsync
  as pass 3. Conservative matching (same ≥4-char segment rule); false-positive-prone
  areas (Services) require install-path substring, not just name.
- Explicitly NOT doing (scoped out, say so in CHANGELOG): pre-install tracing
  integration (InstallMonitor deltas exist but need a UX + persistence design first),
  Store/MSIX + portable enumeration, firewall rules, shell-extension hives.
- Tests: search-term builder + DriveLetter/Date parsing + value-match logic (pure).

## D1 — Dashboard credibility + freshness (builder 2)

Files: ViewModels/MainViewModel.cs (score formula + load triggers only),
Views/DashboardView.xaml (labels + new tiles only, no structural redesign).
Do NOT touch: SystemInfoService.cs, HardwareScoreService.cs, Cleaner/Uninstaller VMs.

- Single ComputeHealthScore() used by BOTH UpdateHealthScore and RunHealthCheckAsync
  (same inputs: junk, threats, startup, browser, recency — missing inputs count 0,
  never fail). Hero tooltip already promises these four; make it true.
- Dashboard triggers Uninstaller load + Cleaner analyze on first show if never scanned
  (reuse existing commands; do not duplicate scan logic). Stat tiles get "Not scanned
  yet" empty-state copy instead of 0. Rename hero CTA "Analyze & Clean" → "Analyze"
  (it only analyzes) OR make it analyze-then-navigate to Cleaner — pick one, be
  consistent in tooltip. Rename "Undo Clean" → "Open System Restore…".
- Add two tiles reusing existing data: disk-free % (from Cleaner or DiskAnalyzerVM if
  cheap, else DriveInfo — pick cheapest, no new service) + hardware grade
  (HardwareScoreService.ComputeScores is sync? if async/expensive, show only after
  SystemInfo visit — never block dashboard).
- Explicitly NOT doing: live-ticking uptime, SystemInfoService reuse (bigger refactor,
  separate plan), quarantined-restore wiring.

## Verify (lead)

1. dotnet build cleaner.sln — 0 warnings, 0 errors.
2. New tests wired into Program.cs, full suite green.
3. Manual checklist: sort each column both directions (Unknown sizes last, bad dates
   last); drive column populated for path-having apps; uninstall a test app →
   leftover scan lists InstallLocation remnants + Run/RunOnce hits; dashboard score
   identical before/after Health Check given same inputs; tiles show empty-state.
4. CHANGELOG.md. Red → eval-chain refiner round on owning builder's diff.
