using LocalAssistant.Configuration;
using LocalAssistant.Digest;
using LocalAssistant.Storage;

namespace LocalAssistant.Tests;

/// <summary>
/// What leaves the machine and how long it stays: the minimal Telegram digest, deleting old
/// Telegram messages, the history's retention, and keeping the model on this machine.
/// </summary>
public class PrivacySettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private static EmailDigest Digest(bool minimal, bool llmAvailable = true) => new()
    {
        EmailCount = 2,
        GeneratedAt = Now,
        LlmAvailable = llmAvailable,
        MinimalTelegram = minimal,
        Items =
        [
            new DigestItem { Id = "m1", Sender = "boss@firm.com", Subject = "Layoffs", Summary = "Confidential: layoffs next week", Priority = EmailPriority.Action },
            new DigestItem { Id = "m2", Sender = "bank@example.com", Subject = "Statement", Summary = "Your balance is 1,234", Priority = EmailPriority.Fyi },
        ],
    };

    [Fact]
    public void TelegramHtml_WhenMinimal_ShouldCarryNoSenderSubjectOrSummary()
    {
        var html = Digest(minimal: true).TelegramHtml;

        foreach (var text in new[] { "layoffs", "Layoffs", "balance", "boss@firm.com", "Statement" })
        {
            Assert.DoesNotContain(text, html);
        }
        Assert.Contains("2 unread", html);
        Assert.Contains($"{EmailDigest.GmailInboxAllUrl}m1\">1.</a>", html);
        Assert.Contains($"{EmailDigest.GmailInboxAllUrl}m2\">2.</a>", html);
    }

    [Fact]
    public void TelegramHtml_WhenMinimalAndOllamaUnreachable_ShouldStillCarryNoSummaries()
    {
        var html = Digest(minimal: true, llmAvailable: false).TelegramHtml;

        Assert.DoesNotContain("layoffs", html);
        Assert.Contains("\">1.</a>", html);
    }

    [Fact]
    public void FullText_WhenMinimal_ShouldKeepSummariesForTheLocalConsole()
    {
        Assert.Contains("Confidential: layoffs next week", Digest(minimal: true).FullText);
    }

    [Fact]
    public void NumberedItems_ShouldNotDependOnTheDetailLevel()
    {
        Assert.Equal(
            Digest(minimal: false).NumberedItems.Select(p => (p.Number, p.Item.Id)),
            Digest(minimal: true).NumberedItems.Select(p => (p.Number, p.Item.Id)));
    }

    [Fact]
    public void Partition_ShouldDeleteExpiredKeepRecentAndForgetPastTelegramsWindow()
    {
        var entries = new[]
        {
            new TelegramMessageEntry { MessageId = 1, SentAt = Now.AddHours(-2) },
            new TelegramMessageEntry { MessageId = 2, SentAt = Now.AddHours(-30) },
            new TelegramMessageEntry { MessageId = 3, SentAt = Now.AddHours(-49) },
        };

        var (due, keep) = TelegramMessageLog.Partition(entries, Now, deleteAfterHours: 24);

        Assert.Equal(new long[] { 2 }, due.Select(e => e.MessageId));
        Assert.Equal(new long[] { 1 }, keep.Select(e => e.MessageId));
    }

    [Fact]
    public void Record_ShouldPersistIdsAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"la-messages-{Guid.NewGuid():N}.json");
        try
        {
            new TelegramMessageLog(path).Record(7, Now.AddHours(-30));
            new TelegramMessageLog(path).Record(8, Now);

            var json = File.ReadAllText(path);
            Assert.Contains("\"messageId\":7", json);
            Assert.Contains("\"messageId\":8", json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PruneOlderThan_ShouldDropOldDigestsAndKeepUnreadableLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"la-history-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                """{"generatedAt":"2026-01-01T10:00:00+00:00","items":[{"subject":"old"}]}""",
                "not json",
                """{"generatedAt":"2026-11-13T10:00:00+00:00","items":[{"subject":"recent"}]}""",
            ]);

            var removed = EmailDigestStore.PruneOlderThan(path, Now.AddDays(-90));

            Assert.Equal(1, removed);
            var remaining = File.ReadAllText(path);
            Assert.DoesNotContain("old", remaining);
            Assert.Contains("recent", remaining);
            Assert.Contains("not json", remaining);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("http://192.168.1.20:11434", false)]
    [InlineData("http://ollama.example.com", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("not a url", false)]
    public void IsLocalUrl_ShouldAcceptOnlyThisMachine(string url, bool expected)
    {
        Assert.Equal(expected, Settings.IsLocalUrl(url));
    }

    [Fact]
    public void TelegramSettings_ShouldClampDeleteAfterHoursToTelegramsLimit()
    {
        var settings = new TelegramSettings { DeleteAfterHours = 100, DigestDetail = "everything" };

        settings.Normalize();

        Assert.Equal(TelegramMessageLog.MaxDeleteAfterHours, settings.DeleteAfterHours);
        Assert.Equal(TelegramSettings.FullDetail, settings.DigestDetail);
    }
}
