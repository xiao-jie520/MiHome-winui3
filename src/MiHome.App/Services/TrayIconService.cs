using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MiHome.App.Services;

/// <summary>
/// 系统托盘（Win32 Shell_NotifyIcon 实现，无第三方依赖）：
/// 左键唤出主界面，右键快捷菜单（设备开关 / 场景执行 / 退出）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WmTrayCallback = 0x8000 + 0x0F01; // WM_APP + 0x0F01
    private const uint WmCommand = 0x0111;               // WM_COMMAND

    private readonly MainWindow _owner;
    private readonly TrayWindow _trayWindow;
    private bool _disposed;

    public TrayIconService(MainWindow owner)
    {
        _owner = owner;
        _trayWindow = new TrayWindow(WmTrayCallback, OnTrayMessage);
        _trayWindow.AddIcon("米家控制台", LoadIconHandle());
        RebuildMenu();
    }

    private static nint LoadIconHandle()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path))
            {
                var h = TrayWindow.LoadImageNative(path);
                if (h != nint.Zero)
                {
                    return h;
                }
            }
        }
        catch { /* ignore */ }
        return TrayWindow.LoadDefaultIcon();
    }

    /// <summary>按当前设备/场景重建托盘菜单（登录成功与刷新后调用）。</summary>
    public async void RebuildMenu()
    {
        var items = new List<(int Id, string Text, Func<Task> Action)>();
        int nextId = 1000;

        items.Add((nextId++, "打开主界面", async () =>
        {
            _owner.ActivateFromTray();
            await Task.CompletedTask;
        }));

        try
        {
            var devices = await App.Service.ListDevicesAsync();
            var controllable = devices
                .Where(d => d.Online && !string.IsNullOrEmpty(d.SpecType))
                .Take(10)
                .ToList();
            if (controllable.Count > 0)
            {
                items.Add((-1, "─ 设备开关 ─", () => Task.CompletedTask));
                foreach (var device in controllable)
                {
                    var d = device;
                    items.Add((nextId++, $"{d.Name}", async () =>
                    {
                        try
                        {
                            var state = await App.Service.PowerStateAsync(d.Did);
                            if (state == null)
                            {
                                _owner.ShowToast($"「{d.Name}」不支持开关控制", InfoBarSeverity.Warning);
                                return;
                            }
                            await App.Service.SetPowerStateAsync(d.Did, !state.Value);
                            _owner.ShowToast($"「{d.Name}」已{(!state.Value ? "打开" : "关闭")}", InfoBarSeverity.Success);
                        }
                        catch (Exception exc)
                        {
                            _owner.ShowToast(exc.Message, InfoBarSeverity.Error);
                        }
                    }));
                }
            }
        }
        catch
        {
            // 未登录或网络失败：只保留基础菜单
        }

        items.Add((-1, "─", () => Task.CompletedTask));
        items.Add((nextId++, "退出", async () =>
        {
            Dispose();
            await Task.CompletedTask;
            Application.Current.Exit();
        }));

        _trayWindow.SetMenuItems(items);
    }

    /// <summary>托盘菜单回调（在托盘消息线程触发，需切回 UI 线程执行动作）。</summary>
    private void OnTrayMessage(uint messageId)
    {
        var item = _trayWindow.FindAction(messageId);
        if (item == null)
        {
            return;
        }
        _owner.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await item();
            }
            catch (Exception exc)
            {
                _owner.ShowToast(exc.Message, InfoBarSeverity.Error);
            }
        });
    }

    public void ShowNotification(string title, string message)
        => _trayWindow.ShowBalloon(title, message);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _trayWindow.Dispose();
    }

    // ---------- Win32 ----------

    private sealed class TrayWindow : IDisposable
    {
        private const uint NimAdd = 0;
        private const uint NimModify = 1;
        private const uint NimDelete = 2;
        private const uint NifMessage = 0x01;
        private const uint NifIcon = 0x02;
        private const uint NifTip = 0x04;
        private const uint NifInfo = 0x10;

        private const string ClassName = "MiHomeTrayWnd";
        private static bool _classRegistered;

        private readonly uint _callbackMessage;
        private readonly Action<uint> _onCommand;
        private nint _hwnd;
        private List<(int Id, string Text, Func<Task> Action)> _items = new();
        private NOTIFYICONDATA _nid;

        public TrayWindow(uint callbackMessage, Action<uint> onCommand)
        {
            _callbackMessage = callbackMessage;
            _onCommand = onCommand;

            var thread = new Thread(MessageLoop)
            {
                Name = "MiHomeTray",
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // 等待窗口创建完成
            while (_hwnd == nint.Zero)
            {
                Thread.Sleep(10);
            }
        }

        private void MessageLoop()
        {
            // 保持委托存活（非托管侧持有函数指针）
            _wndProcDelegate = WndProcImpl;
            if (!_classRegistered)
            {
                var wc = new WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                    lpszClassName = ClassName,
                };
                RegisterClass(ref wc);
                _classRegistered = true;
            }
            _hwnd = CreateWindowEx(0, ClassName, "MiHomeTray", 0, 0, 0, 0, 0, nint.Zero, nint.Zero, nint.Zero, nint.Zero);
            _instances[_hwnd] = this;

            while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private static readonly Dictionary<nint, TrayWindow> _instances = new();
        private WndProcDelegate _wndProcDelegate = null!;

        private delegate nint WndProcDelegate(nint hwnd, uint msg, nint wParam, nint lParam);

        private nint WndProcImpl(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            if (msg == _callbackMessage)
            {
                var mouse = lParam.ToInt64() & 0xFFFF;
                if (mouse == 0x0202 /*WM_LBUTTONUP*/)
                {
                    _onCommand((uint)_items.First(i => i.Text == "打开主界面").Id);
                }
                else if (mouse == 0x0205 /*WM_RBUTTONUP*/)
                {
                    ShowContextMenu();
                }
                return nint.Zero;
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private void ShowContextMenu()
        {
            var menu = CreatePopupMenu();
            foreach (var (id, text, _) in _items)
            {
                if (id < 0)
                {
                    AppendMenu(menu, 0x800 /*MF_SEPARATOR*/, 0, null);
                }
                else
                {
                    AppendMenu(menu, 0 /*MF_STRING*/, (nuint)id, text);
                }
            }
            SetForegroundWindow(_hwnd);
            var point = new POINT();
            GetCursorPos(ref point);
            // TPM_RIGHTBUTTON | TPM_RETURNCMD：返回所选命令 ID
            var chosen = TrackPopupMenu(menu, 0x0002 | 0x0100, point.X, point.Y, 0, _hwnd, nint.Zero);
            DestroyMenu(menu);
            if (chosen != 0)
            {
                _onCommand((uint)chosen);
            }
        }

        public void SetMenuItems(List<(int Id, string Text, Func<Task> Action)> items) => _items = items;

        public Func<Task>? FindAction(uint id)
        {
            var hit = _items.FirstOrDefault(i => i.Id == (int)id);
            return hit.Action;
        }

        public void AddIcon(string tip, nint icon)
        {
            _nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NifMessage | NifIcon | NifTip,
                uCallbackMessage = _callbackMessage,
                hIcon = icon,
                szTip = tip,
            };
            Shell_NotifyIcon(NimAdd, ref _nid);
        }

        public void ShowBalloon(string title, string message)
        {
            var nid = _nid;
            nid.uFlags |= NifInfo;
            nid.szInfoTitle = title;
            nid.szInfo = message;
            Shell_NotifyIcon(NimModify, ref nid);
        }

        public void Dispose()
        {
            Shell_NotifyIcon(NimDelete, ref _nid);
            if (_hwnd != nint.Zero)
            {
                _instances.Remove(_hwnd);
                DestroyWindow(_hwnd);
            }
        }

        // --- P/Invoke ---

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public nint hwnd;
            public uint message;
            public nint wParam;
            public nint lParam;
            public uint time;
            public POINT pt;
        }

        private const int GwlUserData = -21;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
            uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(nint hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern nint DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(ref POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        private static extern nint CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(nint hMenu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll")]
        private static extern nint LoadIcon(nint hInstance, nint lpIconName);

        public static nint LoadImageNative(string path)
            => LoadImage(nint.Zero, path, 1 /*IMAGE_ICON*/, 0, 0, 0x10 /*LR_LOADFROMFILE*/);

        public static nint LoadDefaultIcon() => LoadIcon(nint.Zero, (nint)32512 /*IDI_APPLICATION*/);
    }
}
