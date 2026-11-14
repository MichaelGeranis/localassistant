using Google.Apis.Gmail.v1;
using LocalAssistant.Chat;
using LocalAssistant.Configuration;
using LocalAssistant.Diagnostics;
using LocalAssistant.Digest;
using LocalAssistant.Storage;
using Ollama.Apis.v1;
using Telegram.Apis.v1;

if (args.Length > 0 && string.Equals(args[0], "secrets", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = SecretsCommand.Run(args[1..], SecretSource.Default, Console.Out);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "serve", StringComparison.OrdinalIgnoreCase))
{
    RunLog.Append("Chat service run started.");
    try
    {
        await ChatLoop.RunAsync(Settings.Load());
        RunLog.Append("Chat service exited normally.");
    }
    catch (Exception ex)
    {
        RunLog.Append($"Chat service FAILED: {ex}");
        await TryAlertFailure(ex);
        throw;
    }
    return;
}

RunLog.Append("Run started.");

try
{
    var settings = Settings.Load();
    var gmailClient = new GMailServiceClient(digestLabel: settings.EmailDigestLabel);
    var ollamaClient = new OllamaServiceClient { OllamaUrl = settings.OllamaUrl };

    Console.WriteLine("Fetching and summarizing unread emails...\n");
    var digest = await EmailDigestService.BuildAsync(gmailClient, ollamaClient, settings);

    Console.WriteLine("===== DIGEST =====");
    Console.WriteLine(digest.FullText);
    RunLog.Append($"Digest built: {digest.EmailCount} unread email(s), Ollama available: {digest.LlmAvailable}.");

    var (delivered, channel) = await SendTelegramDigestAsync(digest, settings.Telegram.DeleteAfterHours);

    await EmailDigestStore.AppendAsync(digest, channel, delivered, settings.History.RetentionDays);

    if (delivered)
    {
        await LastEmailDigestStore.SaveAsync(digest);
    }

    await MarkEmailsAsDigestedAsync(gmailClient, digest, delivered, settings.EmailDigestLabel);

    RunLog.Append("Run completed.");
}
catch (Exception ex)
{
    RunLog.Append($"Run FAILED: {ex}");
    await TryAlertFailure(ex);
    throw;
}

// Best-effort "the scheduled run crashed" ping to messenger app, so an unattended failure is not silent.
static async Task TryAlertFailure(Exception ex)
{
    try
    {
        TelegramServiceClient client;
        try
        {
            client = new TelegramServiceClient();
        }
        catch (InvalidOperationException)
        {
            return; // Telegram not configured - nowhere to send an alert.
        }

        // The exception's type only: its message can quote what it was working on (a subject,
        // a note), and the alert is stored on Telegram's servers. The run log has the details.
        var html =
            "<b>⚠️ Email digest run failed</b>\n"
            + $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm} on {HtmlEscape(Environment.MachineName)}\n\n"
            + $"{HtmlEscape(ex.GetType().Name)}\n\n"
            + "Full trace in digest-run-log.txt.";

        await client.SendHtml(html);
        RunLog.Append("Failure alert sent to Telegram.");
    }
    catch (Exception alertEx)
    {
        RunLog.Append($"Failure alert could not be sent: {alertEx.Message}");
    }

    static string HtmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}

// Returns whether the digest reached its destination, and which destination that was
static async Task<(bool Delivered, string Channel)> SendTelegramDigestAsync(EmailDigest digest, int deleteAfterHours)
{
    TelegramServiceClient? telegramClient = null;
    try
    {
        telegramClient = new TelegramServiceClient();
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine($"Skipped Telegram (not configured): {ex.Message}");
        RunLog.Append($"Telegram skipped (not configured): {ex.Message}");
        return (true, EmailDigestStore.ConsoleChannel);
    }

    if (deleteAfterHours > 0)
    {
        // Earlier digests past their time go first; this one is remembered for a later run
        await TelegramMessageLog.Default.DeleteExpiredAsync(telegramClient, deleteAfterHours, DateTimeOffset.Now);
        telegramClient.MessageSent = id => TelegramMessageLog.Default.Record(id, DateTimeOffset.Now);
    }

    try
    {
        await telegramClient.SendLongHtml(digest.TelegramHtml);
        Console.WriteLine("Telegram digest sent.");
        RunLog.Append("Telegram digest sent.");
        return (true, EmailDigestStore.TelegramChannel);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to send Telegram digest: {ex.Message}");
        RunLog.Append($"Telegram send FAILED: {ex.Message}");
        return (false, EmailDigestStore.TelegramChannel);
    }
}

static async Task MarkEmailsAsDigestedAsync(GMailServiceClient gmailClient, EmailDigest digest, bool delivered, string digestedLabelName)
{
    if (delivered && digest.LlmAvailable && digest.EmailIds.Count > 0)
    {
        try
        {
            await gmailClient.MarkDigestedAsync(digest.EmailIds, digestedLabelName);
            Console.WriteLine($"Marked {digest.EmailIds.Count} message(s) as '{digestedLabelName}'.");
            RunLog.Append($"Marked {digest.EmailIds.Count} message(s) as '{digestedLabelName}'.");
        }
        catch (Exception ex)
        {
            // Non-fatal: worst case the same emails appear in the next digest.
            Console.WriteLine($"Failed to apply '{digestedLabelName}' label: {ex.Message}");
            RunLog.Append($"Labeling FAILED (digest still delivered): {ex.Message}");
        }
    }
    else if (digest.EmailIds.Count > 0)
    {
        var reason = !delivered ? "not delivered" : "Ollama unavailable";
        Console.WriteLine($"Skipped labeling ({reason}); {digest.EmailIds.Count} message(s) will be retried next run.");
        RunLog.Append($"Skipped labeling ({reason}); {digest.EmailIds.Count} message(s) will be retried next run.");
    }
}