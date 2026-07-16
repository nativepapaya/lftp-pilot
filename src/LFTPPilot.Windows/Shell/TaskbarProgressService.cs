using System.Runtime.InteropServices;

namespace LFTPPilot.Windows.Shell;

public enum TaskbarProgressState { None, Indeterminate, Normal, Error, Paused }

public sealed class TaskbarProgressService
{
    private readonly ITaskbarList3 _taskbar;

    public TaskbarProgressService()
    {
        _taskbar = (ITaskbarList3)(object)new TaskbarList();
        _taskbar.HrInit();
    }

    public void SetState(nint windowHandle, TaskbarProgressState state)
    {
        if (windowHandle == 0) throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        _taskbar.SetProgressState(windowHandle, state switch
        {
            TaskbarProgressState.None => 0,
            TaskbarProgressState.Indeterminate => 1,
            TaskbarProgressState.Normal => 2,
            TaskbarProgressState.Error => 4,
            TaskbarProgressState.Paused => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        });
    }

    public void SetValue(nint windowHandle, ulong completed, ulong total)
    {
        if (windowHandle == 0) throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        if (total == 0) throw new ArgumentOutOfRangeException(nameof(total));
        if (completed > total) throw new ArgumentOutOfRangeException(nameof(completed));
        _taskbar.SetProgressValue(windowHandle, completed, total);
    }

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private sealed class TaskbarList { }

    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, uint flags);
    }
}
