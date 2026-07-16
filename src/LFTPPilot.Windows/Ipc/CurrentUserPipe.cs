using System.IO.Pipes;

namespace LFTPPilot.Windows.Ipc;

public static class CurrentUserPipe
{
    public static NamedPipeServerStream CreateServer(
        string name,
        PipeDirection direction,
        int maximumInstances = 1,
        int inputBufferSize = 64 * 1024,
        int outputBufferSize = 64 * 1024)
    {
        ValidateName(name);
        if (maximumInstances is < 1 or > 254) throw new ArgumentOutOfRangeException(nameof(maximumInstances));
        return new NamedPipeServerStream(
            name,
            direction,
            maximumInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
            inputBufferSize,
            outputBufferSize);
    }

    public static NamedPipeClientStream CreateClient(string serverName, string name, PipeDirection direction)
    {
        ValidateName(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        return new NamedPipeClientStream(
            serverName,
            name,
            direction,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 200 || name.IndexOfAny(['\\', '/', ':']) >= 0)
            throw new ArgumentException("The pipe name is invalid.", nameof(name));
    }
}
