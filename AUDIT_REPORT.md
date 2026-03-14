# AuraClean — Comprehensive Project Audit Report

> **Generated for:** README authoring  
> **Scope:** Every source file in `c:\Users\SuperHands\Desktop\cleaner`  
> **Files audited:** 63 source files (`.cs`, `.xaml`, `.csproj`, `.manifest`)

---

## 1. Project Identity & Build Configuration

| Property | Value |
|---|---|
| **App Name** | AuraClean |
| **Version** | 1.0.0 |
| **Tagline** | "System Utility" (sidebar branding) |
| **Framework** | .NET 8.0 (`net8.0-windows`) |
| **OutputType** | WinExe (WPF) |
| **RootNamespace** | `AuraClean` |
| **RuntimeIdentifier** | `win-x64` |
| **SelfContained** | `true` |
| **PublishSingleFile** | `true` |
| **EnableCompressionInSingleFile** | `true` |
| **IncludeNativeLibrariesForSelfExtract** | `true` |
| **DebugType** | `embedded` |
| **Application Icon** | `Assets/icon.ico` |
| **Application Manifest** | `app.manifest` |
| **Privilege Level** | `requireAdministrator` (UAC elevation) |
| **DPI Awareness** | `PerMonitorV2` (`dpiAware=true/pm`) |
| **Supported OS** | Windows 10/11 (`{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}`) |
| **Window Dimensions** | 1100×720 (min 900×600) |
| **Startup** | `Views/MainWindow.xaml` |

### NuGet Dependencies

| Package | Version | Purpose |
|---|---|---|
| `MaterialDesignThemes` | 5.1.0 | UI component library & theming |
| `MaterialDesignColors` | 3.1.0 | Color palettes for Material Design |
| `CommunityToolkit.Mvvm` | 8.3.2 | MVVM framework (`ObservableObject`, `[RelayCommand]`, `[ObservableProperty]`) |
| `System.Management` | 8.0.0 | WMI queries (system info, restore points) |
| `System.ServiceProcess.ServiceController` | 8.0.1 | Windows service control (stop/start during cleaning) |
| `System.Data.SQLite.Core` | 1.0.119 | SQLite VACUUM for browser database compaction |

---

## 2. Architecture & Design Patterns

**Pattern:** MVVM (Model-View-ViewModel)  
**Toolkit:** CommunityToolkit.Mvvm — source generators for `[ObservableProperty]`, `[RelayCommand]`, `ObservableObject`

### Layer Breakdown

| Layer | Directory | Count | Description |
|---|---|---|---|
| **Models** | `Models/` | 3 files | Data objects: `InstalledProgram`, `JunkItem`, `ScanResult` |
| **Views** | `Views/` | 16 XAML + 16 code-behind | WPF UserControls + MainWindow |
| **ViewModels** | `ViewModels/` | 15 files | One ViewModel per view |
| **Services** | `Services/` | 20 files | All business logic (100% static classes) |
| **Converters** | `Converters/` | 1 file, 5 converters | Value converters for XAML bindings |
| **Helpers** | `Helpers/` | 2 files | Logging + formatting utilities |
| **Assets** | `Assets/` | 1 file | `icon.ico` |

### Navigation System

Code-behind dictionary maps in `MainWindow.xaml.cs`. All 15 views are pre-instantiated as XAML elements (visibility toggled). `MainViewModel.CurrentViewName` property change triggers `ShowView()` which hides all, then shows the target.

**Navigation Sections & Routes:**

| Sidebar Section | Route Key | View | ViewModel | Icon |
|---|---|---|---|---|
| — | `Dashboard` | `DashboardView` | `MainViewModel` (self) | `ViewDashboard` |
| **CLEANUP** | `Uninstaller` | `UninstallerView` | `UninstallerViewModel` | `DeleteForever` |
| | `Cleaner` | `CleanerView` | `CleanerViewModel` | `Broom` |
| | `Memory` | `MemoryBoostView` | `MemoryViewModel` | `Memory` |
| | `Browser` | `BrowserCleanerView` | `BrowserCleanerViewModel` | `Web` |
| **ANALYZE** | `StorageMap` | `StorageMapView` | `DiskAnalyzerViewModel` | `ChartDonut` |
| | `Monitor` | `InstallMonitorView` | `InstallMonitorViewModel` | `CameraOutline` |
| | `Startup` | `StartupManagerView` | `StartupManagerViewModel` | `RocketLaunch` |
| | `Duplicates` | `DuplicateFinderView` | `DuplicateFinderViewModel` | `ContentDuplicate` |
| | `LargeFiles` | `LargeFileFinderView` | `LargeFileFinderViewModel` | `FileFind` |
| **TOOLS** | `Shredder` | `FileShredderView` | `FileShredderViewModel` | `ShieldLock` |
| | `SystemInfo` | `SystemInfoView` | `SystemInfoViewModel` | `InformationOutline` |
| | `Quarantine` | `QuarantineView` | `QuarantineViewModel` | `ShieldOff` |
| **SETTINGS** | `History` | `CleanupHistoryView` | `CleanupHistoryViewModel` | `ClipboardTextClock` |
| | `Settings` | `SettingsView` | `SettingsViewModel` | `CogOutline` |

---

## 3. Design System — "Obsidian Aurora"

### Color Palette

| Token | Hex | Usage |
|---|---|---|
| `AuraBgColor` | `#080810` | Application background |
| `AuraSurfaceColor` | `#0F0F1E` | Card/panel backgrounds |
| `AuraSurfaceLightColor` | `#171730` | Elevated surface |
| `AuraSurfaceElevatedColor` | `#1F1F42` | Highest elevation |
| `AuraVioletColor` | `#7C5CFC` | Primary accent (purple) |
| `AuraCyanColor` | `#00E5C3` | Secondary accent (teal) |
| `AuraCoralColor` | `#FF6B8A` | Error / warning accent |
| `AuraMintColor` | `#5BF0D7` | Success mint |
| `AuraAmberColor` | `#FFB74D` | Caution / amber |
| `AuraTextBrightColor` | `#F0F0FF` | Headings, prominent text |
| `AuraTextColor` | `#C8C8E0` | Default body text |
| `AuraTextDimColor` | `#7B7BA0` | Secondary/muted text |
| `AuraTextMutedColor` | `#3D3D5C` | Disabled/faint text |
| `AuraBorderColor` | `#222244` | Default borders |
| `AuraBorderSubtleColor` | `#1A1A36` | Faint borders |

### Named Brushes (SolidColorBrush)

- **Surface:** `AuraBackground`, `AuraSurface`, `AuraSurfaceLight`, `AuraSurfaceElevated`
- **Accent:** `AuraAccentPurple` (#7C5CFC), `AuraAccentTeal` (#00E5C3), `AuraWarning` (#FF6B8A), `AuraSuccess` (#5BF0D7), `AuraAmber` (#FFB74D)
- **Text:** `AuraTextBright`, `AuraTextPrimary`, `AuraTextSecondary`, `AuraTextMuted`
- **Border:** `AuraBorder`, `AuraBorderSubtle`

### Gradient Brushes

| Name | Type | Colors |
|---|---|---|
| `AuraGradientAccent` | LinearGradient (diagonal) | `#7C5CFC` → `#00E5C3` |
| `AuraGradientVertical` | LinearGradient (vertical) | `#7C5CFC` → `#4A35B0` |
| `AuraGradientHorizontal` | LinearGradient (horizontal) | `#7C5CFC` → `#00E5C3` |
| `AuraGradientWarm` | LinearGradient (diagonal) | `#FF6B8A` → `#FFB74D` |
| `AuraSidebarGradient` | LinearGradient (vertical) | `#0C0C1A` → `#08080F` |

### Reusable Styles

| Style Key | TargetType | Description |
|---|---|---|
| `AuraNavButton` | `RadioButton` | Sidebar nav item with hover/selected states |
| `AuraPrimaryButton` | `Button` | Gradient fill `#7C5CFC`→`#6045E0`, white text, glow effect |
| `AuraOutlinedButton` | `Button` | Transparent fill, teal border, teal text |
| `AuraGhostButton` | `Button` | Transparent fill, subtle border, dim text |
| `AuraGlassCard` | `Border` | `AuraSurface` background, 14px corner radius, 1px border |

### Theme Configuration

```xml
<materialDesign:BundledTheme BaseTheme="Dark" PrimaryColor="DeepPurple" SecondaryColor="Teal" />
```

---

## 4. All Value Converters

Defined in `Converters/FileSizeConverter.cs`:

| Converter | Input → Output | Notes |
|---|---|---|
| `FileSizeConverter` | `long` bytes → `string` "X.X KB/MB/GB/TB" | Registered as `FileSizeConverter` |
| `BoolToVisibilityConverter` | `bool` → `Visibility` | Supports `ConverterParameter="Inverse"` |
| `InverseBoolConverter` | `bool` → `!bool` | Two-way support |
| `HealthScoreColorConverter` | `int` → `SolidColorBrush` | 0–40: `#FF6B8A` (coral), 40–70: `#FFB74D` (amber), 70–100: `#00E5C3` (cyan) |
| `TreemapColorConverter` | `int` index → `SolidColorBrush` | 10-color palette: `#7C5CFC`, `#00E5C3`, `#FF6B8A`, `#FFB74D`, `#64B5F6`, `#5BF0D7`, `#E073AD`, `#FFD54F`, `#4DD0E1`, `#A088C0` |

---

## 5. All Models

### `InstalledProgram` — `ObservableObject`

| Property | Type | Notes |
|---|---|---|
| `DisplayName` | `string` | `[ObservableProperty]` |
| `DisplayVersion` | `string` | `[ObservableProperty]` |
| `Publisher` | `string` | `[ObservableProperty]` |
| `InstallLocation` | `string` | `[ObservableProperty]` |
| `UninstallString` | `string` | `[ObservableProperty]` |
| `QuietUninstallString` | `string` | `[ObservableProperty]` |
| `DisplayIcon` | `string` | `[ObservableProperty]` |
| `InstallDate` | `string` | `[ObservableProperty]` |
| `EstimatedSizeKB` | `long` | `[ObservableProperty]` |
| `IsWindowsInstaller` | `bool` | `[ObservableProperty]` |
| `IsSelected` | `bool` | `[ObservableProperty]` |
| `RegistryKeyPath` | `string` | Regular property |
| `RegistryView` | `RegistryView` | Regular property |
| `FormattedSize` | `string` | Computed: KB/MB/GB formatting |

### `JunkItem` — `ObservableObject`

| Property | Type | Default |
|---|---|---|
| `IsSelected` | `bool` | `true` |
| `Path` | `string` | |
| `Description` | `string` | |
| `Type` | `JunkType` | |
| `SizeBytes` | `long` | |
| `IsLocked` | `bool` | |
| `LockingProcess` | `string?` | |
| `LastModified` | `DateTime?` | |
| `Category` | `string` | Computed via `switch` on `JunkType` |
| `FormattedSize` | `string` | Computed |

**`JunkType` Enum (17 values):**

`TempFile`, `WindowsUpdateCache`, `Prefetch`, `CrashDump`, `BranchCache`, `ThumbnailCache`, `OrphanedRegistryKey`, `RemnantDirectory`, `AbandonedFile`, `BrowserCache`, `BrowserTracking`, `RecycleBin`, `DeliveryOptimization`, `WindowsErrorReporting`, `FontCache`, `LogFile`, `WindowsOld`

### `ScanResult` — `ObservableObject`

| Property | Type |
|---|---|
| `Items` | `ObservableCollection<JunkItem>` |
| `TotalSizeBytes` | `long` (computed) |
| `TotalCount` | `int` (computed) |
| `SelectedCount` | `int` (computed) |
| `FormattedTotalSize` | `string` (computed) |
| `GroupedByCategory` | `IEnumerable<IGrouping>` (computed) |

**Methods:** `AddRange(items)`, `Clear()`

---

## 6. All Services (20 Static Classes)

### 6.1 `FileCleanerService` — System Junk Scanner & Cleaner

**Scans 14 categories:**

| # | Category | Target |
|---|---|---|
| 1 | Windows Temp | `%WINDIR%\Temp` |
| 2 | User Temp | `%TEMP%` |
| 3 | Prefetch | `%WINDIR%\Prefetch\*.pf` |
| 4 | Crash Dumps | `%LOCALAPPDATA%\CrashDumps` |
| 5 | System Minidumps | `%WINDIR%\Minidump` |
| 6 | Windows Update Cache | `%WINDIR%\SoftwareDistribution\Download` |
| 7 | BranchCache | `%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\PeerDistRepub` |
| 8 | Thumbnail Cache | `thumbcache_*.db` in `%LOCALAPPDATA%\Microsoft\Windows\Explorer` |
| 9 | Recycle Bin | `$Recycle.Bin` (all drives) |
| 10 | Delivery Optimization | `%WINDIR%\SoftwareDistribution\DeliveryOptimization` |
| 11 | Windows Error Reporting | `%LOCALAPPDATA%\Microsoft\Windows\WER` |
| 12 | Font Cache | `%WINDIR%\ServiceProfiles\LocalService\AppData\Local\FontCache` |
| 13 | Windows Logs | `%WINDIR%\Logs` |
| 14 | Windows.old | `C:\Windows.old` |

**Methods:** `AnalyzeSystemJunkAsync(settings, progress, ct)`, `CleanItemsAsync(items, progress, ct)`

**Notable behavior:**
- Stops services (`wuauserv`, `FontCache`, `WSearch`) during cleaning, restarts after
- Detects locked files (error `0x80070020`), uses `FileLockDetector`
- Bottom-up empty directory removal after cleaning

### 6.2 `BrowserCleanerService` — Chromium Browser Privacy Clean

**Supported Browsers:** Chrome, Edge, Brave, Opera, Vivaldi

**Cleanable sub-paths:** `Cache`, `Code Cache`, `GPUCache`, `Service Worker`, `ShaderCache`, `GrShaderCache`, `DawnCache`, `Storage\ext`, `blob_storage`, `IndexedDB`, `Session Storage`, `Local Storage\leveldb`, `Crashpad`

**Vacuumable DB files:** `History`, `Favicons`, `Cookies`, `Web Data`, `Login Data`, `Top Sites`, `Network Action Predictor`, `Shortcuts`

**Tracking patterns:** `Reporting and NEL`, `Trust Tokens`, `optimization_guide*`, `BudgetDatabase`, `commerce_subscription_db`, `Segmentation Platform`, `Site Characteristics Database`

**Methods:** `DetectBrowsers()`, `ScanBrowserAsync(profile, options, progress, ct)`, `CleanBrowserAsync(profile, scan, options, progress, ct)`, `FlushDnsCacheAsync()`

**Records:** `BrowserProfile`, `BrowserScanResult`, `VacuumTarget`, `BrowserCleanResult`

### 6.3 `RegistryScannerService` — Orphaned Registry Key Scanner

**Scans:** `HKCU\Software`, `HKLM\Software` (64-bit and 32-bit views)  
**Max depth:** 6 levels  
**Methods:** `ScanForOrphanedKeysAsync(programName, publisher)`, `BackupRegistryKeyAsync(keyPath)`, `DeleteRegistryKeyAsync(keyPath)`  
**Safety:** Backs up keys via `reg.exe export` before deletion; noise-word filtering (Windows, Microsoft, etc.)

### 6.4 `UninstallerService` — Program Uninstaller

**Registry sources:** `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` (64/32-bit), `HKCU\...\Uninstall`  
**Methods:** `GetInstalledProgramsAsync()`, `RunUninstallAsync(program)`, `PostUninstallScanAsync(program)`, `GetDirectorySize(path)`  
**Notable:** Filters `SystemComponent=1`, deduplicates by `DisplayName`, parses MSI GUIDs, handles quoted/unquoted exe paths

### 6.5 `DuplicateFinderService` — SHA-256 Duplicate Detection

**Algorithm:** 3-pass: (1) Group by file size → (2) Partial hash (first 4KB) → (3) Full SHA-256  
**Methods:** `ScanForDuplicatesAsync(rootPath, minSizeBytes=1024, maxSizeMB=500, fileExtensions, recursive)`, `DeleteDuplicatesAsync(files)`  
**Classes:** `DuplicateGroup`, `DuplicateFileEntry`, `DuplicateScanResult`

### 6.6 `LargeFileFinderService` — Space Hog Detector

**File categories:** Video, Audio, Disk Image, Archive, Executable, Log/Text, Backup/Temp, Virtual Disk, Image (Large), Database, Other  
**Methods:** `ScanAsync(rootPath, minimumSizeBytes, maxResults=500, includeSystemDirs, progress, ct)`, `GetDrives()`, `DeleteFile(path)`  
**Algorithm:** Stack-based directory traversal (no recursion), bounded results (max 500)  
**Exclusions:** `Windows`, `$Recycle.Bin`, `System Volume Information`, `$WinREAgent`, `Recovery`, `PerfLogs`

### 6.7 `DiskAnalyzerService` — Treemap Visualization

**Methods:** `AnalyzeDirectoryAsync(rootPath, maxDepth=4, progress, ct)`, `GetDriveStats()`  
**Classes:** `DiskNode` (Name, FullPath, SizeBytes, IsDirectory, FileCount, DirectoryCount, SizePercent, Children), `AnalysisResult`, `DriveStats`  
**Constraints:** Max 50 children per node, top-20 tracking with `SortedList`, lowers thread priority during scan

### 6.8 `MemoryManagerService` — RAM Optimizer

**P/Invoke:** `psapi.dll` (`EmptyWorkingSet`), `kernel32.dll` (`OpenProcess`, `CloseHandle`), `ntdll.dll` (`NtSetSystemInformation`)  
**Constants:** `SystemMemoryListInformation = 80`, `MemoryPurgeStandbyList = 4`  
**Methods:** `BoostMemoryAsync(purgeStandbyList, dryRun, progress)`, `GetMemorySnapshotAsync()`  
**Records:** `BoostResult(MemoryFreedBytes, ProcessesTrimmed, ProcessesSkipped, StandbyListPurged, WorkingSetBefore, WorkingSetAfter)`, `MemorySnapshot(TotalPhysicalBytes, UsedBytes, AvailableBytes, UsagePercent)`  
**Protected processes:** `system`, `idle`, `registry`, `smss`, `csrss`, `wininit`, `services`, `lsass`, `svchost`, `dwm`, `explorer`, `winlogon`, `fontdrvhost`, `auraclean`

### 6.9 `FileShredderService` — Secure File Deletion

**Enum `ShredAlgorithm`:**

| Value | Passes | Pattern |
|---|---|---|
| `QuickZero` | 1 | All zeros |
| `Random` | 1 | Cryptographic random |
| `DoD3Pass` | 3 | Zeros → ones → random |
| `Enhanced7Pass` | 7 | Alternating `0x00`, `0xFF`, random patterns |

**Methods:** `ShredFilesAsync(files, algorithm, progress, ct)`, `GetPassCount(alg)`, `GetAlgorithmDescription(alg)`  
**Record:** `ShredResult(FilesShredded, FilesFailed, TotalBytesOverwritten, Errors)`  
**Security:** Uses `RandomNumberGenerator.Fill` (crypto-secure), renames file to random name before deletion, 64KB write buffer

### 6.10 `ForceDeleteService` — Locked File Remover

**P/Invoke:** `kernel32.dll` `MoveFileEx` with `MOVEFILE_DELAY_UNTIL_REBOOT`

**Enum `ForceDeleteAction`:** `DeletedDirectly`, `TerminatedAndDeleted`, `ScheduledForBootDeletion`, `DryRunOnly`, `Failed`

**Strategy:** 3-attempt cascade → (1) Direct delete → (2) Identify & kill lockers → (3) Schedule boot-time deletion

**Protected processes:** `system`, `csrss`, `lsass`, `svchost`, `dwm`, `explorer`, `smss`, `wininit`, `services`, `winlogon`, `fontdrvhost`

**Methods:** `ForceDeleteAsync(path, terminateLockers, scheduleBootDelete, dryRun)`, `ForceUninstallAsync(program, dryRun)`

### 6.11 `FileLockDetector` — Process Lock Detection

**P/Invoke:** `rstrtmgr.dll` (Restart Manager API) — `RmStartSession`, `RmRegisterResources`, `RmGetList`, `RmEndSession`  
**Methods:** `IsLocked(filePath)`, `GetLockingProcesses(filePath)`  
**Fallback:** Simple `File.Open` exclusive access test

### 6.12 `StartupManagerService` — Boot Program Manager

**Scans 4 sources:**

| Source | Enum Value | Location |
|---|---|---|
| Registry (Current User) | `RegistryCurrentUser` | `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` |
| Registry (Local Machine) | `RegistryLocalMachine` | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (64/32) |
| Startup Folder | `StartupFolder` | User + All Users startup directories |
| Task Scheduler | `TaskScheduler` | `schtasks.exe /Query` (boot/logon triggers) |

**Enum `StartupImpact`:** `Unknown`, `None`, `Low`, `Medium`, `High`  
**Impact estimation:** >50MB = High, >10MB = Medium, >1MB = Low, else = None  
**Toggle mechanism:** `StartupApproved` registry binary values (`0x02` = enabled, `0x03` = disabled)  
**Methods:** `GetStartupEntriesAsync(progress)`, `ToggleStartupEntryAsync(entry, enable)`, `DeleteStartupEntryAsync(entry)`

### 6.13 `HeuristicScannerService` — Abandoned File Detector

**Scans:** `%LOCALAPPDATA%`, `%APPDATA%`, `%PROGRAMDATA%`  
**Criteria:** Directories with no matching registry entry AND no modifications in > 180 days  
**Methods:** `ScanForAbandonedFilesAsync(dayThreshold, progress, ct)`  
**Performance:** `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 2`, caps directory estimation at 1000 files

### 6.14 `InstallMonitorService` — Shadow Installation Tracker

**Concept:** Before/after snapshot diffing to track what software installs  
**Storage:** `%LocalAppData%\AuraClean\Snapshots\` (JSON serialization)

**Monitored locations:**
- `HKLM\SOFTWARE`, `HKCU\SOFTWARE`, `HKLM\SYSTEM\CurrentControlSet\Services`
- `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%PROGRAMDATA%`, `%LOCALAPPDATA%`, `%APPDATA%`

**Classes:** `SystemSnapshot` (RegistryEntries, FileEntries, DirectoryEntries), `SnapshotDelta` (NewRegistryKeys, NewRegistryValues, NewFiles, NewDirectories, TotalNewFileSizeBytes), `FileChange`

**Methods:** `TakeSnapshotAsync(label, dryRun, progress)`, `CompareAndGenerateDeltaAsync(snapshotId, dryRun, progress)`, `ListSnapshotsAsync()`, `DeleteSnapshot(id)`

### 6.15 `QuarantineService` — Safe File Isolation

**Storage:** `%LocalAppData%\AuraClean\Quarantine\` with `manifest.json`

**Classes:**
- `QuarantineManifest` — container for entries list
- `QuarantineEntry` — `Id`, `OriginalPath`, `StoredFileName`, `Reason`, `QuarantinedAt`, `FileSizeBytes`; computed: `IsExpired`, `ExpiresIn`, `FileName`, `SizeDisplay`, `QuarantinedAtDisplay`
- `QuarantineStats` — `TotalItems`, `TotalSizeBytes`, `OldestEntry`, `NewestEntry`

**Methods:** `QuarantineFileAsync(path, reason, progress)`, `QuarantineFilesAsync(paths, reason, progress)`, `RestoreFileAsync(id, progress)`, `PurgeFileAsync(id, progress)`, `PurgeExpiredAsync(progress)`, `GetAllEntries()`, `GetStats()`, `GetQuarantineDirectory()`

**Conflict handling:** Backs up existing file with `.aura_backup` extension before restoring

### 6.16 `SettingsService` — Persistent Configuration

**Storage:** `%LocalAppData%\AuraClean\Settings\settings.json`  
**Thread safety:** `lock` object + `_cachedSettings` singleton  
**Methods:** `Load()`, `Save(settings)`, `ResetToDefaults()`, `GetSettingsDirectory()`, `InvalidateCache()`

**`AppSettings` Properties & Defaults:**

| Setting | Type | Default |
|---|---|---|
| `CreateRestorePointBeforeClean` | `bool` | `true` |
| `DryRunMode` | `bool` | `false` |
| `ShowConfirmationDialogs` | `bool` | `true` |
| `MinimizeToTray` | `bool` | `false` |
| `LaunchAtStartup` | `bool` | `false` |
| `CleanTempFiles` | `bool` | `true` |
| `CleanWindowsUpdate` | `bool` | `true` |
| `CleanPrefetch` | `bool` | `true` |
| `CleanCrashDumps` | `bool` | `true` |
| `CleanRecycleBin` | `bool` | `true` |
| `CleanBrowserCache` | `bool` | `true` |
| `CleanThumbnailCache` | `bool` | `true` |
| `CleanWindowsLogs` | `bool` | `true` |
| `RunHeuristicScan` | `bool` | `false` |
| `AbandonedFileDaysThreshold` | `int` | `180` |
| `DefaultShredAlgorithm` | `string` | `"DoD3Pass"` |
| `DefaultLargeFileSizeMb` | `long` | `100` |
| `DefaultMinDuplicateSizeMb` | `long` | `1` |
| `QuarantineRetentionDays` | `int` | `30` |
| `AutoPurgeExpiredQuarantine` | `bool` | `true` |
| `MaxHistoryEntries` | `int` | `500` |
| `LogCleanupOperations` | `bool` | `true` |

### 6.17 `CleanupHistoryService` — Operation History

**Storage:** `%LocalAppData%\AuraClean\History\cleanup_history.json`  
**Methods:** `LogOperation(record)`, `LoadHistory()`, `ClearHistory()`, `GetSummary()`, `ExportAsText()`, `GetHistoryDirectory()`

**`CleanupOperationType` Enum (9 values):** `SystemClean`, `BrowserClean`, `RegistryClean`, `Uninstall`, `DuplicateRemoval`, `LargeFileRemoval`, `MemoryBoost`, `FileShred`, `QuarantinePurge`

**Models:** `CleanupHistory`, `CleanupRecord` (Timestamp, OperationType, Summary, Details, ItemCount, BytesFreed, WasDryRun, DurationMs, TimestampDisplay), `HistorySummary`

### 6.18 `RestorePointService` — System Restore Integration

**Uses WMI:** `ManagementClass("SystemRestore")`  
**Methods:** `CreateRestorePointAsync(description = "AuraClean Pre-Cleanup")`  
**Settings:** `RestorePointType = 12` (MODIFY_SETTINGS), `EventType = 100` (BEGIN_SYSTEM_CHANGE)  
**Setup:** Sets `SystemRestorePointCreationFrequency = 0` to allow frequent restore points

### 6.19 `ContextMenuService` — Explorer Integration

**Methods:** `IsContextMenuInstalled()`, `InstallContextMenu(dryRun)`, `UninstallContextMenu(dryRun)`, `GenerateRegistryScript()`, `ExportRegistryScript()`  
**Registry paths:** `SOFTWARE\Classes\exefile\shell\AuraCleanUninstall`, `SOFTWARE\Classes\Msi.Package\shell\AuraCleanUninstall`  
**Label:** "Deep Uninstall with AuraClean"

### 6.20 `SystemInfoService` — Hardware/Software Information

**WMI Queries:** `Win32_OperatingSystem`, `Win32_Processor`, `Win32_PhysicalMemory`, `Win32_VideoController`, `Win32_DiskDrive`, `Win32_NetworkAdapter`, `Win32_BaseBoard`, `Win32_BIOS`

**Categories:** Operating System, Processor, Memory, Graphics, Storage, Network, Motherboard, Runtime

**Record:** `InfoEntry(Category, Label, Value, Icon)`  
**Methods:** `CollectAllAsync(progress)`, `FormatAsText(entries)`

---

## 7. All ViewModels (15 Files)

### 7.1 `MainViewModel` — Root Orchestrator

**Observable Properties:** `CurrentView`, `CurrentViewName` ("Dashboard"), `SystemHealthScore` (85), `HealthLabel` ("Good"), `IsAdmin`, `StatusBarText`, `LastCleanedDate`, `OsName`, `CpuName`, `TotalRam`, `SystemUptime`, `MachineName`, `UserName`, `IsContextMenuInstalled`, `ContextMenuStatus`

**Child ViewModels (14):** `Uninstaller`, `Cleaner`, `Memory`, `InstallMonitor`, `BrowserCleaner`, `DiskAnalyzer`, `StartupManager`, `DuplicateFinder`, `FileShredder`, `LargeFileFinder`, `SystemInfo`, `Settings`, `CleanupHistory`, `Quarantine`

**Commands:** `NavigateTo(viewName)`, `QuickAnalyzeAsync()`, `QuickCleanAsync()`, `BoostMemoryAsync()`, `ToggleContextMenu()`, `ExportContextMenuScript()`

**Health Score:** Base 100, deducts up to 50 for junk size, deducts 5–15 for stale cleaning

### 7.2 `CleanerViewModel` — System Cleaner

**Properties:** `Categories` (ObservableCollection of `JunkCategory`), `IsBusy`, `IsAnalyzing`, `StatusMessage`, `HasResults`, `TotalJunkSize`, `TotalJunkCount`, `ProgressValue`  
**Commands:** `AnalyzeAsync()`, `CleanSelectedAsync()`, `SelectAll()`, `DeselectAll()`  
**Helper class:** `JunkCategory` (Name, IsExpanded, IsAllSelected, Items, ItemCount, FormattedTotalSize)

### 7.3 `UninstallerViewModel` — Program Removal

**Properties:** `Programs`, `FilteredPrograms`, `SelectedProgram`, `SearchText`, `IsBusy`, `StatusMessage`, `IsScanning`, `PostUninstallJunk`, `HasPostUninstallResults`, `IsDryRun`  
**Commands:** `LoadProgramsAsync()`, `UninstallSelectedAsync()`, `DeepScanAsync()`, `CleanLeftoversAsync()`, `ForceUninstallAsync()`

### 7.4 `BrowserCleanerViewModel` — Privacy Clean

**Properties:** `IsBusy`, `StatusMessage`, `HasResults`, `IsDryRun`, `BrowserResults`, `TotalSizeBytes`, `TotalSavingsBytes`, `TotalItemCount`, `CleanCache` (true), `VacuumDatabases` (true), `CleanTracking` (true), `FlushDns` (true)  
**Commands:** `ScanBrowsersAsync()`, `CleanSelectedAsync()`

### 7.5 `MemoryViewModel` — RAM Booster

**Properties:** `IsBusy`, `StatusMessage`, `IsDryRun`, `TotalRamBytes`, `UsedRamBytes`, `AvailableRamBytes`, `UsagePercent`, `LastFreedBytes`, `LastTrimmedCount`, `HasBoostResult`, `StandbyPurged`  
**Commands:** `RefreshStatsAsync()`, `BoostMemoryAsync()`

### 7.6 `DiskAnalyzerViewModel` — Storage Treemap

**Properties:** `IsBusy`, `StatusMessage`, `HasResults`, `ProgressPercent`, `SelectedPath`, `Drives`, `SelectedDrive`, `AnalysisResult`, `TreemapData`, `LargestFiles`, `LargestDirs`, `CurrentPath`, `Breadcrumbs`, `TotalSizeBytes`, `TotalFileCount`, `TotalDirCount`, `ScanDuration`  
**Commands:** `AnalyzeAsync()`, `CancelAnalysis()`, `NavigateToNode()`, `NavigateUp()`, `NavigateToBreadcrumb()`, `OpenInExplorer()`  
**Helper classes:** `TreemapNode`, `LargeFileEntry`, `DriveEntry`, `BreadcrumbItem`

### 7.7 `DuplicateFinderViewModel` — Duplicate Detection

**Properties:** `IsBusy`, `StatusMessage`, `HasResults`, `SelectedPath`, `ProgressPercent`, `MinSizeKB` (1), `MaxSizeMB` (500), `Recursive` (true), `ExtensionFilter`, `DuplicateGroups`, `TotalGroupCount`, `TotalDuplicateCount`, `TotalWastedBytes`, `TotalFilesScanned`, `ScanDuration`, `QuickPaths`  
**Commands:** `ScanAsync()`, `CancelScan()`, `DeleteSelectedAsync()`, `SelectAllDuplicates()`, `DeselectAll()`, `OpenInExplorer()`

### 7.8 `FileShredderViewModel` — Secure Delete

**Properties:** `Files`, `SelectedAlgorithm` (DoD3Pass), `SelectedAlgorithmOption`, `IsBusy`, `StatusMessage`, `ProgressCurrent`, `ProgressTotal`, `ProgressText`, `HasResults`, `LastShredded`, `LastFailed`, `LastBytesOverwritten`, `AlgorithmDescription`  
**Algorithms Collection:** 4 `AlgorithmOption` records (QuickZero/1, Random/1, DoD3Pass/3, Enhanced7Pass/7)  
**Commands:** `AddFiles()`, `AddFolder()`, `RemoveFile()`, `ClearAll()`, `ShredAllAsync()`

### 7.9 `LargeFileFinderViewModel` — Big File Scanner

**Properties:** `Files`, `IsBusy`, `StatusMessage`, `ScanPath`, `MinimumSizeMB` (100), `ProgressFilesScanned`, `ProgressFilesFound`, `ProgressCurrentDir`, `SelectedFile`, `HasResults`, `TotalFilesScanned`, `ResultCount`, `TotalSizeFound`, `AccessErrors`, `Drives`, `SelectedDrive`, `FilterText`, `FilterCategory`, `FilteredFiles`  
**Categories:** "All", "Video", "Audio", "Archive", "Disk Image", "Executable", "Backup / Temp", "Virtual Disk", "Database", "Log / Text", "Image (Large)", "Other"  
**Size presets:** 10, 25, 50, 100, 250, 500, 1000 MB  
**Commands:** `ScanAsync()`, `CancelScan()`, `BrowseFolder()`, `OpenFileLocation()`, `DeleteSelected()`

### 7.10 `StartupManagerViewModel` — Startup Program Control

**Properties:** `Entries`, `FilteredEntries`, `SelectedEntry`, `IsBusy`, `StatusMessage`, `SearchText`, `ShowDisabledOnly`, `ShowEnabledOnly`, `TotalCount`, `EnabledCount`, `DisabledCount`, `HighImpactCount`  
**Commands:** `LoadEntriesAsync()`, `ToggleSelectedAsync()`, `DeleteSelectedAsync()`, `DisableAllHighImpactAsync()`, `OpenFileLocation()`

### 7.11 `InstallMonitorViewModel` — Installation Snapshot

**Properties:** `IsBusy`, `IsMonitoring`, `StatusMessage`, `ProgramLabel`, `ActiveSnapshotId`, `IsDryRun`, `HasDelta`, `NewRegistryKeysCount`, `NewFilesCount`, `NewFileSizeBytes`, `NewDirectoriesCount`, `SavedSnapshots`, `SelectedSnapshot`  
**Commands:** `StartMonitoringAsync()`, `StopMonitoringAsync()`, `CancelMonitoring()`, `LoadSnapshotsAsync()`, `DeleteSnapshotAsync()`  
**Helper class:** `SnapshotEntry` (Id, Label, Timestamp, HasDelta, FormattedTimestamp)

### 7.12 `SystemInfoViewModel` — System Details

**Properties:** `Entries`, `FilteredEntries`, `IsBusy`, `StatusMessage`, `FilterCategory`, `SearchText`  
**Categories:** "All", "Operating System", "Processor", "Memory", "Graphics", "Storage", "Network", "Motherboard", "Runtime"  
**Commands:** `LoadInfoAsync()`, `FilterByCategory(category)`, `CopyToClipboard()`, `RefreshAsync()`

### 7.13 `SettingsViewModel` — App Configuration

**Properties:** Mirrors all 22 `AppSettings` properties + `StatusMessage`, `HasUnsavedChanges`, `SettingsPath`  
**Presets:** `ShredAlgorithms`: ["QuickZero", "Random", "DoD3Pass", "Enhanced7Pass"], `LargeFileSizePresets`: [50, 100, 250, 500, 1024], `RetentionDayPresets`: [7, 14, 30, 60, 90], `HistoryLimitPresets`: [100, 250, 500, 1000, 2000]  
**Commands:** `SaveSettings()`, `ResetToDefaults()`, `ReloadSettings()`  
**Change tracking:** Every `partial void On...Changed()` sets `HasUnsavedChanges = true`

### 7.14 `CleanupHistoryViewModel` — Operation Log

**Properties:** `Records`, `FilteredRecords`, `IsBusy`, `StatusMessage`, `FilterType`, `SearchText`, `TotalOperations`, `TotalBytesFreed`, `TotalItemsCleaned`, `LastOperationDate`, `TotalBytesFreedDisplay`  
**Filter types:** "All", "System Cleanup", "Browser Privacy Clean", "Registry Cleanup", "Program Uninstall", "Duplicate Removal", "Large File Removal", "RAM Boost", "Secure Shred", "Quarantine Purge"  
**Commands:** `LoadHistory()`, `ClearHistory()`, `ExportHistory()`, `CopyToClipboard()`, `FilterByType(type)`

### 7.15 `QuarantineViewModel` — Quarantine Manager

**Properties:** `Entries`, `IsBusy`, `StatusMessage`, `TotalItems`, `TotalSizeDisplay`, `ExpiredCount`, `QuarantinePath`  
**Commands:** `LoadEntries()`, `AddFilesToQuarantineAsync()`, `RestoreSelectedAsync()`, `PurgeSelectedAsync()`, `PurgeExpiredAsync()`, `SelectAll()`, `DeselectAll()`, `OpenQuarantineFolder()`  
**Wrapper class:** `QuarantineEntryItem` (wraps `QuarantineEntry` + adds `IsSelected`)

---

## 8. All Views (16 XAML Files)

### View Titles & Subtitles

| View | Display Title | Subtitle |
|---|---|---|
| Dashboard | "System Health" | "Monitor and optimize your system performance" |
| Uninstaller | "Surgical Uninstaller" | "Remove programs completely — including leftover files and registry keys." |
| Cleaner | "System Cleaner" | "Deep-clean temp files, caches, crash dumps, and abandoned data." |
| Memory | "Memory Optimizer" | "One-Click Boost — Flush working sets and reclaim physical RAM" |
| Browser | "Privacy Clean" | "Deep cache purge, SQLite vacuum, and tracking data removal for Chromium browsers" |
| StorageMap | "Storage Map" | "Visual disk analyzer — identify large files and forgotten backups" |
| InstallMonitor | "Install Monitor" | "Shadow snapshot — track what programs install for surgical removal later" |
| Startup | "Startup Manager" | "Control which programs launch at Windows startup — speed up your boot time." |
| Duplicates | "Duplicate Finder" | "Find and remove duplicate files wasting disk space using SHA-256 verification." |
| LargeFiles | "Large File Finder" | "Discover space-hogging files and reclaim disk space" |
| Shredder | "File Shredder" | "Securely delete files by overwriting data — making recovery impossible" |
| SystemInfo | "System Information" | "Detailed hardware and software inventory of your system" |
| Quarantine | "Quarantine Manager" | "Safely isolate suspicious files — restore or permanently delete them later" |
| History | "Cleanup History" | "Review past cleanup operations and track space recovered" |
| Settings | "App Settings" | "Configure AuraClean preferences and defaults" |

### Code-Behind Complexity

| View | Has Non-Trivial Code-Behind |
|---|---|
| `MainWindow.xaml.cs` | Yes — navigation dictionary, `ShowView()`, PropertyChanged subscription, auto-loads installed programs |
| `StorageMapView.xaml.cs` | Yes — `PercentToWidthValueConverter` (inline IValueConverter, 480px base width) |
| `SystemInfoView.xaml.cs` | Yes — applies `PropertyGroupDescription("Category")` grouping to `FilteredEntries` on load/change |
| All other views | No — empty constructors calling `InitializeComponent()` only |

### Dashboard Components

- **Health Gauge:** Circular `ProgressBar` (MaterialDesign style), decorative rings, center score display
- **Stat Cards:** Junk Found, Programs Installed, Last Cleaned
- **System Info Strip:** OS, CPU, RAM, Uptime (using `UniformGrid`)
- **Action Buttons:** ANALYZE SYSTEM (primary), QUICK CLEAN (outlined), BOOST RAM (ghost), STORAGE MAP (ghost)
- **Context Menu Status** display

---

## 9. P/Invoke & Native Interop

| Service | DLL | Function | Purpose |
|---|---|---|---|
| `FileLockDetector` | `rstrtmgr.dll` | `RmStartSession` | Start Restart Manager session |
| | `rstrtmgr.dll` | `RmRegisterResources` | Register files to check |
| | `rstrtmgr.dll` | `RmGetList` | Get list of processes locking files |
| | `rstrtmgr.dll` | `RmEndSession` | End Restart Manager session |
| `MemoryManagerService` | `psapi.dll` | `EmptyWorkingSet` | Trim process working set |
| | `kernel32.dll` | `OpenProcess` | Open process handle |
| | `kernel32.dll` | `CloseHandle` | Close process handle |
| | `ntdll.dll` | `NtSetSystemInformation` | Purge standby memory list |
| `ForceDeleteService` | `kernel32.dll` | `MoveFileEx` | Schedule file delete on reboot |

---

## 10. Data Persistence Locations

| Feature | Path | Format |
|---|---|---|
| Settings | `%LocalAppData%\AuraClean\Settings\settings.json` | JSON |
| Cleanup History | `%LocalAppData%\AuraClean\History\cleanup_history.json` | JSON |
| Quarantine | `%LocalAppData%\AuraClean\Quarantine\` + `manifest.json` | Binary files + JSON manifest |
| Install Snapshots | `%LocalAppData%\AuraClean\Snapshots\` | JSON |
| Diagnostic Logs | `%LocalAppData%\AuraClean\Logs\AuraClean_{date}.log` | Text |
| Crash Log | `%USERPROFILE%\Desktop\AuraClean_crash.log` | Text |
| Last Cleaned Stamp | `%LocalAppData%\AuraClean\last_cleaned.txt` | Text (ISO date) |

---

## 11. Error Handling & Diagnostics

### Global Exception Handling (`App.xaml.cs`)

- `AppDomain.CurrentDomain.UnhandledException` — logs + MessageBox
- `DispatcherUnhandledException` — logs + MessageBox, sets `e.Handled = true`
- `TaskScheduler.UnobservedTaskException` — logs + sets observed
- All write to `Desktop\AuraClean_crash.log`

### Diagnostic Logger (`DiagnosticLogger.cs`)

- Static methods: `Info(source, message)`, `Warn(source, message, ex?)`, `Error(source, message, ex)`
- Outputs to: `Debug.WriteLine` + file at `%LocalAppData%\AuraClean\Logs\AuraClean_{yyyy-MM-dd}.log`

---

## 12. Test Project (`TestFeatures/`)

**Console test runner** with Assert helper (pass/fail counters).

**3 Test Suites:**

| Suite | Tests |
|---|---|
| `TestSystemInfoService` | `CollectAllAsync()` returns entries, `FormatAsText()` produces output, cancellation token works |
| `TestLargeFileFinderService` | Creates temp files of various sizes, scans, verifies count and category assignment |
| `TestFileShredderService` | Tests all 4 algorithms (QuickZero, Random, DoD3Pass, Enhanced7Pass), non-existent file, empty list |

---

## 13. Feature Summary Matrix

| # | Feature | Service | ViewModel | View | Key Capability |
|---|---|---|---|---|---|
| 1 | **Dashboard** | Multiple | `MainViewModel` | `DashboardView` | Health score gauge, quick actions, system info strip |
| 2 | **Surgical Uninstaller** | `UninstallerService` | `UninstallerViewModel` | `UninstallerView` | Uninstall + post-uninstall deep scan for registry/file leftovers |
| 3 | **System Cleaner** | `FileCleanerService`, `HeuristicScannerService` | `CleanerViewModel` | `CleanerView` | 14 junk categories, restore point before clean, locked file detection |
| 4 | **RAM Booster** | `MemoryManagerService` | `MemoryViewModel` | `MemoryBoostView` | Working set trim, standby list purge, usage gauge |
| 5 | **Privacy Clean** | `BrowserCleanerService` | `BrowserCleanerViewModel` | `BrowserCleanerView` | 5 Chromium browsers, cache clean, SQLite VACUUM, DNS flush |
| 6 | **Storage Map** | `DiskAnalyzerService` | `DiskAnalyzerViewModel` | `StorageMapView` | Treemap visualization, breadcrumb navigation, largest files/dirs |
| 7 | **Install Monitor** | `InstallMonitorService` | `InstallMonitorViewModel` | `InstallMonitorView` | Before/after snapshots, registry & file diff |
| 8 | **Startup Manager** | `StartupManagerService` | `StartupManagerViewModel` | `StartupManagerView` | Enable/disable/delete startup entries, impact rating |
| 9 | **Duplicate Finder** | `DuplicateFinderService` | `DuplicateFinderViewModel` | `DuplicateFinderView` | 3-pass SHA-256, size/extension filters, keep/delete selection |
| 10 | **Large File Finder** | `LargeFileFinderService` | `LargeFileFinderViewModel` | `LargeFileFinderView` | Category detection, drive selection, filter/search |
| 11 | **File Shredder** | `FileShredderService` | `FileShredderViewModel` | `FileShredderView` | 4 algorithms (1–7 passes), crypto-random overwrite |
| 12 | **System Info** | `SystemInfoService` | `SystemInfoViewModel` | `SystemInfoView` | 8 WMI categories, copy/export, grouped display |
| 13 | **Quarantine** | `QuarantineService` | `QuarantineViewModel` | `QuarantineView` | Isolate/restore/purge files, expiry tracking |
| 14 | **Cleanup History** | `CleanupHistoryService` | `CleanupHistoryViewModel` | `CleanupHistoryView` | Operation log, export, summary stats |
| 15 | **Settings** | `SettingsService` | `SettingsViewModel` | `SettingsView` | 22 configurable options, save/reset/reload |
| 16 | **Force Delete** | `ForceDeleteService` | (via Uninstaller) | — | Kill lockers, schedule boot deletion |
| 17 | **File Lock Detection** | `FileLockDetector` | (via Cleaner) | — | Restart Manager API integration |
| 18 | **System Restore** | `RestorePointService` | (via Cleaner) | — | WMI-based restore point creation |
| 19 | **Context Menu** | `ContextMenuService` | (via MainViewModel) | — | Explorer right-click "Deep Uninstall" |
| 20 | **Diagnostics** | `DiagnosticLogger` | — | — | File + Debug logging |

---

## 14. Complete File Inventory

```
AuraClean/
├── AuraClean.csproj                    # Project config, NuGet refs, publish settings
├── App.xaml                            # Theme, colors, styles, converters (248 lines)
├── App.xaml.cs                         # Global exception handling, crash log
├── app.manifest                        # Admin elevation, DPI, OS compatibility
├── AssemblyInfo.cs                     # ThemeInfo attribute
├── Assets/
│   └── icon.ico                        # App icon
├── Converters/
│   └── FileSizeConverter.cs            # 5 converters (116 lines)
├── Helpers/
│   ├── DiagnosticLogger.cs             # File + Debug logging
│   └── FormatHelper.cs                 # FormatBytes, FormatDuration, FormatCount
├── Models/
│   ├── InstalledProgram.cs             # Uninstall registry data model
│   ├── JunkItem.cs                     # Junk file data model + JunkType enum (17 values)
│   └── ScanResult.cs                   # Scan results container
├── Services/
│   ├── BrowserCleanerService.cs        # Chromium browser cleaning (492 lines)
│   ├── CleanupHistoryService.cs        # Operation history logging (230 lines)
│   ├── ContextMenuService.cs           # Explorer context menu integration
│   ├── DiskAnalyzerService.cs          # Treemap analysis (321 lines)
│   ├── DuplicateFinderService.cs       # SHA-256 duplicate detection (368 lines)
│   ├── FileCleanerService.cs           # System junk scanner (465 lines)
│   ├── FileLockDetector.cs             # Restart Manager P/Invoke
│   ├── FileShredderService.cs          # Secure file overwrite
│   ├── ForceDeleteService.cs           # Locked file removal (402 lines)
│   ├── HeuristicScannerService.cs      # Abandoned file detection
│   ├── InstallMonitorService.cs        # Shadow install tracking (387 lines)
│   ├── LargeFileFinderService.cs       # Large file scanner
│   ├── MemoryManagerService.cs         # RAM optimization P/Invoke
│   ├── QuarantineService.cs            # File quarantine (340 lines)
│   ├── RegistryScannerService.cs       # Orphaned registry scanner
│   ├── RestorePointService.cs          # WMI system restore
│   ├── SettingsService.cs              # JSON settings persistence
│   ├── StartupManagerService.cs        # Startup program control (638 lines)
│   ├── SystemInfoService.cs            # WMI system info (417 lines)
│   └── UninstallerService.cs           # Program uninstall orchestration
├── ViewModels/
│   ├── MainViewModel.cs                # Root VM + 14 child VMs (~300 lines)
│   ├── BrowserCleanerViewModel.cs
│   ├── CleanerViewModel.cs
│   ├── CleanupHistoryViewModel.cs
│   ├── DiskAnalyzerViewModel.cs        # (364 lines)
│   ├── DuplicateFinderViewModel.cs
│   ├── FileShredderViewModel.cs
│   ├── InstallMonitorViewModel.cs
│   ├── LargeFileFinderViewModel.cs
│   ├── MemoryViewModel.cs
│   ├── QuarantineViewModel.cs
│   ├── SettingsViewModel.cs
│   ├── StartupManagerViewModel.cs
│   ├── SystemInfoViewModel.cs
│   └── UninstallerViewModel.cs
└── Views/
    ├── MainWindow.xaml                 # Shell: sidebar + content area (313 lines)
    ├── MainWindow.xaml.cs              # Navigation logic
    ├── DashboardView.xaml              # Health gauge + stats + quick actions
    ├── DashboardView.xaml.cs
    ├── BrowserCleanerView.xaml         # Privacy clean UI
    ├── BrowserCleanerView.xaml.cs
    ├── CleanerView.xaml                # Categorized junk list
    ├── CleanerView.xaml.cs
    ├── CleanupHistoryView.xaml         # Operation history log
    ├── CleanupHistoryView.xaml.cs
    ├── DuplicateFinderView.xaml        # Grouped duplicate list
    ├── DuplicateFinderView.xaml.cs
    ├── FileShredderView.xaml           # Algorithm selector + file list
    ├── FileShredderView.xaml.cs
    ├── InstallMonitorView.xaml         # Snapshot UI + delta results
    ├── InstallMonitorView.xaml.cs
    ├── LargeFileFinderView.xaml        # DataGrid + filters
    ├── LargeFileFinderView.xaml.cs
    ├── MemoryBoostView.xaml            # RAM gauge + boost button
    ├── MemoryBoostView.xaml.cs
    ├── QuarantineView.xaml             # Quarantine file list
    ├── QuarantineView.xaml.cs
    ├── SettingsView.xaml               # 5 settings sections
    ├── SettingsView.xaml.cs
    ├── StartupManagerView.xaml         # Startup entries grid
    ├── StartupManagerView.xaml.cs
    ├── StorageMapView.xaml             # Treemap + breadcrumbs
    ├── StorageMapView.xaml.cs          # PercentToWidthConverter
    ├── SystemInfoView.xaml             # Grouped info list
    ├── SystemInfoView.xaml.cs          # Category grouping logic
    ├── UninstallerView.xaml            # Program list + leftovers
    └── UninstallerView.xaml.cs

TestFeatures/
├── Program.cs                          # Console test runner (249 lines)
└── TestFeatures.csproj
```
