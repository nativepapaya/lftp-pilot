using LFTPPilot.Core;
using LFTPPilot.Engine;

namespace LFTPPilot.Tests;

public sealed class RemoteSearchParserTests
{
    [Fact]
    public void ParsesLiteralUnicodeMatchesAndPreservesEntryKinds()
    {
        var search = Search("[FINAL]*");
        string[] lines =
        [
            "/srv/root/",
            "/srv/root/曲 Folder/",
            "/srv/root/曲 Folder/Report [final]*.TXT",
            "/srv/root/other.txt",
        ];

        var result = LftpOutputParser.ParseRemoteFindOutput(lines, search);

        Assert.Equal(3, result.ScannedEntries);
        Assert.False(result.WasLimited);
        var match = Assert.Single(result.Matches);
        Assert.Equal("Report [final]*.TXT", match.Name);
        Assert.Equal("/srv/root/曲 Folder/Report [final]*.TXT", match.FullPath);
        Assert.Equal(RemoteSearchEntryKind.Other, match.Kind);
        Assert.False(match.IsDirectory);

        var directory = LftpOutputParser.ParseRemoteFindOutput(lines, search with { Query = "曲 folder" });
        Assert.True(Assert.Single(directory.Matches).IsDirectory);
    }

    [Fact]
    public void PreservesWhitespaceAndHonorsOrdinalCaseSensitiveMatching()
    {
        var lines = new[] { "/srv/root/", "/srv/root/  Report\tFinal  .txt" };

        var exact = LftpOutputParser.ParseRemoteFindOutput(lines, Search("Report\tFinal", matchCase: true));
        var wrongCase = LftpOutputParser.ParseRemoteFindOutput(lines, Search("report\tfinal", matchCase: true));

        var match = Assert.Single(exact.Matches);
        Assert.Equal("  Report\tFinal  .txt", match.Name);
        Assert.Equal("/srv/root/  Report\tFinal  .txt", match.FullPath);
        Assert.Empty(wrongCase.Matches);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("/outside/file.txt")]
    [InlineData("/srv/rooted/file.txt")]
    [InlineData("/srv/root/../escape.txt")]
    [InlineData("/srv/root//ambiguous.txt")]
    [InlineData("/srv/root/directory//")]
    public void RejectsPathsOutsideTheExactCanonicalRoot(string invalidLine)
    {
        Assert.Throws<InvalidDataException>(() =>
            LftpOutputParser.ParseRemoteFindOutput(["/srv/root/", invalidLine], Search("file")));
    }

    [Fact]
    public void RejectsMissingOrRepeatedRootAndDuplicatePaths()
    {
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/file.txt"], Search("file")));
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/file.txt", "/srv/root/"], Search("file")));
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/", "/srv/root", "/srv/root/file.txt"], Search("file")));
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/", "/srv/root/file.txt", "/srv/root/file.txt"], Search("file")));
    }

    [Fact]
    public void EnforcesLockedFindMaxDepthSemantics()
    {
        var search = Search("child") with { MaxDepth = 2 };
        var direct = LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/", "/srv/root/child.txt"], search);

        Assert.Single(direct.Matches);
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/", "/srv/root/child/", "/srv/root/child/grandchild.txt"], search));
    }

    [Fact]
    public void RejectsNullEmptyControlledOversizedAndInvalidUnicodeLines()
    {
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            new string[] { "/srv/root/", null! }, Search("file")));
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/", ""], Search("file")));
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/", "/srv/root/bad\nfile"], Search("file")));
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/", "/srv/root/" + new string('a', RemoteSearchPolicy.MaximumOutputLineCharacters)], Search("file")));
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            ["/srv/root/", "/srv/root/\ud800"], Search("file")));
    }

    [Fact]
    public void CapsStoredMatchesButContinuesScanningValidatedOutput()
    {
        var result = LftpOutputParser.ParseRemoteFindOutput(
            LongMatchingPaths(500),
            Search("match-"));

        Assert.True(result.WasLimited);
        Assert.Equal(500, result.ScannedEntries);
        Assert.InRange(result.Matches.Length, 1, 499);
    }

    [Fact]
    public void CapsMatchCountAtTheSharedLimit()
    {
        var lines = Enumerable.Range(0, RemoteSearchPolicy.MaximumMatches + 1)
            .Select(index => $"/srv/root/match-{index:D5}.txt")
            .Prepend("/srv/root/");

        var result = LftpOutputParser.ParseRemoteFindOutput(lines, Search("match-"));

        Assert.True(result.WasLimited);
        Assert.Equal(RemoteSearchPolicy.MaximumMatches + 1, result.ScannedEntries);
        Assert.Equal(RemoteSearchPolicy.MaximumMatches, result.Matches.Length);
    }

    [Fact]
    public void RejectsOutputBeyondTheUtf8ByteBudget()
    {
        Assert.Throws<InvalidDataException>(() => LftpOutputParser.ParseRemoteFindOutput(
            Utf8HeavyPaths(5_000),
            Search("absent")));
    }

    [Fact]
    public void RecognizesFindFailuresFromStandardError()
    {
        var error = LftpOutputParser.FirstError(
            [new("stderr", "find: Access failed: No such file or directory")]);

        Assert.Equal("Access failed: No such file or directory", error);
    }

    private static RemoteSearchSpec Search(string query, bool matchCase = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "/srv/root", query, MatchCase: matchCase);

    private static IEnumerable<string> LongMatchingPaths(int count)
    {
        yield return "/srv/root/";
        for (var index = 0; index < count; index++)
        {
            var prefix = $"match-{index:D4}-";
            var padding = new string('曲', RemoteSearchPolicy.MaximumPathCharacters - "/srv/root/".Length - prefix.Length);
            yield return $"/srv/root/{prefix}{padding}";
        }
    }

    private static IEnumerable<string> Utf8HeavyPaths(int count)
    {
        yield return "/srv/root/";
        for (var index = 0; index < count; index++)
        {
            var prefix = $"{index:D5}-";
            var padding = new string('曲', RemoteSearchPolicy.MaximumPathCharacters - "/srv/root/".Length - prefix.Length);
            yield return $"/srv/root/{prefix}{padding}";
        }
    }
}
