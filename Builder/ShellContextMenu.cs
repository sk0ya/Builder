using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Builder;

internal static class ShellContextMenu
{
    #region COM Interfaces

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
        void GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    #endregion

    #region P/Invoke

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.IUnknown)] out object ppshf);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenu(IntPtr hMenu, uint uPosition, uint uFlags, UIntPtr uIDNewItem,
        string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    #endregion

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    private const uint CMF_NORMAL = 0x0000;
    private const uint CMF_EXPLORE = 0x0004;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_BYPOSITION = 0x0400;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_STRING = 0x0000;
    private const int CopyRepoPathCommandId = 0x8000;
    private const int SetGroupCommandId = 0x8001;

    // 右クリック押下時に準備したメニューを保持
    private static IntPtr _hMenu = IntPtr.Zero;
    private static IContextMenu? _contextMenu;
    private static IntPtr _ownerHwnd = IntPtr.Zero;
    private static Action? _copyRepoPathAction;
    private static Action? _setGroupAction;

    /// <summary>
    /// 右クリック押下時に呼ぶ。重い QueryContextMenu をここで済ませておく。
    /// </summary>
    public static void Prepare(string path, Window owner, Action? copyRepoPathAction = null, Action? setGroupAction = null)
    {
        Discard();
        try
        {
            var hwnd = new WindowInteropHelper(owner).Handle;
            _ownerHwnd = hwnd;
            _copyRepoPathAction = copyRepoPathAction;
            _setGroupAction = setGroupAction;

            SHGetDesktopFolder(out var desktopObj);
            var desktop = (IShellFolder)desktopObj;

            uint pchEaten = 0, sfgao = 0;
            desktop.ParseDisplayName(hwnd, IntPtr.Zero, path, ref pchEaten, out IntPtr pidl, ref sfgao);

            try
            {
                var iid = IID_IShellFolder;
                SHBindToParent(pidl, ref iid, out var parentObj, out IntPtr pidlChild);
                var parent = (IShellFolder)parentObj;

                IntPtr[] apidl = [pidlChild];
                var iidMenu = IID_IContextMenu;
                parent.GetUIObjectOf(hwnd, 1, apidl, ref iidMenu, IntPtr.Zero, out var menuObj);
                _contextMenu = (IContextMenu)menuObj;

                _hMenu = CreatePopupMenu();
                // QueryContextMenu がシェル拡張を読み込む重い処理 — Down 時に実行しておく
                _contextMenu.QueryContextMenu(_hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXPLORE);
                InsertMenu(_hMenu, 0, MF_BYPOSITION | MF_STRING, new UIntPtr(SetGroupCommandId), "グループを設定");
                InsertMenu(_hMenu, 1, MF_BYPOSITION | MF_STRING, new UIntPtr(CopyRepoPathCommandId),
                    "リポジトリのパスをコピー");
                InsertMenu(_hMenu, 2, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
            }
            finally
            {
                CoTaskMemFree(pidl);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShellContextMenu.Prepare: {ex.Message}");
            Discard();
        }
    }

    /// <summary>
    /// 右クリック離し時に呼ぶ。Prepare 済みのメニューを即座に表示する。
    /// </summary>
    public static void ShowPrepared(Point screenPoint)
    {
        if (_hMenu == IntPtr.Zero || _contextMenu == null) return;

        try
        {
            SetForegroundWindow(_ownerHwnd);
            int cmd = TrackPopupMenuEx(_hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                (int)screenPoint.X, (int)screenPoint.Y, _ownerHwnd, IntPtr.Zero);

            if (cmd > 0)
            {
                if (cmd == SetGroupCommandId)
                {
                    _setGroupAction?.Invoke();
                    return;
                }
                if (cmd == CopyRepoPathCommandId)
                {
                    _copyRepoPathAction?.Invoke();
                    return;
                }

                var ici = new CMINVOKECOMMANDINFO
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    hwnd = _ownerHwnd,
                    lpVerb = (IntPtr)(cmd - 1),
                    nShow = 1
                };
                _contextMenu.InvokeCommand(ref ici);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ShellContextMenu.ShowPrepared: {ex.Message}");
        }
        finally
        {
            Discard();
        }
    }

    /// <summary>
    /// 表示せずに破棄する（他の場所でマウスボタンが離された場合など）。
    /// </summary>
    public static void Discard()
    {
        if (_hMenu != IntPtr.Zero)
        {
            DestroyMenu(_hMenu);
            _hMenu = IntPtr.Zero;
        }
        _contextMenu = null;
        _ownerHwnd = IntPtr.Zero;
        _copyRepoPathAction = null;
        _setGroupAction = null;
    }
}
