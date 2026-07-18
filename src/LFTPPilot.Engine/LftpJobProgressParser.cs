using System.Collections.Immutable;
using System.Globalization;

namespace LFTPPilot.Engine;

public sealed record LftpTransferProgressObservation(
    string SourcePath,
    long BytesTransferred,
    long? TotalBytes,
    int Percent,
    double? BytesPerSecond)
{
    public bool IsSegmented => TotalBytes is not null;
}

public static class LftpJobProgressParser
{
    private const int MaximumStatusLines = 1_024;
    private const int MaximumStatusLineCharacters = 32_768;

    public static ImmutableArray<LftpTransferProgressObservation> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var observations = ImmutableArray.CreateBuilder<LftpTransferProgressObservation>();
        var lineCount = 0;
        var skipSegmentPosition = false;
        foreach (var line in lines)
        {
            if (line is null) throw new InvalidDataException("LFTP job status contained a null line.");
            lineCount++;
            if (lineCount > MaximumStatusLines)
                throw new InvalidDataException("LFTP job status exceeded its line limit.");
            if (line.Length > MaximumStatusLineCharacters || line.IndexOfAny(['\0', '\r', '\n']) >= 0)
                throw new InvalidDataException("LFTP job status contained an invalid or oversized line.");
            var trimmed = line.AsSpan().TrimStart();
            if (trimmed.StartsWith("\\chunk ", StringComparison.Ordinal))
            {
                skipSegmentPosition = true;
                continue;
            }
            if (TryParse(line, out var observation))
            {
                if (!(skipSegmentPosition && !observation.IsSegmented)) observations.Add(observation);
                skipSegmentPosition = false;
            }
            else if (!trimmed.IsEmpty)
            {
                skipSegmentPosition = false;
            }
        }
        return observations.ToImmutable();
    }

    private static bool TryParse(string line, out LftpTransferProgressObservation observation)
    {
        observation = null!;
        var trimmed = line.AsSpan().TrimStart();
        if (trimmed.Length < 8 || trimmed[0] != '`') return false;

        const string segmentedMarker = "', got ";
        const string positionMarker = "' at ";
        var markerIndex = trimmed.IndexOf(segmentedMarker, StringComparison.Ordinal);
        var segmented = markerIndex > 1;
        if (!segmented)
        {
            markerIndex = trimmed.IndexOf(positionMarker, StringComparison.Ordinal);
            if (markerIndex <= 1) return false;
        }

        var sourcePath = trimmed[1..markerIndex].ToString();
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.Length > 32_767 || sourcePath.Any(char.IsControl))
            throw new InvalidDataException("LFTP job status contained an invalid source path.");
        var remainder = trimmed[(markerIndex + (segmented ? segmentedMarker.Length : positionMarker.Length))..];

        long bytesTransferred;
        long? totalBytes = null;
        if (segmented)
        {
            var ofIndex = remainder.IndexOf(" of ", StringComparison.Ordinal);
            if (ofIndex <= 0 || !TryReadUnsigned(remainder[..ofIndex], out bytesTransferred)) return false;
            remainder = remainder[(ofIndex + 4)..];
            var totalEnd = remainder.IndexOf(' ');
            if (totalEnd <= 0 || !TryReadUnsigned(remainder[..totalEnd], out var parsedTotal)) return false;
            totalBytes = parsedTotal;
            remainder = remainder[totalEnd..];
        }
        else
        {
            var bytesEnd = remainder.IndexOf(' ');
            if (bytesEnd <= 0 || !TryReadUnsigned(remainder[..bytesEnd], out bytesTransferred)) return false;
            remainder = remainder[bytesEnd..];
        }

        remainder = remainder.TrimStart();
        if (remainder.Length < 4 || remainder[0] != '(') return false;
        var percentEnd = remainder.IndexOf("%)", StringComparison.Ordinal);
        if (percentEnd <= 1 || !int.TryParse(
                remainder[1..percentEnd],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var percent) || percent is < 0 or > 100)
        {
            return false;
        }
        remainder = remainder[(percentEnd + 2)..].TrimStart();
        var bytesPerSecond = TryReadRate(remainder);
        if (bytesTransferred < 0 || totalBytes is <= 0 || totalBytes is { } total && bytesTransferred > total)
            throw new InvalidDataException("LFTP job status contained inconsistent byte counts.");
        observation = new(sourcePath, bytesTransferred, totalBytes, percent, bytesPerSecond);
        return true;
    }

    private static bool TryReadUnsigned(ReadOnlySpan<char> value, out long result) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 0;

    private static double? TryReadRate(ReadOnlySpan<char> value)
    {
        var tokenEnd = value.IndexOf(' ');
        var token = tokenEnd < 0 ? value : value[..tokenEnd];
        if (!token.EndsWith("/s", StringComparison.Ordinal) || token.Length < 3) return null;
        token = token[..^2];
        var multiplier = 1d;
        if (token.Length > 0 && token[^1] is 'K' or 'M' or 'G' or 'T' or 'P')
        {
            multiplier = token[^1] switch
            {
                'K' => 1024d,
                'M' => 1024d * 1024,
                'G' => 1024d * 1024 * 1024,
                'T' => 1024d * 1024 * 1024 * 1024,
                _ => 1024d * 1024 * 1024 * 1024 * 1024,
            };
            token = token[..^1];
        }
        return double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) &&
            double.IsFinite(rate) && rate >= 0
            ? rate * multiplier
            : null;
    }
}
