using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LocalAssistant.Configuration;

namespace Google.Apis.Gmail.v1
{
    /// <summary>
    /// Hand-rolled Gmail REST client.
    /// </summary>
    public class GMailServiceClient
    {
        private const string MessagesUrl = "https://gmail.googleapis.com/gmail/v1/users/me/messages";
        private const string LabelsUrl = "https://gmail.googleapis.com/gmail/v1/users/me/labels";
        private const string DraftsUrl = "https://gmail.googleapis.com/gmail/v1/users/me/drafts";
        private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

        /// <summary>batchModify accepts at most 1000 ids per call.</summary>
        private const int BatchModifyChunkSize = 1000;

        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly string _refreshToken;
        private readonly HttpClient _httpClient = new();

        private string? _accessToken;
        private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;
        private string? _digestedLabelId;

        public GMailServiceClient(
            string? clientId = null,
            string? clientSecret = null,
            string? refreshToken = null,
            string digestLabel = "Digested")
        {
            clientId ??= Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID")
                ?? throw new InvalidOperationException("GMAIL_CLIENT_ID environment variable is not set.");
            clientSecret ??= Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET")
                ?? throw new InvalidOperationException("GMAIL_CLIENT_SECRET environment variable is not set.");
            refreshToken ??= Environment.GetEnvironmentVariable("GMAIL_REFRESH_TOKEN")
                ?? throw new InvalidOperationException("GMAIL_REFRESH_TOKEN environment variable is not set.");

            if (string.IsNullOrWhiteSpace(clientId))
            {
                throw new ArgumentException("A Gmail OAuth client id is required.", nameof(clientId));
            }
            if (string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new ArgumentException("A Gmail OAuth client secret is required.", nameof(clientSecret));
            }
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new ArgumentException("A Gmail OAuth refresh token is required.", nameof(refreshToken));
            }

            _clientId = clientId;
            _clientSecret = clientSecret;
            _refreshToken = refreshToken;
        }

        /// <summary>
        /// Pages through every unread and digested message, fetches each in full,
        /// and returns it with headers and a plain-text body.
        /// </summary>
        public async Task<IReadOnlyList<GMailMessage>> GetUnreadEmails(string digestedLabelName)
        {
            var accessToken = await GetAccessTokenAsync();
            var messages = new List<GMailMessage>();
            string? pageToken = null;

            do
            {
                var labelToken = digestedLabelName.Contains(' ') ? $"\"{digestedLabelName}\"" : digestedLabelName;
                var query = "q=" + Uri.EscapeDataString($"is:unread -label:{labelToken}") + "&maxResults=500";
                if (!string.IsNullOrEmpty(pageToken))
                {
                    query += "&pageToken=" + Uri.EscapeDataString(pageToken);
                }

                using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"{MessagesUrl}?{query}");
                listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var listResponse = await _httpClient.SendAsync(listRequest);
                listResponse.EnsureSuccessStatusCode();

                var list = JsonSerializer.Deserialize(
                    await listResponse.Content.ReadAsStringAsync(),
                    GMailJsonContext.Default.GMailMessageListResponse);

                foreach (var reference in list?.Messages ?? new List<GMailMessageReference>())
                {
                    using var messageRequest = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"{MessagesUrl}/{Uri.EscapeDataString(reference.Id)}?format=full");
                    messageRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    using var messageResponse = await _httpClient.SendAsync(messageRequest);
                    messageResponse.EnsureSuccessStatusCode();

                    var message = JsonSerializer.Deserialize(
                        await messageResponse.Content.ReadAsStringAsync(),
                        GMailJsonContext.Default.GMailMessageResponse);
                    if (message is null)
                    {
                        continue;
                    }

                    messages.Add(new GMailMessage
                    {
                        Id = message.Id,
                        ThreadId = message.ThreadId,
                        From = GetHeader(message.Payload, "From"),
                        Subject = GetHeader(message.Payload, "Subject"),
                        Date = GetHeader(message.Payload, "Date"),
                        MessageIdHeader = GetHeader(message.Payload, "Message-ID"),
                        Body = GetBody(message.Payload)
                    });
                }

                pageToken = list?.NextPageToken;
            }
            while (!string.IsNullOrEmpty(pageToken));

            return messages;
        }

        /// <summary>
        /// Applies the digested label to the given
        /// messages so the next run's query skips them.
        /// </summary>
        public async Task MarkDigestedAsync(IReadOnlyList<string> messageIds, string digestedLabelName)
        {
            if (messageIds.Count == 0)
            {
                return;
            }

            var labelId = await EnsureDigestedLabelAsync(await GetAccessTokenAsync(), digestedLabelName);
            await BatchModifyAsync(messageIds, addLabelIds: new List<string> { labelId }, removeLabelIds: null);
        }

        /// <summary>
        /// Removes Gmail's built-in <c>UNREAD</c> label from the given messages (marks them read).
        /// </summary>
        public async Task MarkAsReadAsync(IReadOnlyList<string> messageIds) =>
            await BatchModifyAsync(messageIds, addLabelIds: null, removeLabelIds: new List<string> { "UNREAD" });

        private async Task BatchModifyAsync(
            IReadOnlyList<string> messageIds, List<string>? addLabelIds, List<string>? removeLabelIds)
        {
            if (messageIds.Count == 0)
            {
                return;
            }

            var accessToken = await GetAccessTokenAsync();

            for (var offset = 0; offset < messageIds.Count; offset += BatchModifyChunkSize)
            {
                var chunk = messageIds.Skip(offset).Take(BatchModifyChunkSize).ToList();
                var body = JsonSerializer.Serialize(
                    new GMailBatchModifyRequest { Ids = chunk, AddLabelIds = addLabelIds, RemoveLabelIds = removeLabelIds },
                    GMailJsonContext.Default.GMailBatchModifyRequest);

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{MessagesUrl}/batchModify")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var response = await _httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }
        }

        /// <summary>
        /// Creates a Gmail draft reply in the given thread.
        /// </summary>
        public async Task<string?> CreateDraftAsync(
            string threadId, string toAddress, string subject, string? inReplyToMessageId, string bodyText)
        {
            if (string.IsNullOrWhiteSpace(toAddress))
            {
                return null;
            }

            var accessToken = await GetAccessTokenAsync();
            var request = new GMailDraftCreateRequest
            {
                Message = new GMailDraftMessage
                {
                    Raw = BuildRawMessage(toAddress, subject, inReplyToMessageId, bodyText),
                    ThreadId = string.IsNullOrWhiteSpace(threadId) ? null : threadId
                }
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, DraftsUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(request, GMailJsonContext.Default.GMailDraftCreateRequest),
                    Encoding.UTF8,
                    "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();

            var draft = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                GMailJsonContext.Default.GMailDraftResponse);
            return draft?.Message?.Id;
        }

        /// <summary>
        /// Pulls the bare address out of a <c>From</c> header value
        /// </summary>
        public static string ParseAddress(string fromHeader)
        {
            if (string.IsNullOrWhiteSpace(fromHeader))
            {
                return string.Empty;
            }

            var match = Regex.Match(fromHeader, "<([^>]+)>");
            var candidate = (match.Success ? match.Groups[1].Value : fromHeader).Trim().Trim('"').Trim();

            var lastSpace = candidate.LastIndexOf(' ');
            if (lastSpace >= 0)
            {
                candidate = candidate[(lastSpace + 1)..];
            }

            return candidate.Trim().Trim('<', '>').ToLowerInvariant();
        }

        /// <summary>Assembles an RFC 2822 reply and returns it base64url-encoded for the Gmail <c>raw</c> field.</summary>
        private static string BuildRawMessage(string toAddress, string subject, string? inReplyToMessageId, string body)
        {
            static string HeaderSafe(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            static string EncodeWord(string value) => value.All(c => c < 128)
                ? value
                : "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "?=";

            var replySubject = subject.Trim();
            if (!replySubject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
            {
                replySubject = "Re: " + replySubject;
            }

            var message = new StringBuilder();
            message.Append("To: ").Append(HeaderSafe(toAddress)).Append("\r\n");
            message.Append("Subject: ").Append(EncodeWord(HeaderSafe(replySubject))).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(inReplyToMessageId))
            {
                var id = HeaderSafe(inReplyToMessageId);
                message.Append("In-Reply-To: ").Append(id).Append("\r\n");
                message.Append("References: ").Append(id).Append("\r\n");
            }
            message.Append("MIME-Version: 1.0\r\n");
            message.Append("Content-Type: text/plain; charset=\"UTF-8\"\r\n");
            message.Append("Content-Transfer-Encoding: base64\r\n");
            message.Append("\r\n");

            var bodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body.Replace("\r\n", "\n")));
            for (var i = 0; i < bodyBase64.Length; i += 76)
            {
                message.Append(bodyBase64, i, Math.Min(76, bodyBase64.Length - i)).Append("\r\n");
            }

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(message.ToString()))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private async Task<string> EnsureDigestedLabelAsync(string accessToken, string digestedLabelName)
        {
            if (_digestedLabelId is not null)
            {
                return _digestedLabelId;
            }

            using var listRequest = new HttpRequestMessage(HttpMethod.Get, LabelsUrl);
            listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var listResponse = await _httpClient.SendAsync(listRequest);
            listResponse.EnsureSuccessStatusCode();

            var existing = JsonSerializer.Deserialize(
                    await listResponse.Content.ReadAsStringAsync(),
                    GMailJsonContext.Default.GMailLabelListResponse)?.Labels?
                .FirstOrDefault(label => string.Equals(label.Name, digestedLabelName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return _digestedLabelId = existing.Id;
            }

            var createBody = JsonSerializer.Serialize(
                new GMailLabelCreateRequest { Name = digestedLabelName },
                GMailJsonContext.Default.GMailLabelCreateRequest);
            using var createRequest = new HttpRequestMessage(HttpMethod.Post, LabelsUrl)
            {
                Content = new StringContent(createBody, Encoding.UTF8, "application/json")
            };
            createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var createResponse = await _httpClient.SendAsync(createRequest);
            createResponse.EnsureSuccessStatusCode();

            var created = JsonSerializer.Deserialize(
                await createResponse.Content.ReadAsStringAsync(),
                GMailJsonContext.Default.GMailLabel)
                ?? throw new InvalidOperationException("Failed to parse label creation response.");
            return _digestedLabelId = created.Id;
        }

        private async Task<string> GetAccessTokenAsync()
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            {
                return _accessToken;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["client_secret"] = _clientSecret,
                    ["refresh_token"] = _refreshToken,
                    ["grant_type"] = "refresh_token"
                })
            };
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var tokenResponse = JsonSerializer.Deserialize(
                await response.Content.ReadAsStringAsync(),
                GMailJsonContext.Default.GMailTokenResponse)
                ?? throw new InvalidOperationException("Failed to parse token refresh response.");

            _accessToken = tokenResponse.AccessToken;
            // Renew a minute early so a token never expires mid-run.
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn - 60);
            return _accessToken;
        }

        private static string GetHeader(GMailMessagePart? part, string name) =>
            part?.Headers?.FirstOrDefault(header => string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))?.Value
            ?? string.Empty;

        /// <summary>
        /// Walks the MIME tree preferring <c>text/plain</c>; falls back to stripping HTML, then to
        /// any decodable leaf body.
        /// </summary>
        private static string GetBody(GMailMessagePart? part)
        {
            if (part is null)
            {
                return string.Empty;
            }

            if (part.MimeType == "text/plain" && !string.IsNullOrEmpty(part.Body?.Data))
            {
                return DecodeBase64Url(part.Body.Data);
            }
            if (part.MimeType == "text/html" && !string.IsNullOrEmpty(part.Body?.Data))
            {
                return HtmlToPlainText(DecodeBase64Url(part.Body.Data));
            }

            foreach (var child in part.Parts ?? new List<GMailMessagePart>())
            {
                var body = GetBody(child);
                if (!string.IsNullOrEmpty(body))
                {
                    return body;
                }
            }

            if (string.IsNullOrEmpty(part.Body?.Data))
            {
                return string.Empty;
            }

            var text = DecodeBase64Url(part.Body.Data);
            return text.Contains('<') && text.Contains('>') ? HtmlToPlainText(text) : text;
        }

        private static string HtmlToPlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            html = Regex.Replace(html, "<(script|style|head|title)[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            html = Regex.Replace(html, "<!--.*?-->", " ", RegexOptions.Singleline);
            html = Regex.Replace(html, "<(br|/p|/div|/tr|/li|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, "<[^>]+>", " ");
            html = WebUtility.HtmlDecode(html);
            html = Regex.Replace(html, "[ \\t\\f\\v]+", " ");
            html = Regex.Replace(html, " *\\n *", "\n");
            html = Regex.Replace(html, "\\n{3,}", "\n\n");
            return html.Trim();
        }

        private static string DecodeBase64Url(string value)
        {
            var text = value.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + (4 - text.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(text));
        }
    }

    public class GMailMessage
    {
        public string Id { get; init; } = string.Empty;
        public string ThreadId { get; init; } = string.Empty;
        public string From { get; init; } = string.Empty;
        public string Subject { get; init; } = string.Empty;
        public string Date { get; init; } = string.Empty;

        /// <summary>The RFC 2822 <c>Message-ID</c> header, for threading a draft reply (<c>In-Reply-To</c>).</summary>
        public string MessageIdHeader { get; init; } = string.Empty;

        public string Body { get; init; } = string.Empty;
    }

    public class GMailMessageListResponse
    {
        [JsonPropertyName("messages")]
        public List<GMailMessageReference>? Messages { get; set; }

        [JsonPropertyName("nextPageToken")]
        public string? NextPageToken { get; set; }
    }

    public class GMailMessageReference
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }

    public class GMailMessageResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("threadId")]
        public string ThreadId { get; set; } = string.Empty;

        [JsonPropertyName("payload")]
        public GMailMessagePart? Payload { get; set; }
    }

    public class GMailMessagePart
    {
        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; } = string.Empty;

        [JsonPropertyName("headers")]
        public List<GMailHeader>? Headers { get; set; }

        [JsonPropertyName("body")]
        public GMailMessageBody? Body { get; set; }

        [JsonPropertyName("parts")]
        public List<GMailMessagePart>? Parts { get; set; }
    }

    public class GMailHeader
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;
    }

    public class GMailMessageBody
    {
        [JsonPropertyName("data")]
        public string? Data { get; set; }
    }

    public class GMailTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }

    public class GMailLabel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    public class GMailLabelListResponse
    {
        [JsonPropertyName("labels")]
        public List<GMailLabel>? Labels { get; set; }
    }

    public class GMailLabelCreateRequest
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("labelListVisibility")]
        public string LabelListVisibility { get; set; } = "labelShow";

        [JsonPropertyName("messageListVisibility")]
        public string MessageListVisibility { get; set; } = "show";
    }

    public class GMailBatchModifyRequest
    {
        [JsonPropertyName("ids")]
        public List<string> Ids { get; set; } = new();

        [JsonPropertyName("addLabelIds")]
        public List<string>? AddLabelIds { get; set; }

        [JsonPropertyName("removeLabelIds")]
        public List<string>? RemoveLabelIds { get; set; }
    }

    public class GMailDraftCreateRequest
    {
        [JsonPropertyName("message")]
        public GMailDraftMessage Message { get; set; } = new();
    }

    public class GMailDraftMessage
    {
        [JsonPropertyName("raw")]
        public string Raw { get; set; } = string.Empty;

        [JsonPropertyName("threadId")]
        public string? ThreadId { get; set; }
    }

    public class GMailDraftResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public GMailMessageResponse? Message { get; set; }
    }

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(GMailMessageListResponse))]
    [JsonSerializable(typeof(GMailMessageResponse))]
    [JsonSerializable(typeof(GMailTokenResponse))]
    [JsonSerializable(typeof(GMailLabel))]
    [JsonSerializable(typeof(GMailLabelListResponse))]
    [JsonSerializable(typeof(GMailLabelCreateRequest))]
    [JsonSerializable(typeof(GMailBatchModifyRequest))]
    [JsonSerializable(typeof(GMailDraftCreateRequest))]
    [JsonSerializable(typeof(GMailDraftResponse))]
    internal partial class GMailJsonContext : JsonSerializerContext
    {
    }
}
