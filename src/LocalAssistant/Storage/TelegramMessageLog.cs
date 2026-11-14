using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAssistant.Diagnostics;
using LocalAssistant.Integrations.Telegram;

namespace LocalAssistant.Storage
{
    /// <summary>
    /// The ids of the messages in the Telegram chat - digests, alerts, chat replies and what was
    /// typed to the bot - so they can be deleted once they are older than
    /// <c>telegram.deleteAfterHours</c>. Telegram keeps a chat on its servers (bot chats are not
    /// end-to-end encrypted), so this keeps email summaries and notes from piling up there.
    ///
    /// Telegram lets a bot delete messages only for 48 hours after they were sent, so an entry it
    /// refuses after that is dropped rather than retried forever.
    /// </summary>
    public sealed class TelegramMessageLog
    {
        /// <summary>Telegram's limit for a bot deleting a message, less an hour of margin.</summary>
        public const int MaxDeleteAfterHours = 47;

        private static readonly TimeSpan TelegramDeleteWindow = TimeSpan.FromHours(48);

        private readonly string _path;
        private readonly object _gate = new();

        public TelegramMessageLog(string path)
        {
            _path = path;
        }

        public static TelegramMessageLog Default { get; } = new(AppPaths.TelegramMessagesFile);

        /// <summary>Remembers a message. Never throws: a bookkeeping failure must not cost a reply.</summary>
        public void Record(long messageId, DateTimeOffset sentAt)
        {
            try
            {
                lock (_gate)
                {
                    var entries = Load();
                    entries.Add(new TelegramMessageEntry { MessageId = messageId, SentAt = sentAt });
                    Save(entries);
                }
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not record a Telegram message id ({ex.GetType().Name}); it will not be deleted automatically.");
            }
        }

        /// <summary>
        /// Which entries to delete now, and which to keep for later. An entry past Telegram's
        /// 48-hour window can no longer be deleted, so it is neither: it is forgotten.
        /// </summary>
        public static (IReadOnlyList<TelegramMessageEntry> Due, IReadOnlyList<TelegramMessageEntry> Keep) Partition(
            IEnumerable<TelegramMessageEntry> entries, DateTimeOffset now, int deleteAfterHours)
        {
            var due = new List<TelegramMessageEntry>();
            var keep = new List<TelegramMessageEntry>();
            foreach (var entry in entries)
            {
                var age = now - entry.SentAt;
                if (age >= TelegramDeleteWindow)
                {
                    continue;
                }
                (age >= TimeSpan.FromHours(deleteAfterHours) ? due : keep).Add(entry);
            }
            return (due, keep);
        }

        /// <summary>Deletes every remembered message older than <paramref name="deleteAfterHours"/>.</summary>
        public async Task DeleteExpiredAsync(TelegramServiceClient client, int deleteAfterHours, DateTimeOffset now)
        {
            if (deleteAfterHours <= 0)
            {
                return;
            }

            try
            {
                List<TelegramMessageEntry> entries;
                lock (_gate)
                {
                    entries = Load();
                }

                var (due, _) = Partition(entries, now, deleteAfterHours);
                if (due.Count == 0)
                {
                    return;
                }

                var deleted = 0;
                foreach (var entry in due)
                {
                    try
                    {
                        if (await client.TryDeleteMessageAsync(entry.MessageId))
                        {
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        RunLog.Append($"Deleting a Telegram message failed ({ex.GetType().Name}); will retry.");
                        return;
                    }
                }

                lock (_gate)
                {
                    // Re-read: the chat loop may have recorded messages while these were deleted
                    var dueIds = due.Select(e => e.MessageId).ToHashSet();
                    var (_, keep) = Partition(Load().Where(e => !dueIds.Contains(e.MessageId)), now, deleteAfterHours);
                    Save(keep.ToList());
                }

                RunLog.Append($"Deleted {deleted} of {due.Count} Telegram message(s) older than {deleteAfterHours}h.");
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not delete old Telegram messages ({ex.GetType().Name}); continuing.");
            }
        }

        private List<TelegramMessageEntry> Load()
        {
            if (!File.Exists(_path))
            {
                return new List<TelegramMessageEntry>();
            }
            return JsonSerializer.Deserialize(File.ReadAllText(_path), TelegramMessageLogJsonContext.Default.ListTelegramMessageEntry)
                ?? new List<TelegramMessageEntry>();
        }

        private void Save(List<TelegramMessageEntry> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, TelegramMessageLogJsonContext.Default.ListTelegramMessageEntry));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public sealed class TelegramMessageEntry
    {
        public long MessageId { get; set; }

        public DateTimeOffset SentAt { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(List<TelegramMessageEntry>))]
    internal partial class TelegramMessageLogJsonContext : JsonSerializerContext
    {
    }
}
