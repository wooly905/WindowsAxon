using WindowsAxon.Mesh;

namespace WindowsAxon.Agent.Tests.Mesh;

public sealed class PeerMentionParserTests
{
    [Fact]
    public void MatchesPeerNameWithSpacesAndComma()
    {
        var matched = PeerMentionParser.TryParse("@computer B , show me your harddrive free space",
                                                 ["computer A", "computer B"],
                                                 out var target,
                                                 out var prompt);

        Assert.True(matched);
        Assert.Equal("computer B", target);
        Assert.Equal("show me your harddrive free space", prompt);
    }

    [Fact]
    public void MatchesLongestPeerNameFirstCaseInsensitive()
    {
        var matched = PeerMentionParser.TryParse("@lab-pc-2: check disk",
                                                 ["lab-pc", "Lab-PC-2"],
                                                 out var target,
                                                 out var prompt);

        Assert.True(matched);
        Assert.Equal("Lab-PC-2", target);
        Assert.Equal("check disk", prompt);
    }

    [Fact]
    public void FallsBackToFirstTokenForOfflinePeer()
    {
        var matched = PeerMentionParser.TryParse("@OfflinePC show disk usage",
                                                 ["OnlinePC"],
                                                 out var target,
                                                 out var prompt);

        Assert.True(matched);
        Assert.Equal("OfflinePC", target);
        Assert.Equal("show disk usage", prompt);
    }

    [Theory]
    [InlineData("show disk usage")]
    [InlineData("email me@example.com")]
    [InlineData("@")]
    [InlineData("@ not a mention")]
    public void IgnoresNonMentionInput(string input)
    {
        var matched = PeerMentionParser.TryParse(input, ["computer B"], out _, out _);

        Assert.False(matched);
    }
}

