using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAssistant.Diagnostics;
using LocalAssistant.Digest;

namespace LocalAssistant.Storage
{
    /// <summary>
    /// An append-only record of every digest the app has produced, at
    /// <c>%LOCALAPPDATA%\LocalAssistant\digest-history.jsonl</c> - next to the run log and
    /// settings.json, so it survives a republish of <c>bin/Publish</c>.
    /// 
    /// May contain private information. Should be treated as sensitive, and not shared with anyone who shouldn't see the email subjects and summaries.
    /// </summary>
    public static class EmailDigestStore
    {
        public const int CurrentSchemaVersion = 1;

        public const string ConsoleChannel = "console";

        public const string TelegramChannel = "telegram";

        public static string FilePath => AppPaths.DigestHistoryFile;

        /// <summary>
        /// Appends one line for this run.
        /// </summary>
        public static async Task AppendAsync(EmailDigest digest, string channel, bool delivered)
        {
            try
            {
                var record = new DigestRunRecord
                {
                    SchemaVersion = CurrentSchemaVersion,
                    GeneratedAt = digest.GeneratedAt,
                    Channel = channel,
                    Delivered = delivered,
                    LlmAvailable = digest.LlmAvailable,
                    EmailCount = digest.EmailCount,
                    Message = channel == TelegramChannel ? digest.TelegramHtml : digest.FullText,
                    Items = digest.Items.Select(item => new DigestItemRecord
                    {
                        Id = item.Id,
                        Sender = item.Sender,
                        Subject = item.Subject,
                        Summary = item.Summary,
                        Priority = item.Priority.ToString(),
                        DraftUrl = item.DraftUrl
                    }).ToList()
                };

                // WriteIndented is off in the context below - one record must be one line, or the
                // file stops being JSON Lines. Newlines inside the summaries are escaped by the
                // serializer, so they can't break a line either.
                var line = JsonSerializer.Serialize(record, EmailDigestStoreJsonContext.Default.DigestRunRecord);

                AppPaths.EnsureRoot();
                await File.AppendAllTextAsync(FilePath, line + Environment.NewLine);

                RunLog.Append($"Digest recorded to {Path.GetFileName(FilePath)} ({digest.Items.Count} item(s), channel {channel}, delivered {delivered}).");
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not record the digest to history ({ex.Message}); continuing.");
            }
        }
    }

    /// <summary>One digest run, as stored on one line of the history file.</summary>
    public sealed class DigestRunRecord
    {
        public int SchemaVersion { get; set; }

        public DateTimeOffset GeneratedAt { get; set; }

        public string Channel { get; set; } = string.Empty;

        public bool Delivered { get; set; }

        public bool LlmAvailable { get; set; }

        public int EmailCount { get; set; }

        public string Message { get; set; } = string.Empty;

        public List<DigestItemRecord> Items { get; set; } = new();
    }

    /// <summary>One summarized, triaged email inside a stored run.</summary>
    public sealed class DigestItemRecord
    {
        public string Id { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;

        /// <summary>The <see cref="EmailPriority"/> name: <c>Action</c>, <c>Fyi</c> or <c>Quiet</c>.</summary>
        public string Priority { get; set; } = string.Empty;

        public string? DraftUrl { get; set; }
    }

    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(DigestRunRecord))]
    internal partial class EmailDigestStoreJsonContext : JsonSerializerContext
    {
    }
}
