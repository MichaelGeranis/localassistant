using LocalAssistant.Chat;

namespace LocalAssistant.Tests;

public class ChatCommandRouterTests
{
    [Theory]
    [InlineData("Gmail: mark 1 as read", "Gmail", "mark 1 as read")]
    [InlineData("  obsidian :  buy milk ", "Obsidian", "buy milk")]
    [InlineData("Obsidian: meeting at 10:30, see https://example.com", "Obsidian", "meeting at 10:30, see https://example.com")]
    public void TryParse_WhenTextStartsWithAKnownPrefix_ShouldReturnTargetAndBody(string text, string target, string body)
    {
        Assert.True(ChatCommandRouter.TryParse(text, out var parsedTarget, out var parsedBody));
        Assert.Equal(target, parsedTarget.ToString());
        Assert.Equal(body, parsedBody);
    }

    [Theory]
    [InlineData("What time is it?")]
    [InlineData("Note: remember this")]
    [InlineData(": no prefix")]
    [InlineData("tell me about gmail: is it good")]
    public void TryParse_WhenTextHasNoKnownPrefix_ShouldLeaveItToChat(string text)
    {
        Assert.False(ChatCommandRouter.TryParse(text, out _, out _));
    }
}

public class MarkReadCommandTests
{
    [Fact]
    public void TryParse_WhenShortcutNamesNumbers_ShouldReturnThem()
    {
        Assert.True(MarkReadCommand.TryParse("/read 1 3-4", out var numbers));
        Assert.Equal(new[] { 1, 3, 4 }, numbers);
    }

    [Theory]
    [InlineData("/read")]
    [InlineData("read 1 2")]
    [InlineData("please /read 1")]
    public void TryParse_WhenNotTheShortcutWithNumbers_ShouldReturnFalse(string text)
    {
        Assert.False(MarkReadCommand.TryParse(text, out _));
    }
}

public class VaultDigestCommandTests
{
    [Theory]
    [InlineData("digest")]
    [InlineData("  Summarize the vault! ")]
    [InlineData("vault digest.")]
    public void IsDigestRequest_WhenBodyIsADigestPhrase_ShouldReturnTrue(string body)
    {
        Assert.True(VaultDigestCommand.IsDigestRequest(body));
    }

    [Theory]
    [InlineData("digest the pitch section first")]
    [InlineData("buy milk")]
    [InlineData("")]
    public void IsDigestRequest_WhenBodyIsANote_ShouldReturnFalse(string body)
    {
        Assert.False(VaultDigestCommand.IsDigestRequest(body));
    }
}
