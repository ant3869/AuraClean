<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-purple?style=for-the-badge&logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/WPF-Desktop-blue?style=for-the-badge&logo=windows" alt="WPF" />
  <img src="https://img.shields.io/badge/MaterialDesign-5.1-00BCD4?style=for-the-badge" alt="MaterialDesign" />
  <img src="https://img.shields.io/badge/License-MIT-green?style=for-the-badge" alt="MIT License" />
</p>

<h1 align="center">AuraClean</h1>
<p align="center"><strong>A modern Windows system cleaner &amp; optimizer built with WPF and .NET 8</strong></p>
<p align="center">
  Deep uninstaller · System hygiene engine · Privacy cleaner · RAM booster · Storage analyzer<br/>
  All wrapped in a dark glass-morphism UI called <em>Obsidian Aurora</em>
</p>

---

## Screenshots

> AuraClean uses a custom **Obsidian Aurora v2** design system — a dark theme with violet/cyan accents, glass-morphism cards, and gradient highlights. Every page follows a consistent layout with stat cards, action toolbars, and scrollable content areas.

---

## Features (15 Pages)

| Page | Description |
|------|-------------|
| **Dashboard** | System health score (0–100), quick-action buttons, system info strip (OS, CPU, RAM, uptime) |
| **Uninstaller** | Deep uninstall with orphaned registry + remnant file scanning, force uninstall for broken MSI, dry-run mode |
| **System Cleaner** | Scans 14 junk categories (temp files, Windows Update cache, prefetch, crash dumps, Recycle Bin, etc.) |
| **RAM Booster** | Working-set trimming via `EmptyWorkingSet` + standby list purge via `NtSetSystemInformation` |
| **Privacy Clean** | Chromium browser cache/cookies/tracking cleanup with SQLite VACUUM, DNS flush |
| **Storage Map** | Visual treemap disk analyzer with breadcrumb navigation and top-10 largest files |
| **Install Monitor** | Before/after snapshot diffing — captures registry keys, files, and directories created by installers |
| **Startup Manager** | Registry Run keys + shell:startup + Task Scheduler entries with enable/disable/delete, impact ratings |
| **Duplicate Finder** | 3-pass detection: file size → partial 4KB hash → full SHA-256, configurable size filters |
| **Large File Finder** | Scans drives for files above a threshold (50MB–1GB), categorized by type (Video, Archive, etc.) |
| **File Shredder** | Secure multi-pass deletion: Quick Zero, Random, DoD 5220.22-M (3-pass), Enhanced (7-pass) |
| **System Info** | WMI-based hardware inventory: OS, CPU, Memory, GPU, Storage, Network, Motherboard, Runtime |
| **Quarantine** | Move suspicious files to quarantine with restore capability, auto-purge expired entries |
| **Cleanup History** | Persistent log of all past operations with summary stats, filtering, and text export |
| **Settings** | Centralized preferences: restore points, dry-run, scan defaults, shred algorithm, retention policies |

---

## Architecture

```
AuraClean/
├── Models/                 # ObservableObject data models
│   ├── InstalledProgram.cs
│   ├── JunkItem.cs         # 17-value JunkType enum
│   └── ScanResult.cs
├── Services/               # All static, async, with IProgress<string> + CancellationToken
│   ├── BrowserCleanerService.cs
│   ├── CleanupHistoryService.cs
│   ├── ContextMenuService.cs
│   ├── DiskAnalyzerService.cs
│   ├── DuplicateFinderService.cs
│   ├── FileCleanerService.cs
│   ├── FileLockDetector.cs
│   ├── FileShredderService.cs
│   ├── ForceDeleteService.cs
│   ├── HeuristicScannerService.cs
│   ├── InstallMonitorService.cs
│   ├── LargeFileFinderService.cs
│   ├── MemoryManagerService.cs
│   ├── QuarantineService.cs
│   ├── RegistryScannerService.cs
│   ├── RestorePointService.cs
│   ├── SettingsService.cs
│   ├── StartupManagerService.cs
│   ├── SystemInfoService.cs
│   └── UninstallerService.cs
├── ViewModels/             # CommunityToolkit.Mvvm ObservableObject + [RelayCommand]
│   ├── MainViewModel.cs    # Root VM: navigation, health score, system info
│   └── ...                 # 14 feature ViewModels
├── Views/                  # WPF UserControls + MainWindow
│   ├── MainWindow.xaml     # Sidebar nav + content switching
│   └── ...                 # 15 feature views
├── Converters/             # FileSizeConverter, BoolToVisibility, HealthScoreColor, etc.
├── Helpers/                # DiagnosticLogger, FormatHelper
└── Assets/                 # icon.ico
```

### Design Patterns

| Pattern | Implementation |
|---------|---------------|
| **MVVM** | CommunityToolkit.Mvvm source generators (`[ObservableProperty]`, `[RelayCommand]`) |
| **Static Services** | All 20 services are `static` classes — no DI container |
| **Navigation** | String-based view switching via `MainViewModel.CurrentViewName` → code-behind `Dictionary<string, FrameworkElement>` |
| **Progress Reporting** | Every long operation accepts `IProgress<string>` for live status updates |
| **Cancellation** | All async operations support `CancellationToken` |
| **Diagnostic Logging** | `DiagnosticLogger` writes daily logs to `%LocalAppData%\AuraClean\Logs\` |

### Native Interop (P/Invoke)

| DLL | Function | Used By |
|-----|----------|---------|
| `kernel32.dll` | `MoveFileEx` | ForceDeleteService — boot-time delete for locked files |
| `psapi.dll` | `EmptyWorkingSet` | MemoryManagerService — trim process working sets |
| `ntdll.dll` | `NtSetSystemInformation` | MemoryManagerService — purge standby memory list |
| `rstrtmgr.dll` | `RmStartSession`, `RmRegisterResources`, `RmGetList` | FileLockDetector — identify processes locking files |

---

## Tech Stack

| Component | Version | Purpose |
|-----------|---------|---------|
| [.NET 8.0](https://dotnet.microsoft.com/) | `net8.0-windows` | Runtime & SDK |
| [WPF](https://github.com/dotnet/wpf) | Built-in | UI framework |
| [MaterialDesignThemes](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | 5.1.0 | UI components & icons |
| [MaterialDesignColors](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) | 3.1.0 | Color palette resources |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.3.2 | MVVM infrastructure |
| [System.Management](https://www.nuget.org/packages/System.Management) | 8.0.0 | WMI queries |
| [System.Data.SQLite.Core](https://www.nuget.org/packages/System.Data.SQLite.Core) | 1.0.119 | Browser database VACUUM |
| [System.ServiceProcess.ServiceController](https://www.nuget.org/packages/System.ServiceProcess.ServiceController) | 8.0.1 | Stop/start Windows services |

---

## Design System — Obsidian Aurora v2

All colors and styles are defined in `App.xaml`:

| Token | Hex | Usage |
|-------|-----|-------|
| `AuraBgColor` | `#080810` | Main background |
| `AuraSurfaceColor` | `#0F0F1E` | Card/panel surfaces |
| `AuraSurfaceLightColor` | `#171730` | Elevated surfaces |
| `AuraVioletColor` | `#7C5CFC` | Primary accent |
| `AuraCyanColor` | `#00E5C3` | Secondary accent |
| `AuraCoralColor` | `#FF6B8A` | Warning/destructive |
| `AuraMintColor` | `#5BF0D7` | Success |
| `AuraAmberColor` | `#FFB74D` | Caution |
| `AuraTextBrightColor` | `#F0F0FF` | Bright text |
| `AuraTextColor` | `#C8C8E0` | Primary text |
| `AuraTextDimColor` | `#7B7BA0` | Secondary text |
| `AuraBorderColor` | `#222244` | Borders |

**Custom Styles:** `AuraNavButton` (sidebar RadioButton), `AuraPrimaryButton` (gradient violet), `AuraOutlinedButton` (teal outline), `AuraGhostButton` (subtle border), `AuraGlassCard` (glass-morphism Border)

---

## Data Persistence

All user data lives in `%LocalAppData%\AuraClean\`:

| Path | Format | Contents |
|------|--------|----------|
| `Settings/settings.json` | JSON | All application preferences |
| `History/cleanup_history.json` | JSON | Cleanup operation log |
| `Quarantine/manifest.json` | JSON | Quarantine entry metadata |
| `Quarantine/*.dat` | Binary | Quarantined file copies |
| `Snapshots/*.json` | JSON | Install monitor before/after snapshots |
| `Logs/auraclean_YYYY-MM-DD.log` | Text | Daily diagnostic logs |
| `last_cleaned.txt` | Text | Last cleanup timestamp |

---

## Getting Started

### Prerequisites

- **Windows 10/11** (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Administrator privileges** (required for registry scanning, prefetch cleanup, restore points, memory management)

### Build & Run

```powershell
# Clone
git clone https://github.com/ant3869/AuraClean.git
cd AuraClean

# Build
dotnet build -c Debug AuraClean/AuraClean.csproj

# Run (as Administrator)
.\AuraClean\bin\Debug\net8.0-windows\win-x64\AuraClean.exe
```

### Publish (single-file)

```powershell
dotnet publish -c Release AuraClean/AuraClean.csproj
# Output: AuraClean/bin/Release/net8.0-windows/win-x64/publish/AuraClean.exe
```

The published binary is a **self-contained single-file executable** (~60MB) — no .NET runtime installation required on the target machine.

### Run Tests

```powershell
dotnet run --project TestFeatures/TestFeatures.csproj
```

Tests cover `SystemInfoService`, `LargeFileFinderService`, and `FileShredderService` with pass/fail assertions.

---

## Junk Categories (System Cleaner)

The System Cleaner scans 14 categories via `FileCleanerService`:

| Category | Path(s) | Notes |
|----------|---------|-------|
| Temp Files | `%TEMP%`, `C:\Windows\Temp` | User + system temp |
| Windows Update | `C:\Windows\SoftwareDistribution\Download` | Stops `wuauserv` service first |
| Prefetch | `C:\Windows\Prefetch` | `.pf` files |
| Crash Dumps | `C:\Windows\Minidump`, `%LocalAppData%\CrashDumps` | Memory dump files |
| BranchCache | `%SystemRoot%\BranchCache` | Peer distribution cache |
| Thumbnail Cache | `%LocalAppData%\Microsoft\Windows\Explorer` | `thumbcache_*.db` |
| Recycle Bin | `$Recycle.Bin` on all drives | Per-drive scan |
| Delivery Optimization | `C:\Windows\SoftwareDistribution\DeliveryOptimization` | Windows Update P2P |
| Windows Error Reports | `%ProgramData%\Microsoft\Windows\WER`, `%LocalAppData%\...\WER` | Crash telemetry |
| Font Cache | `%WinDir%\ServiceProfiles\LocalService\AppData\Local\FontCache` | Stops `FontCache` service |
| Windows Logs | `C:\Windows\Logs` | CBS, DISM, setup logs |
| Windows.old | `C:\Windows.old` | Previous Windows installation |
| Abandoned Files | AppData/ProgramData heuristic scan | Dirs with no matching registry entry + 180+ days stale |

---

## Security Notes

- **Runs as Administrator** — required by design for system-level cleanup operations
- **Restore points** — optional system restore point creation before destructive operations
- **Dry-run mode** — scan-only mode that reports what would be deleted without touching files
- **File shredding** — multi-pass overwrite algorithms (DoD 5220.22-M standard) before deletion
- **Quarantine** — deleted files can be quarantined instead of permanently removed, with timed auto-purge
- **File lock detection** — uses Windows Restart Manager API to identify processes holding file locks
- **Boot-time delete** — `MoveFileEx` with `MOVEFILE_DELAY_UNTIL_REBOOT` for stubborn locked files

---

## License

This project is provided as-is for educational and personal use.
