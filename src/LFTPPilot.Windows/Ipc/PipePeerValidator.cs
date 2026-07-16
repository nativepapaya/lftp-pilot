using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LFTPPilot.Windows.Ipc;

/// <summary>Validates that a pipe peer is the process the launcher expected.</summary>
public static class PipePeerValidator
{
    public static int ValidateClient(NamedPipeServerStream pipe, Process expectedProcess)
    {
        ArgumentNullException.ThrowIfNull(expectedProcess);
        if (expectedProcess.HasExited) throw new UnauthorizedAccessException("The expected named-pipe client has exited.");
        return ValidateClient(pipe, expectedProcess.Id);
    }

    public static int ValidateServer(NamedPipeClientStream pipe, Process expectedProcess)
    {
        ArgumentNullException.ThrowIfNull(expectedProcess);
        if (expectedProcess.HasExited) throw new UnauthorizedAccessException("The expected named-pipe server has exited.");
        return ValidateServer(pipe, expectedProcess.Id);
    }

    public static int ValidateClient(NamedPipeServerStream pipe, int expectedProcessId)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected) throw new InvalidOperationException("The pipe is not connected.");
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actual))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to identify the named-pipe client.");
        return Validate(actual, expectedProcessId);
    }

    public static int ValidateServer(NamedPipeClientStream pipe, int expectedProcessId)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected) throw new InvalidOperationException("The pipe is not connected.");
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to identify the named-pipe server.");
        return Validate(actual, expectedProcessId);
    }

    private static int Validate(uint actualProcessId, int expectedProcessId)
    {
        if (expectedProcessId <= 0 || actualProcessId != (uint)expectedProcessId)
            throw new UnauthorizedAccessException($"Named-pipe peer PID {actualProcessId} does not match expected PID {expectedProcessId}.");
        try
        {
            using Process process = Process.GetProcessById(expectedProcessId);
            if (process.HasExited) throw new UnauthorizedAccessException("The named-pipe peer has exited.");
        }
        catch (ArgumentException error)
        {
            throw new UnauthorizedAccessException("The named-pipe peer no longer exists.", error);
        }
        return expectedProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
