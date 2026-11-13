using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Telegram.Apis.v1
{
    /// <summary>
    /// Hand-rolled Telegram Bot API client (not a NuGet package). 
    /// </summary>
    public class TelegramServiceClient
    {
        private const string ApiBaseUrl = "https://api.telegram.org/bot";

        /// <summary>Stay well under Telegram's 4096-char hard limit.</summary>
        private const int MaxMessageLength = 3800;

        private static readonly TimeSpan DelayBetweenChunks = TimeSpan.FromSeconds(1);

        private readonly string _botToken; // provided by the user or from TELEGRAM_BOT_TOKEN env var
        private readonly string _chatId; // provided by the user or from TELEGRAM_CHAT_ID env var
        private readonly HttpClient _httpClient = new();

        /// <summary>
        /// The authorized chat id. Chat passthrough must reject any update whose chat id doesn't
        /// match this - see <see cref="ChatId"/>.
        /// </summary>
        public string ChatId => _chatId;

        public TelegramServiceClient(string? botToken = null, string? chatId = null)
        {
            botToken ??= Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
                ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN environment variable is not set.");
            chatId ??= Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")
                ?? throw new InvalidOperationException("TELEGRAM_CHAT_ID environment variable is not set.");

            botToken = botToken.Trim();
            chatId = chatId.Trim();

            if (string.IsNullOrWhiteSpace(botToken))
            {
                throw new ArgumentException("A Telegram bot token is required.", nameof(botToken));
            }
            if (string.IsNullOrWhiteSpace(chatId))
            {
                throw new ArgumentException("A Telegram chat id is required.", nameof(chatId));
            }

            _botToken = botToken;
            _chatId = chatId;
        }

        /// <summary>
        /// Sends <paramref name="html"/> as one or more HTML messages.
        /// </summary>
        public async Task SendLongHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                throw new ArgumentException("Message text cannot be null or empty.", nameof(html));
            }

            var chunks = SplitIntoChunks(html, MaxMessageLength);
            var failures = new List<string>();

            for (var i = 0; i < chunks.Count; i++)
            {
                try
                {
                    await SendHtml(chunks[i]);
                }
                catch (Exception ex)
                {
                    failures.Add($"({i + 1}/{chunks.Count}): {ex.Message}");
                }

                if (i < chunks.Count - 1)
                {
                    await Task.Delay(DelayBetweenChunks);
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{failures.Count} of {chunks.Count} Telegram message(s) failed: {string.Join(" | ", failures)}");
            }
        }

        /// <summary>Sends a single HTML message and verifies the ok field in the response.</summary>
        public Task SendHtml(string html) => SendMessage(html, parseModeHtml: true, toChatId: null);

        /// <summary>
        /// Sends text as one or more plain-text messages (no HTML parsing).
        /// </summary>
        public async Task SendLongText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Message text cannot be null or empty.", nameof(text));
            }

            var chunks = SplitIntoChunks(text, MaxMessageLength);
            var failures = new List<string>();

            for (var i = 0; i < chunks.Count; i++)
            {
                try
                {
                    await SendText(chunks[i]);
                }
                catch (Exception ex)
                {
                    failures.Add($"({i + 1}/{chunks.Count}): {ex.Message}");
                }

                if (i < chunks.Count - 1)
                {
                    await Task.Delay(DelayBetweenChunks);
                }
            }

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{failures.Count} of {chunks.Count} Telegram message(s) failed: {string.Join(" | ", failures)}");
            }
        }

        /// <summary>Sends a single plain-text message (no HTML parsing) and verifies the ok field in the response.</summary>
        public Task SendText(string text, string? toChatId = null) => SendMessage(text, parseModeHtml: false, toChatId);

        private async Task SendMessage(string text, bool parseModeHtml, string? toChatId)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Message text cannot be null or empty.", nameof(text));
            }

            var fields = new Dictionary<string, string>
            {
                ["chat_id"] = toChatId ?? _chatId,
                ["text"] = text,
                ["disable_web_page_preview"] = "true"
            };
            if (parseModeHtml)
            {
                fields["parse_mode"] = "HTML";
            }

            using var content = new FormUrlEncodedContent(fields);
            using var response = await _httpClient.PostAsync($"{ApiBaseUrl}{_botToken}/sendMessage", content);
            var body = await response.Content.ReadAsStringAsync();

            TelegramApiResponse? result = null;
            try
            {
                result = JsonSerializer.Deserialize(body, TelegramJsonContext.Default.TelegramApiResponse);
            }
            catch (JsonException)
            {
                // Fall through to the generic error below.
            }

            if (result is null || !result.Ok)
            {
                throw new InvalidOperationException(
                    $"Telegram API error (HTTP {(int)response.StatusCode}): {result?.Description ?? body}");
            }
        }

        /// <summary>
        /// Sends the "typing…" indicator, shown to the recipient for a few seconds. Best-effort:
        /// failures are swallowed since this is cosmetic, never worth breaking a chat reply over.
        /// </summary>
        public async Task TrySendTypingAsync(string? toChatId = null)
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["chat_id"] = toChatId ?? _chatId,
                    ["action"] = "typing"
                });
                using var response = await _httpClient.PostAsync($"{ApiBaseUrl}{_botToken}/sendChatAction", content);
            }
            catch
            {
                // Cosmetic only.
            }
        }

        /// <summary>
        /// Long-poll for new updates (messages) sent to the bot, starting after <paramref name="offset"/>.
        /// Returns an empty list after <paramref name="timeoutSeconds"/>.
        /// </summary>
        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds)
        {
            var url = $"{ApiBaseUrl}{_botToken}/getUpdates?offset={offset}&timeout={timeoutSeconds}&allowed_updates=%5B%22message%22%5D";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds + 10));
            using var response = await _httpClient.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync();

            var result = JsonSerializer.Deserialize(body, TelegramJsonContext.Default.TelegramGetUpdatesResponse);
            if (result is null || !result.Ok)
            {
                throw new InvalidOperationException(
                    $"Telegram getUpdates error (HTTP {(int)response.StatusCode}): {result?.Description ?? body}");
            }

            return result.Result ?? [];
        }

        /// <summary>
        /// Splits on newlines and re-packs lines into chunks no longer than <paramref name="maxLength"/>.
        /// A single over-long line is hard-truncated with an ellipsis.
        /// </summary>
        private static List<string> SplitIntoChunks(string text, int maxLength)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var chunks = new List<string>();
            var current = new StringBuilder();

            foreach (var rawLine in lines)
            {
                var line = rawLine.Length > maxLength
                    ? rawLine[..(maxLength - 3)] + "..."
                    : rawLine;

                var projected = current.Length == 0 ? line.Length : current.Length + 1 + line.Length;
                if (current.Length > 0 && projected > maxLength)
                {
                    chunks.Add(current.ToString().TrimEnd());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append('\n');
                }
                current.Append(line);
            }

            if (current.Length > 0)
            {
                chunks.Add(current.ToString().TrimEnd());
            }

            return chunks.Count > 0 ? chunks : new List<string> { string.Empty };
        }
    }

    public class TelegramApiResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    public class TelegramGetUpdatesResponse
    {
        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("result")]
        public List<TelegramUpdate>? Result { get; set; }
    }

    public class TelegramUpdate
    {
        [JsonPropertyName("update_id")]
        public long UpdateId { get; set; }

        [JsonPropertyName("message")]
        public TelegramIncomingMessage? Message { get; set; }
    }

    public class TelegramIncomingMessage
    {
        [JsonPropertyName("message_id")]
        public long MessageId { get; set; }

        [JsonPropertyName("date")]
        public long Date { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("chat")]
        public TelegramChat Chat { get; set; } = new();

        [JsonPropertyName("from")]
        public TelegramUser? From { get; set; }
    }

    public class TelegramChat
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }

    public class TelegramUser
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }
    }

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(TelegramApiResponse))]
    [JsonSerializable(typeof(TelegramGetUpdatesResponse))]
    [JsonSerializable(typeof(TelegramUpdate))]
    [JsonSerializable(typeof(TelegramIncomingMessage))]
    [JsonSerializable(typeof(TelegramChat))]
    [JsonSerializable(typeof(TelegramUser))]
    internal partial class TelegramJsonContext : JsonSerializerContext
    {
    }
}
