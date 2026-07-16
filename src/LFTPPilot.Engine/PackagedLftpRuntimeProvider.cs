using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public sealed class PackagedLftpRuntimeProvider : ILftpRuntimeProvider
{
    private const int SupportedManifestSchema = 3;
    private readonly string _runtimeRoot;
    private readonly string? _explicitTestExecutable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LftpRuntimeDescriptor? _cached;

    public PackagedLftpRuntimeProvider()
        : this(GetDefaultRuntimeRoot(), null)
    {
    }

    private PackagedLftpRuntimeProvider(string runtimeRoot, string? explicitTestExecutable)
    {
        _runtimeRoot = Path.GetFullPath(runtimeRoot);
        _explicitTestExecutable = explicitTestExecutable;
    }

    internal static PackagedLftpRuntimeProvider CreatePackageCandidateForTests(string runtimeRoot) => new(runtimeRoot, null);

    public static PackagedLftpRuntimeProvider CreateTestOverride(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var fullPath = Path.GetFullPath(executablePath);
        return new(Path.GetDirectoryName(fullPath)!, fullPath);
    }

    private static string GetDefaultRuntimeRoot()
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var directory = new DirectoryInfo(baseDirectory);
        return string.Equals(directory.Name, "agent", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(Path.Combine(baseDirectory, "..", "lftp"))
            : Path.Combine(baseDirectory, "lftp");
    }

    public async Task<LftpRuntimeDescriptor> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null) return _cached;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            _cached = _explicitTestExecutable is null
                ? await AuthenticatePackagedRuntimeAsync(cancellationToken).ConfigureAwait(false)
                : ResolveTestOverride();
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private LftpRuntimeDescriptor ResolveTestOverride()
    {
        var executable = _explicitTestExecutable ?? throw new InvalidOperationException("The explicit test runtime is unavailable.");
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The explicit test runtime executable was not found.", executable);
        return new(_runtimeRoot, executable, Path.GetDirectoryName(executable)!, false, "explicit-test-override", true);
    }

    private async Task<LftpRuntimeDescriptor> AuthenticatePackagedRuntimeAsync(CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(_runtimeRoot, "bundle-manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("The packaged LFTP runtime manifest is missing.");
        if (new FileInfo(manifestPath).Length > 16 * 1024 * 1024) throw new InvalidDataException("The packaged LFTP runtime manifest is too large.");
        if ((File.GetAttributes(_runtimeRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The packaged LFTP runtime root cannot be a reparse point.");

        await using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<RuntimeManifest>(manifestStream, FramedJsonStream.SerializerOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The packaged LFTP runtime manifest is empty.");
        if (manifest.Schema != SupportedManifestSchema || !string.Equals(manifest.Architecture, "x64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The packaged LFTP runtime manifest has an unsupported schema or architecture.");
        var revisionPath = Path.Combine(_runtimeRoot, ".bundle-rev");
        if (string.IsNullOrWhiteSpace(manifest.BundleRevision) || !File.Exists(revisionPath) ||
            !string.Equals((await File.ReadAllTextAsync(revisionPath, cancellationToken).ConfigureAwait(false)).Trim(), manifest.BundleRevision, StringComparison.Ordinal))
            throw new InvalidDataException("The packaged LFTP runtime revision marker is missing or mismatched.");
        if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > 10_000)
            throw new InvalidDataException("The packaged LFTP runtime manifest has an invalid file inventory.");

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bundle-manifest.json", ".bundle-rev" };
        foreach (var item in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelativePath(item.Path);
            if (!allowed.Add(relative)) throw new InvalidDataException($"The packaged runtime manifest repeats '{relative}'.");
            var fullPath = GetContainedPath(relative);
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length != item.Size || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"The packaged runtime file '{relative}' is missing or has invalid metadata.");
            await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexStringLower(hash), item.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The packaged runtime file '{relative}' failed SHA-256 authentication.");
        }

        foreach (var file in EnumerateRuntimeFilesSafely())
        {
            var relative = Path.GetRelativePath(_runtimeRoot, file).Replace('\\', '/');
            if (!allowed.Contains(relative)) throw new InvalidDataException($"The packaged runtime contains unexpected file '{relative}'.");
        }

        var executable = GetContainedPath("usr/bin/lftp.exe");
        var ssh = GetContainedPath("usr/bin/ssh.exe");
        var shell = GetContainedPath("usr/bin/sh.exe");
        if (!allowed.Contains("usr/bin/lftp.exe") || !allowed.Contains("usr/bin/ssh.exe") || !allowed.Contains("usr/bin/sh.exe") ||
            !File.Exists(executable) || !File.Exists(ssh) || !File.Exists(shell))
            throw new InvalidDataException("The packaged runtime is missing authenticated LFTP, SSH, or shell executables.");
        return new(_runtimeRoot, executable, Path.GetDirectoryName(executable)!, true, "packaged-manifest");
    }

    private IEnumerable<string> EnumerateRuntimeFilesSafely()
    {
        var pending = new Stack<string>();
        pending.Push(_runtimeRoot);
        var directoryCount = 0;
        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            if (++directoryCount > 10_000) throw new InvalidDataException("The packaged runtime contains too many directories.");
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The packaged runtime cannot contain directory reparse points.");
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory)) yield return file;
        }
    }

    private string GetContainedPath(string relative)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_runtimeRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _runtimeRoot.EndsWith(Path.DirectorySeparatorChar) ? _runtimeRoot : _runtimeRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The runtime manifest contains a path outside its root.");
        return fullPath;
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || Path.IsPathFullyQualified(value) || value.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException("The runtime manifest contains an invalid path.");
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Split('/').Any(static segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The runtime manifest contains an invalid relative path.");
        return normalized;
    }

    private sealed record RuntimeManifest(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("bundleRevision")] string BundleRevision,
        [property: JsonPropertyName("architecture")] string Architecture,
        [property: JsonPropertyName("files")] IReadOnlyList<RuntimeFile>? Files);

    private sealed record RuntimeFile(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha256")] string Sha256);
}
