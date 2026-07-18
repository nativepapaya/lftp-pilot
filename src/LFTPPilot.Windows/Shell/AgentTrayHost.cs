using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LFTPPilot.Windows.Activation;

namespace LFTPPilot.Windows.Shell;

public sealed class AgentTrayHost : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = NativeMethods.WindowMessageApp + 1;
    private const string Tooltip = "LFTP Pilot background agent";

    private static readonly NativeMethods.WindowProcedure SharedWindowProcedure = WindowProcedure;

    private readonly AgentTrayActions _actions;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly string _windowClassName = $"LFTPPilot.AgentTray.{Environment.ProcessId}";
    private Exception? _startupFailure;
    private nint _window;
    private uint _taskbarCreatedMessage;
    private bool _iconAdded;
    private bool _disposed;

    private AgentTrayHost(AgentTrayActions actions)
    {
        _actions = actions;
        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "LFTP Pilot notification area",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_startupFailure is not null)
        {
            _thread.Join();
            throw new InvalidOperationException("The LFTP Pilot notification-area surface could not start.", _startupFailure);
        }
    }

    public static AgentTrayHost? TryStart(Action requestStop)
    {
        ArgumentNullException.ThrowIfNull(requestStop);
        try
        {
            return new(new AgentTrayActions(OpenApp, requestStop));
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            Trace.TraceError("The Agent notification-area surface is unavailable: {0}", exception.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var window = Volatile.Read(ref _window);
        if (window != 0)
            _ = NativeMethods.PostMessage(window, NativeMethods.WindowMessageClose, 0, 0);
        if (_thread.IsAlive && Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
            _thread.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private static bool OpenApp(Uri uri)
    {
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        return process is not null;
    }

    private void RunMessageLoop()
    {
        GCHandle selfHandle = default;
        ushort windowClass = 0;
        try
        {
            selfHandle = GCHandle.Alloc(this);
            var module = NativeMethods.GetModuleHandle(null);
            var windowClassDefinition = new NativeMethods.WindowClass
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WindowClass>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(SharedWindowProcedure),
                Instance = module,
                ClassName = _windowClassName,
            };
            windowClass = NativeMethods.RegisterClassEx(ref windowClassDefinition);
            if (windowClass == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());

            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
            if (_taskbarCreatedMessage == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            _window = NativeMethods.CreateWindowEx(
                0,
                _windowClassName,
                Tooltip,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                module,
                GCHandle.ToIntPtr(selfHandle));
            if (_window == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            AddIcon();
            _ready.Set();

            while (true)
            {
                var result = NativeMethods.GetMessage(out var message, 0, 0, 0);
                if (result == 0) break;
                if (result < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                _ = NativeMethods.TranslateMessage(ref message);
                _ = NativeMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            if (_ready.IsSet)
                Trace.TraceError("The Agent notification-area message loop failed: {0}", exception.Message);
            _ready.Set();
        }
        finally
        {
            RemoveIcon();
            var window = Interlocked.Exchange(ref _window, 0);
            if (window != 0 && NativeMethods.IsWindow(window)) _ = NativeMethods.DestroyWindow(window);
            if (windowClass != 0) _ = NativeMethods.UnregisterClass(_windowClassName, NativeMethods.GetModuleHandle(null));
            if (selfHandle.IsAllocated) selfHandle.Free();
        }
    }

    private static nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        AgentTrayHost? host = null;
        if (message == NativeMethods.WindowMessageNonClientCreate)
        {
            var create = Marshal.PtrToStructure<NativeMethods.CreateStructure>(lParam);
            if (create.CreateParameters != 0)
            {
                var handle = GCHandle.FromIntPtr(create.CreateParameters);
                host = handle.Target as AgentTrayHost;
                _ = NativeMethods.SetWindowLongPtr(window, NativeMethods.WindowLongUserData, create.CreateParameters);
            }
        }
        else
        {
            var userData = NativeMethods.GetWindowLongPtr(window, NativeMethods.WindowLongUserData);
            if (userData != 0) host = GCHandle.FromIntPtr(userData).Target as AgentTrayHost;
        }

        try
        {
            return host?.HandleWindowMessage(window, message, wParam, lParam)
                ?? NativeMethods.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            // Exceptions must never cross the unmanaged window-procedure boundary
            // and terminate the Agent or its active LFTP process trees.
            Trace.TraceError("The Agent notification-area callback failed: {0}", exception.Message);
            return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private nint HandleWindowMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            AddIcon();
            return 0;
        }

        switch (message)
        {
            case TrayCallbackMessage:
                switch ((uint)(lParam.ToInt64() & 0xffff))
                {
                    case NativeMethods.WindowMessageLeftButtonDoubleClick:
                    case NativeMethods.NotifyIconKeySelect:
                        _actions.TryExecute(AgentTrayActions.OpenCommand);
                        break;
                    case NativeMethods.WindowMessageContextMenu:
                    case NativeMethods.WindowMessageRightButtonUp:
                        ShowContextMenu(window);
                        break;
                }
                return 0;
            case NativeMethods.WindowMessageCommand:
                _actions.TryExecute((uint)(wParam & 0xffff));
                return 0;
            case NativeMethods.WindowMessageDestroy:
                RemoveIcon();
                NativeMethods.PostQuitMessage(0);
                return 0;
            default:
                return NativeMethods.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private void ShowContextMenu(nint window)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MenuFlagString, AgentTrayActions.OpenCommand, "Open LFTP Pilot");
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MenuFlagSeparator, 0, null);
            _ = NativeMethods.AppendMenu(menu, NativeMethods.MenuFlagString, AgentTrayActions.StopCommand, "Stop Agent and transfers");
            if (!NativeMethods.GetCursorPos(out var cursor)) return;
            _ = NativeMethods.SetForegroundWindow(window);
            var command = NativeMethods.TrackPopupMenu(
                menu,
                NativeMethods.TrackPopupMenuReturnCommand | NativeMethods.TrackPopupMenuRightButton,
                cursor.X,
                cursor.Y,
                0,
                window,
                0);
            if (command != 0) _actions.TryExecute(command);
            var iconData = CreateIconData(0);
            _ = NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconSetFocus, ref iconData);
            _ = NativeMethods.PostMessage(window, NativeMethods.WindowMessageNull, 0, 0);
        }
        finally
        {
            _ = NativeMethods.DestroyMenu(menu);
        }
    }

    private void AddIcon()
    {
        if (_window == 0 || _iconAdded) return;
        var icon = NativeMethods.LoadIcon(0, (nint)NativeMethods.ApplicationIconResource);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var data = CreateIconData(icon);
        if (!NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconAdd, ref data))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        _iconAdded = true;
        data.VersionOrTimeout = NativeMethods.NotifyIconVersion4;
        _ = NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconSetVersion, ref data);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded || _window == 0) return;
        var data = CreateIconData(0);
        _ = NativeMethods.ShellNotifyIcon(NativeMethods.NotifyIconDelete, ref data);
        _iconAdded = false;
    }

    private NativeMethods.NotifyIconData CreateIconData(nint icon) => new()
    {
        Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
        Window = _window,
        Id = TrayIconId,
        Flags = NativeMethods.NotifyIconFlagMessage | NativeMethods.NotifyIconFlagIcon | NativeMethods.NotifyIconFlagTip | NativeMethods.NotifyIconFlagShowTip,
        CallbackMessage = TrayCallbackMessage,
        Icon = icon,
        Tip = Tooltip,
    };

    private static class NativeMethods
    {
        internal const int WindowLongUserData = -21;
        internal const uint WindowMessageNull = 0x0000;
        internal const uint WindowMessageNonClientCreate = 0x0081;
        internal const uint WindowMessageCommand = 0x0111;
        internal const uint WindowMessageClose = 0x0010;
        internal const uint WindowMessageDestroy = 0x0002;
        internal const uint WindowMessageContextMenu = 0x007b;
        internal const uint WindowMessageRightButtonUp = 0x0205;
        internal const uint WindowMessageLeftButtonDoubleClick = 0x0203;
        internal const uint WindowMessageApp = 0x8000;
        internal const uint NotifyIconKeySelect = 0x0401;
        internal const uint NotifyIconAdd = 0;
        internal const uint NotifyIconDelete = 2;
        internal const uint NotifyIconSetFocus = 3;
        internal const uint NotifyIconSetVersion = 4;
        internal const uint NotifyIconVersion4 = 4;
        internal const uint NotifyIconFlagMessage = 0x0001;
        internal const uint NotifyIconFlagIcon = 0x0002;
        internal const uint NotifyIconFlagTip = 0x0004;
        internal const uint NotifyIconFlagShowTip = 0x0080;
        internal const uint MenuFlagString = 0;
        internal const uint MenuFlagSeparator = 0x0800;
        internal const uint TrackPopupMenuRightButton = 0x0002;
        internal const uint TrackPopupMenuReturnCommand = 0x0100;
        internal const uint ApplicationIconResource = 32512;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Size;
            internal uint Style;
            internal nint WindowProcedure;
            internal int ClassExtraBytes;
            internal int WindowExtraBytes;
            internal nint Instance;
            internal nint Icon;
            internal nint Cursor;
            internal nint BackgroundBrush;
            internal string? MenuName;
            internal string ClassName;
            internal nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct CreateStructure
        {
            internal nint CreateParameters;
            internal nint Instance;
            internal nint Menu;
            internal nint Parent;
            internal int Height;
            internal int Width;
            internal int Y;
            internal int X;
            internal int Style;
            internal nint Name;
            internal nint Class;
            internal uint ExtendedStyle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal nint Window;
            internal uint Value;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal Point Point;
            internal uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NotifyIconData
        {
            internal uint Size;
            internal nint Window;
            internal uint Id;
            internal uint Flags;
            internal uint CallbackMessage;
            internal nint Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
            internal uint State;
            internal uint StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
            internal uint VersionOrTimeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
            internal uint InfoFlags;
            internal Guid ItemGuid;
            internal nint BalloonIcon;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClass(string className, nint instance);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern nint CreateWindowEx(uint extendedStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        internal static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint window);

        [DllImport("user32.dll", EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint window);

        [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
        internal static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);

        [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
        internal static extern nint DispatchMessage(ref Message message);

        [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll", EntryPoint = "PostQuitMessage")]
        internal static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern nint SetWindowLongPtr(nint window, int index, nint value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern nint GetWindowLongPtr(nint window, int index);

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessage(string value);

        [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
        internal static extern nint LoadIcon(nint instance, nint iconName);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

        [DllImport("user32.dll", EntryPoint = "CreatePopupMenu", SetLastError = true)]
        internal static extern nint CreatePopupMenu();

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenu(nint menu, uint flags, uint newItem, string? text);

        [DllImport("user32.dll", EntryPoint = "TrackPopupMenu", SetLastError = true)]
        internal static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);

        [DllImport("user32.dll", EntryPoint = "DestroyMenu")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(nint menu);

        [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint window);
    }
}

internal sealed class AgentTrayActions(Func<Uri, bool> openApp, Action requestStop)
{
    internal const uint OpenCommand = 1001;
    internal const uint StopCommand = 1002;
    internal static readonly Uri TransfersUri = new($"{ProtocolActivationParser.Scheme}://transfers");

    internal bool TryExecute(uint command)
    {
        try
        {
            switch (command)
            {
                case OpenCommand:
                    return openApp(TransfersUri);
                case StopCommand:
                    requestStop();
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception exception)
        {
            Trace.TraceError("The Agent notification-area command failed: {0}", exception.Message);
            return false;
        }
    }
}
