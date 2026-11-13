using LocalAssistant.Digest;

namespace LocalAssistant.Tests;

public class DraftAllowlistTests
{
    private static readonly string[] Patterns = ["jane@firm.com", "@partner.org", "client.io"];

    [Theory]
    [InlineData("Jane Doe <Jane@Firm.com>")]
    [InlineData("\"Bob\" <bob@partner.org>")]
    [InlineData("anyone@client.io")]
    public void Matches_WhenSenderIsListedByAddressOrDomain_ShouldReturnTrue(string from)
    {
        Assert.True(DraftAllowlist.Matches(Patterns, from));
    }

    [Theory]
    [InlineData("John <john@firm.com>")]
    [InlineData("someone@mail.partner.org")]
    [InlineData("")]
    public void Matches_WhenSenderIsNotListed_ShouldReturnFalse(string from)
    {
        Assert.False(DraftAllowlist.Matches(Patterns, from));
    }

    [Fact]
    public void Matches_WhenAllowlistIsEmpty_ShouldReturnFalse()
    {
        Assert.False(DraftAllowlist.Matches([], "jane@firm.com"));
    }
}
