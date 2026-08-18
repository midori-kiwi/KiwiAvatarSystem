using System;
using System.Runtime.InteropServices;
using System.Text;

public static class KiwiWindowsFilePicker
{
    public static bool IsSupported
    {
        get
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            return true;
#else
            return false;
#endif
        }
    }

    public static bool TryPickVrm(out string fullPath)
    {
        fullPath = string.Empty;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        try
        {
            OpenFileName dialog = new OpenFileName();
            dialog.lStructSize = Marshal.SizeOf(typeof(OpenFileName));
            dialog.lpstrFilter = "VRM Files (*.vrm)\0*.vrm\0All Files (*.*)\0*.*\0\0";
            dialog.lpstrFile = new StringBuilder(4096);
            dialog.nMaxFile = dialog.lpstrFile.Capacity;
            dialog.lpstrTitle = "Import VRM";
            dialog.lpstrDefExt = "vrm";
            dialog.Flags =
                OFN_PATHMUSTEXIST |
                OFN_FILEMUSTEXIST |
                OFN_NOCHANGEDIR |
                OFN_EXPLORER;

            if (!GetOpenFileName(ref dialog))
            {
                return false;
            }

            fullPath = dialog.lpstrFile.ToString();
            return !string.IsNullOrWhiteSpace(fullPath);
        }
        catch
        {
            fullPath = string.Empty;
            return false;
        }
#else
        return false;
#endif
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public StringBuilder lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);
#endif
}
