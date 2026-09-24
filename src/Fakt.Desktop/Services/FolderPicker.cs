using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Fakt.Desktop.Services;

/// <summary>Выбор папки через системный диалог IFileOpenDialog (FOS_PICKFOLDERS) — доступен в Windows Vista/7 и новее.</summary>
public static class FolderPicker
{
    private const uint FosPickFolders = 0x20;
    private const uint FosForceFileSystem = 0x40;
    private const uint FosNoChangeDir = 0x8;
    private const uint FosPathMustExist = 0x800;
    private const uint SigdnFileSysPath = 0x80058000;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    public static string Pick(Window owner, string title, string initialFolder)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosNoChangeDir | FosPathMustExist);
            dialog.SetTitle(title);
            if (!string.IsNullOrWhiteSpace(initialFolder) && System.IO.Directory.Exists(initialFolder))
            {
                try
                {
                    SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, typeof(IShellItem).GUID, out var folder);
                    dialog.SetFolder(folder);
                }
                catch (COMException)
                {
                }
            }

            var handle = owner == null ? IntPtr.Zero : new WindowInteropHelper(owner).Handle;
            var hr = dialog.Show(handle);
            if (hr == ErrorCancelled)
            {
                return null;
            }

            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out var item);
            item.GetDisplayName(SigdnFileSysPath, out var path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw
    {
    }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig]
        int Show(IntPtr parent);

        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
