using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Win10Audio
{
    // Keeps the existing Windows volume icon. No Explorer injection, registry edits,
    // overlay windows, or additional notification icon are used.
    public sealed class TrayVolumeHook : IDisposable
    {
        private static readonly Guid VolumeIconGuid = new Guid("7820AE73-23E3-4229-82C1-E41CB67D5B9C");
        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const uint WM_QUIT = 0x0012;
        private const uint GA_ROOT = 2;
        private readonly Action _toggle;
        private readonly Action _dismiss;
        private readonly HookProc _hookProc;
        private readonly HookProc _keyboardProc;
        private readonly Thread _thread;
        private readonly ManualResetEvent _started = new ManualResetEvent(false);
        private System.Threading.Timer _rectTimer;
        private IntPtr _hook;
        private IntPtr _keyboardHook;
        private uint _threadId;
        private volatile bool _disposed;
        private bool _capturedDown;
        private bool _capturedV;
        private int _refreshing;
        private RectSnapshot _snapshot;
        private RectSnapshot _panelSnapshot;
        public bool IsInstalled { get { return _hook != IntPtr.Zero; } }
        public bool IsKeyboardInstalled { get { return _keyboardHook != IntPtr.Zero; } }
        public int InstallationError { get; private set; }

        public TrayVolumeHook(Action toggle) : this(toggle, null) { }

        public TrayVolumeHook(Action toggle, Action dismiss)
        {
            if (toggle == null) throw new ArgumentNullException("toggle");
            _toggle = toggle;
            _dismiss = dismiss;
            _hookProc = MouseCallback; // Root the delegate for the hook lifetime.
            _keyboardProc = KeyboardCallback;
            _thread = new Thread(HookThread);
            _thread.IsBackground = true;
            _thread.Name = "Native volume icon mouse hook";
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _started.WaitOne(2000);
        }

        public void SetPanelBounds(Rectangle bounds)
        {
            Interlocked.Exchange(ref _panelSnapshot, bounds.IsEmpty ? null : new RectSnapshot(bounds, Environment.TickCount));
        }

        // Returned coordinates are physical screen pixels, including negative
        // coordinates on monitors to the left or above the primary monitor.
        public static Rectangle GetVolumeIconBounds()
        {
            Rectangle bounds;
            return TryGetVolumeIconBounds(out bounds) ? bounds : Rectangle.Empty;
        }

        public static bool TryGetVolumeIconBounds(out Rectangle bounds)
        {
            IntPtr previous = IntPtr.Zero;
            try
            {
                // Shell icon rectangles and MSLLHOOKSTRUCT points must share a
                // physical coordinate space even in a system-DPI-aware WPF app.
                previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
                NOTIFYICONIDENTIFIER id = new NOTIFYICONIDENTIFIER();
                id.cbSize = (uint)Marshal.SizeOf(typeof(NOTIFYICONIDENTIFIER));
                id.guidItem = VolumeIconGuid;
                RECT rect;
                if (Shell_NotifyIconGetRect(ref id, out rect) == 0 &&
                    rect.right > rect.left && rect.bottom > rect.top)
                {
                    bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
                    return true;
                }
            }
            catch (EntryPointNotFoundException) { }
            finally
            {
                if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
            }
            bounds = Rectangle.Empty;
            return false;
        }

        private void HookThread()
        {
            try
            {
                _threadId = GetCurrentThreadId();
                SetThreadDpiAwarenessContext(new IntPtr(-4));
                // Create the thread queue before another thread can send WM_QUIT.
                MSG msg;
                PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) InstallationError = Marshal.GetLastWin32Error();
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandle(null), 0);
                if (_keyboardHook == IntPtr.Zero) InstallationError = Marshal.GetLastWin32Error();
                // Refresh from a worker instead of doing Explorer IPC inside the
                // low-level hook. Re-querying the GUID handles Explorer restarts,
                // tray reordering, hotplug, taskbar movement, and changed DPI.
                // The initial lookup must also be asynchronous: an Explorer IPC
                // delay here would starve newly registered low-level hooks before
                // their message loop starts, causing Windows to remove them.
                _rectTimer = new System.Threading.Timer(RefreshBounds, null, 0, 100);
                _started.Set();
                while (!_disposed && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            finally
            {
                if (_rectTimer != null) _rectTimer.Dispose();
                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
                if (_keyboardHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_keyboardHook);
                    _keyboardHook = IntPtr.Zero;
                }
                _started.Set();
            }
        }

        private void RefreshBounds(object state)
        {
            if (_disposed || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
            try
            {
                Rectangle bounds;
                RectSnapshot next = TryGetVolumeIconBounds(out bounds)
                    ? new RectSnapshot(bounds, Environment.TickCount) : null;
                Interlocked.Exchange(ref _snapshot, next);
            }
            catch { Interlocked.Exchange(ref _snapshot, null); }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        }

        private bool IsVolumePoint(POINT point)
        {
            RectSnapshot snapshot = _snapshot;
            // Fail open if Explorer is stalled or the cached result is old.
            if (snapshot == null || unchecked((uint)(Environment.TickCount - snapshot.Tick)) > 1500 ||
                !snapshot.Bounds.Contains(point.x, point.y)) return false;
            IntPtr underMouse = WindowFromPoint(point);
            IntPtr root = GetAncestor(underMouse, GA_ROOT);
            StringBuilder name = new StringBuilder(64);
            GetClassName(root, name, name.Capacity);
            string className = name.ToString();
            // A popup or another app above the taskbar must never lose a click.
            return className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd";
        }

        private IntPtr MouseCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !_disposed)
            {
                int kind = message.ToInt32();
                if (kind == WM_LBUTTONDOWN || kind == WM_LBUTTONUP || kind == 0x0204 || kind == 0x0207 || kind == 0x020B)
                {
                    MSLLHOOKSTRUCT mouse = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(data, typeof(MSLLHOOKSTRUCT));
                    if (kind == WM_LBUTTONDOWN && IsVolumePoint(mouse.pt))
                    {
                        _capturedDown = true;
                        return new IntPtr(1);
                    }
                    if (kind == WM_LBUTTONUP && _capturedDown)
                    {
                        _capturedDown = false;
                        if (IsVolumePoint(mouse.pt))
                            ThreadPool.QueueUserWorkItem(delegate {
                                if (_disposed) return;
                                try { _toggle(); } catch { }
                            });
                        // Consume the paired up even if the pointer was dragged
                        // away; no unmatched mouse-up reaches another app.
                        return new IntPtr(1);
                    }
                    if (kind != WM_LBUTTONUP && _dismiss != null)
                    {
                        RectSnapshot panel = _panelSnapshot;
                        if (panel != null && !panel.Bounds.Contains(mouse.pt.x, mouse.pt.y))
                            ThreadPool.QueueUserWorkItem(delegate { if (!_disposed) try { _dismiss(); } catch { } });
                    }
                }
            }
            return CallNextHookEx(_hook, code, message, data);
        }

        private IntPtr KeyboardCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !_disposed)
            {
                KBDLLHOOKSTRUCT key = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(data, typeof(KBDLLHOOKSTRUCT));
                int kind = message.ToInt32();
                bool down = kind == 0x0100 || kind == 0x0104;
                bool up = kind == 0x0101 || kind == 0x0105;
                if (key.vkCode == 0x56 && (key.flags & 0x10) == 0)
                {
                    if (down && (_capturedV || ((GetAsyncKeyState(0x11) & 0x8000) != 0 && ((GetAsyncKeyState(0x5B) | GetAsyncKeyState(0x5C)) & 0x8000) != 0)))
                    {
                        if (!_capturedV)
                        {
                            _capturedV = true;
                            // Mark the Windows-key chord as used so its release
                            // does not open Start after the V key is consumed.
                            INPUT[] mask = new INPUT[2];
                            mask[0].type = 1; mask[0].union.ki.wVk = 0xE8;
                            mask[1].type = 1; mask[1].union.ki.wVk = 0xE8; mask[1].union.ki.dwFlags = 2;
                            SendInput(2, mask, Marshal.SizeOf(typeof(INPUT)));
                            ThreadPool.QueueUserWorkItem(delegate { if (!_disposed) try { _toggle(); } catch { } });
                        }
                        return new IntPtr(1);
                    }
                    if (up && _capturedV) { _capturedV = false; return new IntPtr(1); }
                }
            }
            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != _thread) _thread.Join(1000);
            // Do not dispose _started if a delayed hook thread can still set it.
        }

        private sealed class RectSnapshot
        {
            public readonly Rectangle Bounds;
            public readonly int Tick;
            public RectSnapshot(Rectangle bounds, int tick) { Bounds = bounds; Tick = tick; }
        }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NOTIFYICONIDENTIFIER
        { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT
        { public POINT pt; public uint mouseData, flags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT
        { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION union; }
        [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
        }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG
        { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    }
}
