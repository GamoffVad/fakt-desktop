using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Fakt.Infrastructure.Scanning;

internal static class NativeMethods
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorFilenameExcedRange = 206;

    public const uint FileAttributeDirectory = 0x10;
    public const uint FileAttributeHidden = 0x2;
    public const uint FileAttributeSystem = 0x4;
    public const uint FileAttributeReadOnly = 0x1;
    public const uint FileAttributeReparsePoint = 0x400;
    public const uint FileAttributeOffline = 0x1000;

    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const int FindFirstExLargeFetch = 2;

    public const uint FileReadAttributes = 0x80;
    public const uint FileShareAll = 0x7;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstFileExW")]
    public static extern SafeFindHandle FindFirstFileEx(string lpFileName, int fInfoLevelId, out Win32FindData lpFindFileData,
        int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFile(SafeFindHandle hFindFile, out Win32FindData lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    public static DateTime ToDateTimeUtc(System.Runtime.InteropServices.ComTypes.FILETIME time)
    {
        var ticks = ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;
        return ticks <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(ticks);
    }
}

internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeFindHandle() : base(true)
    {
    }

    protected override bool ReleaseHandle() => NativeMethods.FindClose(handle);
}
