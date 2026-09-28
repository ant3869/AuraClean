using System.IO;
using System.Runtime.InteropServices;

namespace AuraClean.Helpers;

/// <summary>
/// Moves files to the Windows Recycle Bin (recoverable deletion) through the shell.
/// No progress or confirmation UI is shown; if a file is too large for the Recycle Bin,
/// Windows asks before deleting it permanently rather than doing so silently.
/// </summary>
public static class RecycleBinHelper
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOpStruct fileOp);

    /// <summary>
    /// Sends one file to the Recycle Bin. Returns false with a reason when the file is missing,
    /// the user declined a permanent-delete prompt, or the shell reported an error.
    /// </summary>
    public static bool TrySendToRecycleBin(string path, out string error)
    {
        error = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                error = "File no longer exists.";
                return false;
            }

            var op = new ShFileOpStruct
            {
                Func = FoDelete,
                // pFrom is a list of paths terminated by an extra null character.
                From = fullPath + '\0',
                Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
            };

            var result = SHFileOperation(ref op);
            if (op.AnyOperationsAborted)
            {
                error = "Cancelled.";
                return false;
            }

            if (result != 0)
            {
                error = $"Windows could not move the file to the Recycle Bin (code 0x{result:X}).";
                return false;
            }

            if (File.Exists(fullPath))
            {
                error = "The file is still present after the Recycle Bin move.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            error = ex.Message;
            return false;
        }
    }
}
