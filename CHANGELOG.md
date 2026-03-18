# Changelog

All notable changes to AuraClean will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
