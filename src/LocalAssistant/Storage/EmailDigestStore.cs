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
        /// Appends one line for this run, then removes lines older than
        /// <paramref name="retentionDays"/>. With 0, records nothing and deletes the history.
        /// </summary>
        public static async Task AppendAsync(EmailDigest digest, string channel, bool delivered, int retentionDays)
        {
            try
            {
                if (retentionDays <= 0)
                {
                    if (File.Exists(FilePath))
                    {
                        File.Delete(FilePath);
                        RunLog.Append($"Digest history is off (history.retentionDays = 0); deleted {Path.GetFileName(FilePath)}.");
                    }
                    return;
                }

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
                var removed = PruneOlderThan(FilePath, digest.GeneratedAt - TimeSpan.FromDays(retentionDays));
                if (removed > 0)
                {
                    RunLog.Append($"Removed {removed} digest(s) older than {retentionDays} days from history.");
                }

                RunLog.Append($"Digest recorded to {Path.GetFileName(FilePath)} ({digest.Items.Count} item(s), channel {channel}, delivered {delivered}).");
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not record the digest to history ({ex.GetType().Name}); continuing.");
            }
        }

        /// <summary>
        /// Keeps the lines generated at or after <paramref name="cutoff"/>; a line that cannot be
        /// read is kept rather than lost. Rewrites the file only when something is removed.
        /// </summary>
        /// <returns>How many lines were removed</returns>
        internal static int PruneOlderThan(string path, DateTimeOffset cutoff)
        {
            var lines = File.ReadAllLines(path);
            var kept = lines.Where(line => !IsOlderThan(line, cutoff)).ToList();
            var removed = lines.Length - kept.Count;
            if (removed > 0)
            {
                var temp = path + ".tmp";
                File.WriteAllLines(temp, kept);
                File.Move(temp, path, overwrite: true);
            }
            return removed;
        }

        private static bool IsOlderThan(string line, DateTimeOffset cutoff)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return true;
            }
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.TryGetProperty("generatedAt", out var at)
                    && at.TryGetDateTimeOffset(out var generatedAt)
                    && generatedAt < cutoff;
            }
            catch (JsonException)
            {
                return false;
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
