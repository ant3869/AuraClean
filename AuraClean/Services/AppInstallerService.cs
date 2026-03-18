using AuraClean.Helpers;
using AuraClean.Models;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace AuraClean.Services;

/// <summary>
/// Downloads and silently installs free/open-source applications.
/// Each app is downloaded to a temp folder, installed with silent flags, then cleaned up.
/// </summary>
public static class AppInstallerService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private static readonly string DownloadDir = Path.Combine(
        Path.GetTempPath(), "AuraClean_AppInstaller");

    /// <summary>
    /// Returns the curated catalog of popular free/open-source applications.
    /// </summary>
    public static List<BundleApp> GetAppCatalog()
    {
        return
        [
            // ── Browsers ──
            new BundleApp
            {
                Name = "Mozilla Firefox",
                Description = "Fast, private open-source web browser",
                Category = "Browsers",
                IconKind = "Firefox",
                DownloadUrl = "https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=en-US",
                InstallerArgs = "/S",
                Website = "https://www.mozilla.org/firefox/",
                License = "MPL 2.0"
            },
            new BundleApp
            {
                Name = "Brave Browser",
                Description = "Privacy-focused Chromium browser with built-in ad blocking",
                Category = "Browsers",
                IconKind = "Shield",
                DownloadUrl = "https://laptop-updates.brave.com/latest/winx64",
                InstallerArgs = "--silent --system-level",
                Website = "https://brave.com/",
                License = "MPL 2.0"
            },

            // ── Media ──
            new BundleApp
            {
                Name = "VLC Media Player",
                Description = "Universal media player — plays virtually any audio/video format",
                Category = "Media",
                IconKind = "PlayCircle",
                DownloadUrl = "https://get.videolan.org/vlc/3.0.21/win64/vlc-3.0.21-win64.exe",
                InstallerArgs = "/S /L=1033",
                Website = "https://www.videolan.org/vlc/",
                License = "GPL 2.0"
            },
            new BundleApp
            {
                Name = "Audacity",
                Description = "Free multi-track audio editor and recorder",
                Category = "Media",
                IconKind = "Microphone",
                DownloadUrl = "https://github.com/audacity/audacity/releases/download/Audacity-3.7.3/audacity-win-3.7.3-64bit.exe",
                InstallerArgs = "/VERYSILENT /NORESTART",
                Website = "https://www.audacityteam.org/",
                License = "GPL 3.0"
            },
            new BundleApp
            {
                Name = "HandBrake",
                Description = "Open-source video transcoder for converting video formats",
                Category = "Media",
                IconKind = "MovieEdit",
                DownloadUrl = "https://github.com/HandBrake/HandBrake/releases/download/1.9.2/HandBrake-1.9.2-x86_64-Win_GUI.exe",
                InstallerArgs = "/VERYSILENT /NORESTART",
                Website = "https://handbrake.fr/",
                License = "GPL 2.0"
            },

            // ── Office & Productivity ──
            new BundleApp
            {
                Name = "LibreOffice",
                Description = "Full office suite — word processor, spreadsheet, presentations & more",
                Category = "Office & Productivity",
                IconKind = "FileDocument",
                DownloadUrl = "https://download.documentfoundation.org/libreoffice/stable/25.2.2/win/x86_64/LibreOffice_25.2.2_Win_x86-64.msi",
                InstallerArgs = "/qn /norestart",
                Website = "https://www.libreoffice.org/",
                License = "MPL 2.0"
            },
            new BundleApp
            {
                Name = "Notepad++",
                Description = "Powerful source code editor with syntax highlighting",
                Category = "Office & Productivity",
                IconKind = "NoteEdit",
                DownloadUrl = "https://github.com/notepad-plus-plus/notepad-plus-plus/releases/download/v8.7.7/npp.8.7.7.Installer.x64.exe",
                InstallerArgs = "/S",
                Website = "https://notepad-plus-plus.org/",
                License = "GPL 3.0"
            },
            new BundleApp
            {
                Name = "SumatraPDF",
                Description = "Lightweight PDF, eBook, and comic reader",
                Category = "Office & Productivity",
                IconKind = "FilePdfBox",
                DownloadUrl = "https://www.sumatrapdfreader.org/dl/rel/3.5.2/SumatraPDF-3.5.2-64-install.exe",
                InstallerArgs = "-s",
                Website = "https://www.sumatrapdfreader.org/",
                License = "GPL 3.0"
            },

            // ── Development ──
            new BundleApp
            {
                Name = "Visual Studio Code",
                Description = "Lightweight but powerful source code editor by Microsoft",
                Category = "Development",
                IconKind = "MicrosoftVisualStudioCode",
                DownloadUrl = "https://code.visualstudio.com/sha/download?build=stable&os=win32-x64",
                InstallerArgs = "/VERYSILENT /NORESTART /MERGETASKS=!runcode,addcontextmenufiles,addcontextmenufolders,addtopath",
                Website = "https://code.visualstudio.com/",
                License = "MIT"
            },
            new BundleApp
            {
                Name = "Git",
                Description = "Distributed version control system — essential for developers",
                Category = "Development",
                IconKind = "Git",
                DownloadUrl = "https://github.com/git-for-windows/git/releases/download/v2.48.1.windows.1/Git-2.48.1-64-bit.exe",
                InstallerArgs = "/VERYSILENT /NORESTART",
                Website = "https://git-scm.com/",
                License = "GPL 2.0"
            },
            new BundleApp
            {
                Name = "Python 3",
                Description = "Popular general-purpose programming language",
                Category = "Development",
                IconKind = "LanguagePython",
                DownloadUrl = "https://www.python.org/ftp/python/3.13.2/python-3.13.2-amd64.exe",
                InstallerArgs = "/quiet InstallAllUsers=1 PrependPath=1",
                Website = "https://www.python.org/",
                License = "PSF"
            },
            new BundleApp
            {
                Name = "Node.js LTS",
                Description = "JavaScript runtime built on Chrome's V8 engine",
                Category = "Development",
                IconKind = "Nodejs",
                DownloadUrl = "https://nodejs.org/dist/v22.14.0/node-v22.14.0-x64.msi",
                InstallerArgs = "/qn /norestart",
                Website = "https://nodejs.org/",
                License = "MIT"
            },

            // ── Graphics & Design ──
            new BundleApp
            {
                Name = "GIMP",
                Description = "Professional open-source image editor — Photoshop alternative",
                Category = "Graphics & Design",
                IconKind = "Palette",
                DownloadUrl = "https://download.gimp.org/gimp/v2.10/windows/gimp-2.10.38-setup.exe",
                InstallerArgs = "/VERYSILENT /NORESTART",
                Website = "https://www.gimp.org/",
                License = "GPL 3.0"
            },
            new BundleApp
            {
                Name = "Inkscape",
                Description = "Vector graphics editor — Illustrator alternative",
                Category = "Graphics & Design",
                IconKind = "Drawing",
                DownloadUrl = "https://inkscape.org/gallery/item/46809/inkscape-1.4.1_2025-03-16_1b38f61-x64.exe",
                InstallerArgs = "/S",
                Website = "https://inkscape.org/",
                License = "GPL 2.0"
            },
            new BundleApp
            {
                Name = "ShareX",
                Description = "Advanced screenshot and screen recording tool",
                Category = "Graphics & Design",
                IconKind = "Monitor",
                DownloadUrl = "https://github.com/ShareX/ShareX/releases/download/v17.1.0/ShareX-17.1.0-setup.exe",
                InstallerArgs = "/VERYSILENT /NORESTART",
                Website = "https://getsharex.com/",
                License = "GPL 3.0"
            },

            // ── File Management ──
            new BundleApp
            {
                Name = "7-Zip",
                Description = "High-compression file archiver — zip, 7z, tar, and more",
                Category = "File Management",
                IconKind = "FolderZip",
                DownloadUrl = "https://www.7-zip.org/a/7z2409-x64.exe",
                InstallerArgs = "/S",
                Website = "https://www.7-zip.org/",
                License = "LGPL / BSD"
            },
            new BundleApp
            {
                Name = "Everything",
                Description = "Instant file search engine for Windows — lightning fast",
                Category = "File Management",
                IconKind = "FileSearch",
                DownloadUrl = "https://www.voidtools.com/Everything-1.4.1.1026.x64-Setup.exe",
                InstallerArgs = "/S /D",
                Website = "https://www.voidtools.com/",
                License = "MIT"
            },
            new BundleApp
            {
                Name = "WinSCP",
                Description = "SFTP, FTP, and SCP client for secure file transfers",
                Category = "File Management",
                IconKind = "FileUpload",
                DownloadUrl = "https://winscp.net/download/WinSCP-6.5.1-Setup.exe",
                InstallerArgs = "/VERYSILENT /NORESTART",
                Website = "https://winscp.net/",
                License = "GPL 3.0"
            },

            // ── Communication ──
            new BundleApp
            {
                Name = "Thunderbird",
                Description = "Free email client by Mozilla with calendar & chat",
                Category = "Communication",
                IconKind = "Email",
                DownloadUrl = "https://download.mozilla.org/?product=thunderbird-latest-ssl&os=win64&lang=en-US",
                InstallerArgs = "/S",
                Website = "https://www.thunderbird.net/",
                License = "MPL 2.0"
            },
            new BundleApp
            {
                Name = "Signal Desktop",
                Description = "Private messenger with end-to-end encryption",
                Category = "Communication",
                IconKind = "MessageLock",
                DownloadUrl = "https://updates.signal.org/desktop/signal-desktop-win-7.46.0.exe",
                InstallerArgs = "--silent",
                Website = "https://signal.org/",
                License = "AGPL 3.0"
            },

            // ── System Utilities ──
            new BundleApp
            {
                Name = "CPU-Z",
                Description = "Detailed CPU, motherboard, and memory information",
                Category = "System Utilities",
                IconKind = "Chip",
                DownloadUrl = "https://download.cpuid.com/cpu-z/cpu-z_2.13-en.exe",
                InstallerArgs = "/VERYSILENT /NORESTART",
                Website = "https://www.cpuid.com/softwares/cpu-z.html",
                License = "Freeware"
            },
            new BundleApp
            {
                Name = "HWiNFO",
                Description = "Comprehensive hardware analysis, monitoring, and reporting",
                Category = "System Utilities",
                IconKind = "Thermometer",
                DownloadUrl = "https://www.sac.sk/download/utildiag/hwi_832.exe",
                InstallerArgs = "/VERYSILENT /NORESTART",
                Website = "https://www.hwinfo.com/",
                License = "Freeware"
            },
            new BundleApp
            {
                Name = "Rainmeter",
                Description = "Desktop customization with widgets, skins, and system monitors",
                Category = "System Utilities",
                IconKind = "Widgets",
                DownloadUrl = "https://github.com/rainmeter/rainmeter/releases/download/v4.5.21.3762/Rainmeter-4.5.21.exe",
                InstallerArgs = "/S",
                Website = "https://www.rainmeter.net/",
                License = "GPL 2.0"
            },
            new BundleApp
            {
                Name = "WinDirStat",
                Description = "Disk usage visualizer — see what's consuming your drive space",
                Category = "System Utilities",
                IconKind = "ChartTreemap",
                DownloadUrl = "https://windirstat.net/wds_current_setup.exe",
                InstallerArgs = "/S",
                Website = "https://windirstat.net/",
                License = "GPL 2.0"
            },

            // ── Gaming ──
            new BundleApp
            {
                Name = "Steam",
                Description = "World's largest PC gaming platform and store",
                Category = "Gaming",
                IconKind = "Steam",
                DownloadUrl = "https://cdn.cloudflare.steamstatic.com/client/installer/SteamSetup.exe",
                InstallerArgs = "/S",
                Website = "https://store.steampowered.com/",
                License = "Freeware"
            },
            new BundleApp
            {
                Name = "Epic Games Launcher",
                Description = "Game store with free weekly games and Unreal Engine access",
                Category = "Gaming",
                IconKind = "Gamepad",
                DownloadUrl = "https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi",
                InstallerArgs = "/qn /norestart",
                Website = "https://www.epicgames.com/store/",
                License = "Freeware"
            },

            // ── Networking ──
            new BundleApp
            {
                Name = "PuTTY",
                Description = "SSH, Telnet, and serial console client",
                Category = "Networking",
                IconKind = "Console",
                DownloadUrl = "https://the.earth.li/~sgtatham/putty/latest/w64/putty-64bit-0.82-installer.msi",
                InstallerArgs = "/qn /norestart",
                Website = "https://www.putty.org/",
                License = "MIT"
            },
            new BundleApp
            {
                Name = "qBittorrent",
                Description = "Open-source BitTorrent client — clean, no ads",
                Category = "Networking",
                IconKind = "Download",
                DownloadUrl = "https://downloads.sourceforge.net/project/qbittorrent/qbittorrent-win32/qbittorrent-5.0.4/qbittorrent_5.0.4_x64_setup.exe",
                InstallerArgs = "/S",
                Website = "https://www.qbittorrent.org/",
                License = "GPL 2.0"
            },

            // ── Security & Privacy ──
            new BundleApp
            {
                Name = "KeePassXC",
                Description = "Cross-platform password manager — offline, encrypted vault",
                Category = "Security & Privacy",
                IconKind = "KeyChainVariant",
                DownloadUrl = "https://github.com/keepassxreboot/keepassxc/releases/download/2.7.9/KeePassXC-2.7.9-Win64.msi",
                InstallerArgs = "/qn /norestart",
                Website = "https://keepassxc.org/",
                License = "GPL 3.0"
            },
            new BundleApp
            {
                Name = "VeraCrypt",
                Description = "Disk encryption software — protects your data with strong encryption",
                Category = "Security & Privacy",
                IconKind = "LockCheck",
                DownloadUrl = "https://launchpad.net/veracrypt/trunk/1.26.20/+download/VeraCrypt_Setup_x64_1.26.20.exe",
                InstallerArgs = "/S",
                Website = "https://www.veracrypt.fr/",
                License = "Apache 2.0"
            },

            // ── Command Line Tools ──
            new BundleApp
            {
                Name = "FFmpeg",
                Description = "Universal command-line audio/video processing toolkit",
                Category = "Command Line Tools",
                IconKind = "MovieFilter",
                DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip",
                InstallerArgs = "", // Portable — extract only
                Website = "https://ffmpeg.org/",
                License = "LGPL 2.1 / GPL",
                IsPortable = true
            },
            new BundleApp
            {
                Name = "yt-dlp",
                Description = "Download videos from YouTube and 1000+ other sites",
                Category = "Command Line Tools",
                IconKind = "VideoDownload",
                DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe",
                InstallerArgs = "", // Portable single exe
                Website = "https://github.com/yt-dlp/yt-dlp",
                License = "Unlicense",
                IsPortable = true
            },
            new BundleApp
            {
                Name = "Windows Terminal",
                Description = "Modern, tabbed terminal from Microsoft with GPU rendering",
                Category = "Command Line Tools",
                IconKind = "Console",
                DownloadUrl = "https://github.com/microsoft/terminal/releases/download/v1.22.3232.0/Microsoft.WindowsTerminal_1.22.3232.0_x64.zip",
                InstallerArgs = "", // Portable or MSIX
                Website = "https://github.com/microsoft/terminal",
                License = "MIT",
                IsPortable = true
            },
        ];
    }

    /// <summary>
    /// Returns the distinct categories from the catalog, sorted.
    /// </summary>
    public static List<string> GetCategories()
    {
        return GetAppCatalog()
            .Select(a => a.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
    }

    /// <summary>
    /// Downloads and installs a single application, reporting progress.
    /// </summary>
    public static async Task InstallAppAsync(
        BundleApp app,
        IProgress<(int percent, string message)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(DownloadDir);

        var fileName = SanitizeFileName(app.Name) + GetExtension(app.DownloadUrl);
        var filePath = Path.Combine(DownloadDir, fileName);

        try
        {
            // Download
            progress?.Report((5, $"Downloading {app.Name}..."));
            await DownloadFileAsync(app.DownloadUrl, filePath, progress, ct);

            if (app.IsPortable)
            {
                await HandlePortableAppAsync(app, filePath, progress, ct);
            }
            else
            {
                // Install
                progress?.Report((80, $"Installing {app.Name}..."));
                await RunInstallerAsync(filePath, app.InstallerArgs, ct);
            }

            progress?.Report((100, $"{app.Name} installed successfully!"));
        }
        finally
        {
            // Cleanup downloaded file
            try { if (File.Exists(filePath)) File.Delete(filePath); }
            catch { /* ignore cleanup errors */ }
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<(int percent, string message)>? progress,
        CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[65536];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (totalBytes > 0)
            {
                int pct = (int)(totalRead * 70 / totalBytes) + 5; // 5-75% range
                progress?.Report((Math.Min(pct, 75),
                    $"Downloading... {totalRead / 1_048_576.0:F1} MB" +
                    (totalBytes > 0 ? $" / {totalBytes / 1_048_576.0:F1} MB" : "")));
            }
        }

        progress?.Report((75, "Download complete."));
    }

    private static async Task RunInstallerAsync(
        string installerPath,
        string arguments,
        CancellationToken ct)
    {
        var ext = Path.GetExtension(installerPath).ToLowerInvariant();
        string fileName;
        string args;

        if (ext == ".msi")
        {
            fileName = "msiexec.exe";
            args = $"/i \"{installerPath}\" {arguments}";
        }
        else
        {
            fileName = installerPath;
            args = arguments;
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            UseShellExecute = true,
            Verb = "runas", // Elevate if needed
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using var process = Process.Start(psi);
        if (process == null) throw new InvalidOperationException("Failed to start installer process.");

        // Wait up to 10 minutes for install to complete
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(10), ct);
    }

    private static async Task HandlePortableAppAsync(
        BundleApp app,
        string downloadedFile,
        IProgress<(int percent, string message)>? progress,
        CancellationToken ct)
    {
        var portableDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AuraClean", "PortableApps", SanitizeFileName(app.Name));

        Directory.CreateDirectory(portableDir);

        progress?.Report((80, $"Extracting {app.Name}..."));

        var ext = Path.GetExtension(downloadedFile).ToLowerInvariant();

        if (ext == ".zip")
        {
            await Task.Run(() =>
                System.IO.Compression.ZipFile.ExtractToDirectory(downloadedFile, portableDir, overwriteFiles: true), ct);
        }
        else if (ext == ".exe")
        {
            // Single portable exe — just copy it
            var destFile = Path.Combine(portableDir, Path.GetFileName(downloadedFile));
            File.Copy(downloadedFile, destFile, overwrite: true);
        }

        progress?.Report((95, $"Extracted to {portableDir}"));
    }

    /// <summary>
    /// Cleans up the temp download directory.
    /// </summary>
    public static void CleanupDownloads()
    {
        try
        {
            if (Directory.Exists(DownloadDir))
                Directory.Delete(DownloadDir, recursive: true);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("AppInstaller", "Cleanup failed", ex);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray()).Replace(' ', '_');
    }

    private static string GetExtension(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".exe" or ".msi" or ".zip")
                return ext;
        }
        catch { /* ignore malformed URLs */ }
        return ".exe"; // Default assumption
    }
}
