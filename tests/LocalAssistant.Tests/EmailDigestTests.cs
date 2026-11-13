using LocalAssistant.Digest;

namespace LocalAssistant.Tests;

public class EmailDigestTests
{
    private static DigestItem Item(string id, EmailPriority priority, string summary = "summary") => new()
    {
        Id = id,
        Sender = "sender@example.com",
        Subject = "subject",
        Summary = summary,
        Priority = priority,
    };

    private static EmailDigest Digest(bool llmAvailable, bool expandQuiet, params DigestItem[] items) => new()
    {
        EmailCount = items.Length,
        GeneratedAt = new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.Zero),
        LlmAvailable = llmAvailable,
        ExpandQuietEmails = expandQuiet,
        Items = items,
    };

    [Fact]
    public void NumberedItems_WhenTriaged_ShouldNumberActionThenFyiAndSkipCollapsedQuiet()
    {
        var digest = Digest(true, false,
            Item("fyi", EmailPriority.Fyi),
            Item("quiet", EmailPriority.Quiet),
            Item("action", EmailPriority.Action));

        Assert.Equal(
            new[] { (1, "action"), (2, "fyi") },
            digest.NumberedItems.Select(pair => (pair.Number, pair.Item.Id)));
    }

    [Fact]
    public void NumberedItems_WhenQuietIsExpanded_ShouldNumberQuietLast()
    {
        var digest = Digest(true, true,
            Item("quiet", EmailPriority.Quiet),
            Item("action", EmailPriority.Action));

        Assert.Equal(new[] { "action", "quiet" }, digest.NumberedItems.Select(pair => pair.Item.Id));
    }

    [Fact]
    public void NumberedItems_WhenOllamaWasUnreachable_ShouldKeepTheOriginalOrder()
    {
        var digest = Digest(false, false,
            Item("b", EmailPriority.Fyi),
            Item("a", EmailPriority.Fyi));

        Assert.Equal(new[] { "b", "a" }, digest.NumberedItems.Select(pair => pair.Item.Id));
    }

    [Fact]
    public void FullText_ShouldPrintTheSameNumbersAsNumberedItems()
    {
        var digest = Digest(true, false,
            Item("fyi", EmailPriority.Fyi, "an update"),
            Item("action", EmailPriority.Action, "please reply"));

        var text = digest.FullText;

        Assert.Contains("1. please reply", text);
        Assert.Contains("2. an update", text);
        Assert.True(text.IndexOf("NEEDS ACTION", StringComparison.Ordinal) < text.IndexOf("2. an update", StringComparison.Ordinal));
    }

    [Fact]
    public void TelegramHtml_ShouldEscapeSummariesAndLinkEachNumber()
    {
        var digest = Digest(true, false, Item("abc123", EmailPriority.Action, "Q&A <today>"));

        var html = digest.TelegramHtml;

        Assert.Contains("Q&amp;A &lt;today&gt;", html);
        Assert.Contains($"<a href=\"{EmailDigest.GmailInboxAllUrl}abc123\">1.</a>", html);
    }

    [Fact]
    public void FullText_WhenQuietIsCollapsed_ShouldShowOnlyItsCount()
    {
        var digest = Digest(true, false,
            Item("q1", EmailPriority.Quiet, "weekly promo"),
            Item("q2", EmailPriority.Quiet, "receipt"));

        var text = digest.FullText;

        Assert.Contains("Quiet: 2 hidden", text);
        Assert.DoesNotContain("weekly promo", text);
    }
}
