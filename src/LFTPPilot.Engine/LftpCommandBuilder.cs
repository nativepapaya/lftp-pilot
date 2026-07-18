using System.Text;
using LFTPPilot.Core;

namespace LFTPPilot.Engine;

public static class LftpCommandBuilder
{
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RejectProtocolControls(value, nameof(value));
        return $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    public static string ShellQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RejectProtocolControls(value, nameof(value));
        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    public static string BuildOpen(
        ConnectionProfile profile,
        string? secret = null,
        string? trustedKnownHostsPath = null,
        string? hostKeyAlias = null)
    {
        ProfileValidator.ThrowIfInvalid(profile);
        if (profile.Authentication is AuthenticationKind.Password or AuthenticationKind.AskOnConnect && string.IsNullOrEmpty(secret))
            throw new ArgumentException("This profile requires a password or passphrase.", nameof(secret));
        if (secret is not null) RejectProtocolControls(secret, nameof(secret));

        if (profile.Protocol == ConnectionProtocol.Sftp)
        {
            EnsureFullyQualifiedLocalPath(trustedKnownHostsPath ?? string.Empty, nameof(trustedKnownHostsPath));
            SftpHostKeyIdentity.ValidateAlias(hostKeyAlias, nameof(hostKeyAlias));
        }
        else if (trustedKnownHostsPath is not null || hostKeyAlias is not null)
        {
            throw new ArgumentException("SSH host-key trust inputs are valid only for SFTP profiles.");
        }

        var commands = new List<string>();
        switch (profile.Protocol)
        {
            case ConnectionProtocol.Ftp:
                commands.Add("set ftp:ssl-allow false");
                break;
            case ConnectionProtocol.FtpOpportunisticTls:
                commands.Add("set ftp:ssl-allow true");
                commands.Add("set ftp:ssl-force false");
                break;
            case ConnectionProtocol.FtpsExplicit:
            case ConnectionProtocol.FtpsImplicit:
                commands.Add("set ftp:ssl-allow true");
                commands.Add("set ftp:ssl-force true");
                commands.Add("set ftp:ssl-protect-data true");
                break;
        }

        if (profile.Protocol == ConnectionProtocol.Sftp)
        {
            var knownHostsOption = ShellQuote(SftpHostKeyIdentity.CreateUserKnownHostsOption(
                ToMsysPath(trustedKnownHostsPath!)));
            var aliasOption = ShellQuote($"HostKeyAlias={hostKeyAlias}");
            var connectProgram = $"ssh -F none -a -x -o StrictHostKeyChecking=yes -o {knownHostsOption} " +
                $"-o GlobalKnownHostsFile=none -o UpdateHostKeys=no -o VerifyHostKeyDNS=no -o CheckHostIP=no " +
                $"-o IdentityAgent=none -o {aliasOption}";
            if (profile.Authentication == AuthenticationKind.SshKey)
                connectProgram += $" -o IdentitiesOnly=yes -i {ShellQuote(ToMsysPath(profile.SshKeyPath!))}";
            commands.Add($"set sftp:connect-program {Quote(connectProgram)}");
        }

        var scheme = profile.Protocol switch
        {
            ConnectionProtocol.Sftp => "sftp",
            ConnectionProtocol.FtpsImplicit => "ftps",
            _ => "ftp",
        };
        var endpoint = $"{scheme}://{FormatHost(profile.Host)}:{profile.Port}";
        var open = profile.Authentication switch
        {
            AuthenticationKind.Anonymous => $"open {Quote(endpoint)}",
            // LFTP 4.9.3 calls GetPass before launching its configured OpenSSH
            // program unless the password field is initialized. An explicit
            // empty value lets an unencrypted -i key authenticate without a
            // terminal prompt; encrypted keys receive their real passphrase.
            AuthenticationKind.SshKey => $"open --user {Quote(profile.UserName)} --password {Quote(secret ?? string.Empty)} {Quote(endpoint)}",
            _ => $"open --user {Quote(profile.UserName)} --password {Quote(secret!)} {Quote(endpoint)}",
        };
        commands.Add(open);
        if (!string.IsNullOrWhiteSpace(profile.InitialRemotePath)) commands.Add($"cd {Quote(DashSafe(profile.InitialRemotePath))}");
        return string.Join("; ", commands);
    }

    public static string BuildList(string remotePath, bool fresh = false) =>
        $"{(fresh ? "recls" : "cls")} -laB --time-style=long-iso {Quote(DashSafe(EnsureRemoteDirectory(remotePath)))}";

    public static string BuildNameList(string remotePath, bool fresh = false) =>
        $"{(fresh ? "recls" : "cls")} -1FaB {Quote(DashSafe(EnsureRemoteDirectory(remotePath)))}";

    public static string BuildRemoteFind(string remoteRoot, int maxDepth)
    {
        if (!ProfileValidator.IsCanonicalRemotePath(remoteRoot))
            throw new ArgumentException("A bounded canonical remote-search root is required.", nameof(remoteRoot));
        if (maxDepth is < 1 or > RemoteSearchPolicy.MaximumMaxDepth)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), $"Remote-search depth must be between 1 and {RemoteSearchPolicy.MaximumMaxDepth}.");
        return $"command find -d {maxDepth} {Quote(remoteRoot)}";
    }

    public static string BuildStat(string remotePath, bool fresh = false)
    {
        EnsureRemoteAbsolute(remotePath, nameof(remotePath));
        return $"{(fresh ? "recls" : "cls")} -ldB --time-style=long-iso {Quote(DashSafe(remotePath))}";
    }

    public static string BuildCreateDirectory(string remotePath)
    {
        EnsureRemoteAbsolute(remotePath, nameof(remotePath));
        if (remotePath == "/") throw new ArgumentException("The remote root already exists.", nameof(remotePath));
        return $"mkdir -p {Quote(DashSafe(remotePath))}";
    }

    public static string BuildMove(string sourcePath, string destinationPath)
    {
        EnsureRemoteAbsolute(sourcePath, nameof(sourcePath));
        EnsureRemoteAbsolute(destinationPath, nameof(destinationPath));
        if (sourcePath == "/" || destinationPath == "/") throw new ArgumentException("The remote root cannot be moved or replaced.");
        return $"mv {Quote(DashSafe(sourcePath))} {Quote(DashSafe(destinationPath))}";
    }

    public static string BuildDelete(string remotePath, bool isDirectory, bool recursive)
    {
        EnsureRemoteAbsolute(remotePath, nameof(remotePath));
        if (remotePath == "/") throw new ArgumentException("The remote root cannot be deleted.", nameof(remotePath));
        if (isDirectory) return recursive ? $"rm -r {Quote(DashSafe(remotePath))}" : $"rmdir {Quote(DashSafe(remotePath))}";
        return $"rm {Quote(DashSafe(remotePath))}";
    }

    public static string BuildTransfer(TransferPlan plan, bool background = true)
    {
        PlanValidator.Validate(plan);
        if (background && plan.Mode == TransferMode.Skip)
            throw new InvalidOperationException("Skip-mode transfers require foreground collision enforcement.");
        var command = BuildTransferCore(plan);

        if (background)
        {
            command = WithRateLimit(command, plan.RateLimitBytesPerSecond);
            return $"{command} &";
        }

        var commands = new List<string>();
        if (plan.RateLimitBytesPerSecond is { } rate) commands.Add($"set net:limit-rate {rate}:{rate}");
        if (plan.Direction == TransferDirection.Download && plan.Mode == TransferMode.Skip)
        {
            commands.Add("set xfer:use-temp-file no");
            commands.Add("set xfer:clobber no");
        }
        commands.Add(command);
        if (plan.Direction == TransferDirection.Download && plan.Mode == TransferMode.Skip)
        {
            commands.Add("set xfer:clobber yes");
            commands.Add("set xfer:use-temp-file yes");
        }
        if (plan.RateLimitBytesPerSecond is not null) commands.Add("set net:limit-rate 0:0");
        return string.Join("; ", commands);
    }

    public static string BuildDirectoryTransferPreview(TransferPlan plan)
    {
        PlanValidator.Validate(plan);
        if (plan.SourceKind != TransferSourceKind.Directory)
            throw new ArgumentException("A directory transfer plan is required for a mirror preview.", nameof(plan));
        return BuildDirectoryTransfer(plan, dryRun: true);
    }

    public static string BuildQueuedTransfer(
        TransferPlan plan,
        string aliasName,
        string successMarker,
        string failureMarker,
        string submissionSuccessMarker,
        string submissionFailureMarker)
    {
        PlanValidator.Validate(plan);
        EnsureMarker(aliasName, nameof(aliasName));
        EnsureMarker(successMarker, nameof(successMarker));
        EnsureMarker(failureMarker, nameof(failureMarker));
        EnsureMarker(submissionSuccessMarker, nameof(submissionSuccessMarker));
        EnsureMarker(submissionFailureMarker, nameof(submissionFailureMarker));
        if (new[] { aliasName, successMarker, failureMarker, submissionSuccessMarker, submissionFailureMarker }
            .Distinct(StringComparer.Ordinal).Count() != 5)
            throw new ArgumentException("The queue alias, submission markers, and completion markers must be distinct.");

        var setup = new List<string>();
        var cleanup = new List<string>();
        if (plan.RateLimitBytesPerSecond is { } rate)
        {
            setup.Add($"set net:limit-rate {rate}:{rate}");
            cleanup.Add("set net:limit-rate 0:0");
        }
        if (plan.Direction == TransferDirection.Download && plan.Mode == TransferMode.Skip)
        {
            setup.Add("set xfer:use-temp-file no");
            setup.Add("set xfer:clobber no");
            cleanup.Add("set xfer:clobber yes");
            cleanup.Add("set xfer:use-temp-file yes");
        }

        setup.Add(BuildTransferCore(plan));
        // LFTP 4.9.3 parses a compound expression supplied directly to `queue` only up to
        // its first conditional operator. Define the complete expression as an alias first,
        // then queue that simple alias name so the expression remains one parallel queue job.
        var success = cleanup.Append($"alias {aliasName}").Append($"echo {Quote(successMarker)}");
        var failure = cleanup.Append($"alias {aliasName}").Append($"echo {Quote(failureMarker)}");
        var aliasBody = $"( ( {string.Join(" && ", setup)} ) && ( {string.Join("; ", success)} ) || ( {string.Join("; ", failure)} ) )";
        var defineAlias = $"alias {aliasName} {Quote(aliasBody)}";
        return $"{defineAlias}; ( queue {aliasName} && echo {Quote(submissionSuccessMarker)} || echo {Quote(submissionFailureMarker)} )";
    }

    public static string BuildMirror(MirrorDefinition definition, bool dryRun)
    {
        PlanValidator.Validate(definition);
        var builder = new StringBuilder("mirror --verbose=1");
        if (dryRun) builder.Append(" --dry-run");
        if (definition.Direction == MirrorDirection.Upload) builder.Append(" --reverse");
        builder.Append(" --no-symlinks --overwrite");
        if (definition.DeleteExtraneous) builder.Append(" --delete");
        builder.Append(" --parallel=").Append(definition.ParallelFiles);
        if (definition.Direction == MirrorDirection.Download)
            builder.Append(" --use-pget-n=").Append(definition.SegmentsPerFile);
        foreach (var pattern in definition.EffectiveIncludes) builder.Append(" --include-glob ").Append(Quote(pattern));
        foreach (var pattern in definition.EffectiveExcludes) builder.Append(" --exclude-glob ").Append(Quote(pattern));
        var source = definition.Direction == MirrorDirection.Download ? DashSafe(definition.RemoteRoot) : ToMsysMirrorRoot(definition.LocalRoot);
        var destination = definition.Direction == MirrorDirection.Download ? ToMsysMirrorRoot(definition.LocalRoot) : DashSafe(definition.RemoteRoot);
        builder.Append(' ').Append(Quote(source)).Append(' ').Append(Quote(destination));
        if (definition.RateLimitBytesPerSecond is not { } rate) return builder.ToString();
        return $"set net:limit-rate {rate}:{rate}; {builder}; set net:limit-rate 0:0";
    }

    public static string BuildRemoteTransfer(RemoteTransferPlan plan)
    {
        PlanValidator.Validate(plan);
        if (plan.Mode != RemoteTransferMode.Fxp)
            throw new InvalidOperationException("Client relay requires distinct source and destination LFTP processes.");
        var source = SlotUrl("source", plan.SourcePath);
        var destination = SlotUrl("destination", plan.DestinationPath);
        var overwrite = plan.Overwrite ? " -e" : string.Empty;
        if (plan.Overwrite) return $"set ftp:use-fxp true; get{overwrite} {Quote(source)} -o {Quote(destination)}";
        return $"set ftp:use-fxp true; set xfer:use-temp-file no; set xfer:clobber no; get {Quote(source)} -o {Quote(destination)}; set xfer:clobber yes; set xfer:use-temp-file yes";
    }

    public static string BuildRemoteRelayDownload(string remotePath, string managedLocalPath)
    {
        EnsureRemoteAbsolute(remotePath, nameof(remotePath));
        EnsureFullyQualifiedLocalPath(managedLocalPath, nameof(managedLocalPath));
        return $"set xfer:use-temp-file no; get {Quote(DashSafe(remotePath))} -o {Quote(ToMsysPath(managedLocalPath))}; set xfer:use-temp-file yes";
    }

    public static string BuildRemoteRelayUpload(string managedLocalPath, string remotePath, bool overwrite)
    {
        EnsureFullyQualifiedLocalPath(managedLocalPath, nameof(managedLocalPath));
        EnsureRemoteAbsolute(remotePath, nameof(remotePath));
        var upload = $"put {Quote(ToMsysPath(managedLocalPath))} -o {Quote(DashSafe(remotePath))}";
        return overwrite
            ? upload
            : $"set xfer:use-temp-file no; set xfer:clobber no; {upload}; set xfer:clobber yes; set xfer:use-temp-file yes";
    }

    public static string BuildRemoteEditDownload(string remotePath, string managedLocalPath)
    {
        EnsureRemoteAbsolute(remotePath, nameof(remotePath));
        EnsureFullyQualifiedLocalPath(managedLocalPath, nameof(managedLocalPath));
        return $"get {Quote(DashSafe(remotePath))} -o {Quote(ToMsysPath(managedLocalPath))}";
    }

    public static string BuildRemoteEditUpload(string managedLocalPath, string remotePath)
    {
        EnsureFullyQualifiedLocalPath(managedLocalPath, nameof(managedLocalPath));
        EnsureRemoteAbsolute(remotePath, nameof(remotePath));
        return $"put {Quote(ToMsysPath(managedLocalPath))} -o {Quote(DashSafe(remotePath))}";
    }

    public static string DashSafe(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.StartsWith("-", StringComparison.Ordinal) ? $"./{value}" : value;
    }

    public static string ToMsysPath(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RejectProtocolControls(value, nameof(value));
        var normalized = value.Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal) || normalized.StartsWith("/", StringComparison.Ordinal)) return normalized;
        if (normalized.Length >= 2 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':')
        {
            var rest = normalized.Length == 2 ? "/" : normalized[2..];
            if (!rest.StartsWith("/", StringComparison.Ordinal)) rest = "/" + rest;
            return $"/{char.ToLowerInvariant(normalized[0])}{rest}";
        }
        return normalized;
    }

    private static string WithRateLimit(string command, long? bytesPerSecond) => bytesPerSecond is null
        ? command
        : $"set net:limit-rate {bytesPerSecond.Value}:{bytesPerSecond.Value}; {command}";

    private static string BuildTransferCore(TransferPlan plan)
    {
        if (plan.SourceKind == TransferSourceKind.Directory)
            return BuildDirectoryTransfer(plan);

        var modeOption = plan.Mode switch
        {
            TransferMode.Resume => " -c",
            TransferMode.Overwrite => " -e",
            _ => string.Empty,
        };
        if (plan.Direction == TransferDirection.Download)
        {
            // pget does not document get's -e option. Explicit overwrite therefore favors correct
            // delete-before-transfer semantics over segmentation for this mode.
            var verb = plan.Segments > 1 && plan.Mode != TransferMode.Overwrite ? $"pget -n {plan.Segments}" : "get";
            return $"{verb}{modeOption} {Quote(DashSafe(plan.SourcePath))} -o {Quote(ToMsysPath(plan.DestinationPath))}";
        }
        return $"put{modeOption} {Quote(ToMsysPath(plan.SourcePath))} -o {Quote(DashSafe(plan.DestinationPath))}";
    }

    private static string BuildDirectoryTransfer(TransferPlan plan, bool dryRun = false)
    {
        var builder = new StringBuilder("mirror");
        if (dryRun) builder.Append(" --verbose=1 --dry-run");
        if (plan.Direction == TransferDirection.Upload) builder.Append(" --reverse");
        if (plan.Mode == TransferMode.Resume) builder.Append(" --continue");
        builder.Append(" --no-symlinks --overwrite");
        if (plan.Direction == TransferDirection.Download && plan.Segments > 1)
            builder.Append(" --use-pget-n=").Append(plan.Segments);

        var source = plan.Direction == TransferDirection.Download ? DashSafe(plan.SourcePath) : ToMsysMirrorRoot(plan.SourcePath);
        var destination = plan.Direction == TransferDirection.Download ? ToMsysMirrorRoot(plan.DestinationPath) : DashSafe(plan.DestinationPath);
        return builder.Append(' ').Append(Quote(source)).Append(' ').Append(Quote(destination)).ToString();
    }

    private static string ToMsysMirrorRoot(string value)
    {
        var mapped = ToMsysPath(value);
        return mapped.Length > 1 ? mapped.TrimEnd('/') : mapped;
    }

    private static void EnsureMarker(string marker, string parameterName)
    {
        if (string.IsNullOrEmpty(marker) || marker.Length > 128 || marker.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("A queue marker may contain only 1-128 ASCII letters, digits, underscores, or hyphens.", parameterName);
    }

    private static string FormatHost(string host) => host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)
        ? $"[{host}]"
        : host;

    private static string EnsureRemoteDirectory(string path) => path == "/" || path.EndsWith("/", StringComparison.Ordinal) ? path : path + "/";

    private static string SlotUrl(string slot, string remotePath) => $"slot:{slot}{remotePath}";

    private static void EnsureRemoteAbsolute(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !path.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("A bounded absolute remote path is required.", parameterName);
        RejectProtocolControls(path, parameterName);
        if (path.Contains("//", StringComparison.Ordinal) ||
            path.Split('/', StringSplitOptions.None).Any(static segment => segment is "." or ".."))
            throw new ArgumentException("The remote path cannot contain empty, current-directory, or parent-directory segments.", parameterName);
    }

    private static void EnsureFullyQualifiedLocalPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A bounded fully qualified local path is required.", parameterName);
        RejectProtocolControls(path, parameterName);
    }

    private static void RejectProtocolControls(string value, string parameterName)
    {
        if (value.IndexOfAny(['\0', '\r', '\n']) >= 0)
            throw new ArgumentException("LFTP command values cannot contain NUL, CR, or LF characters.", parameterName);
    }
}
