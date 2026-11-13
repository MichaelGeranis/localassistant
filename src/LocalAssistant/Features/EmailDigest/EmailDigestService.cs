using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Apis.Gmail.v1;
using LocalAssistant.Configuration;
using LocalAssistant.Diagnostics;
using Ollama.Apis.v1;

namespace LocalAssistant.Digest
{
    /// <summary>How much the recipient should care about an email.</summary>
    /// Items are grouped by priority when LLM produced real summaries
    public enum EmailPriority
    {
        Action,
        Fyi,
        Quiet
    }

    /// <summary>One summarized, triaged unread email.</summary>
    public sealed class DigestItem
    {
        public required string Id { get; init; }
        public required string Sender { get; init; }
        public required string Subject { get; init; }
        public required string Summary { get; init; }
        public required EmailPriority Priority { get; init; }
        public string? DraftUrl { get; init; }
    }

    /// <summary>
    /// Summarized unread emails. 
    /// </summary>
    public sealed class EmailDigest
    {
        public const string GmailInboxUnreadUrl = "https://mail.google.com/mail/#search/is%3Aunread";

        public const string GmailInboxAllUrl = "https://mail.google.com/mail/#all/";

        public required int EmailCount { get; init; }

        public required DateTimeOffset GeneratedAt { get; init; }

        /// <summary>False if Ollama was unreachable, so Items carry subjects only, not AI summaries or triage.</summary>
        public required bool LlmAvailable { get; init; }

        public required IReadOnlyList<DigestItem> Items { get; init; }

        public bool ExpandQuietEmails { get; init; }

        public IReadOnlyList<string> EmailIds => Items.Select(item => item.Id).ToList();

        private IReadOnlyList<(int Number, DigestItem Item)>? _numberedItems;

        /// <summary>
        /// Items paired with the number shown next to them in the rendered digest.
        ///
        /// The single source of truth for digest numbering: <see cref="Render"/> prints these
        /// numbers and <c>LastEmailDigestStore</c> persists them, so a chat "mark 3 as read"
        /// resolves to the message the user actually saw as 3. Numbering must never be derived
        /// a second time anywhere - the two copies drifting apart would silently mark the wrong
        /// email, across two separate processes.
        /// </summary>
        public IReadOnlyList<(int Number, DigestItem Item)> NumberedItems =>
            _numberedItems ??= OrderedForNumbering().Select((item, index) => (index + 1, item)).ToList();

        private IEnumerable<DigestItem> OrderedForNumbering()
        {
            if (!LlmAvailable)
            {
                foreach (var item in Items) yield return item;
                yield break;
            }

            foreach (var item in Items.Where(i => i.Priority == EmailPriority.Action)) yield return item;
            foreach (var item in Items.Where(i => i.Priority == EmailPriority.Fyi)) yield return item;
            if (ExpandQuietEmails)
            {
                foreach (var item in Items.Where(i => i.Priority == EmailPriority.Quiet)) yield return item;
            }
        }

        /// <summary>Full digest as plain text, for console output.</summary>
        public string FullText => Render(html: false);

        /// <summary>
        /// Digest as Telegram-flavoured HTML: each item number is a tap-through link to that email,
        /// group headers are bold, and a trailing "All unread" link. Telegram's HTML mode only
        /// needs &amp; &lt; &gt; escaped.
        /// </summary>
        public string TelegramHtml => Render(html: true);

        private string Render(bool html)
        {
            string Bold(string s) => html ? $"<b>{s}</b>" : s;
            string Line(int number, DigestItem item)
            {
                var label = html && !string.IsNullOrEmpty(item.Id)
                    ? $"<a href=\"{GmailInboxAllUrl}{item.Id}\">{number}.</a>"
                    : $"{number}.";
                var summary = html ? HtmlEscape(item.Summary) : item.Summary;
                var draft = item.DraftUrl is { Length: > 0 } url
                    ? (html ? $"  <a href=\"{url}\">✍️ draft ready</a>" : $"  [draft ready: {url}]")
                    : string.Empty;
                return $"{label} {summary}{draft}";
            }
            string AllUnread() => html
                ? $"<a href=\"{GmailInboxUnreadUrl}\">All unread</a>"
                : $"All unread: {GmailInboxUnreadUrl}";

            // Numbers come from NumberedItems rather than being counted out again here, so what
            // is printed and what "mark N as read" resolves against cannot disagree.
            var numbered = NumberedItems;
            var text = new StringBuilder();
            text.AppendLine(Bold($"Unread email digest - {GeneratedAt:yyyy-MM-dd HH:mm}"));

            if (!LlmAvailable)
            {
                // Degraded run: no summaries, no triage - fall back to the old flat list.
                text.AppendLine($"{EmailCount} unread email(s)");
                text.AppendLine("[Ollama was unreachable - subjects only, no AI summaries]");
                text.AppendLine();
                foreach (var (number, item) in numbered)
                {
                    text.AppendLine(Line(number, item));
                }
                if (EmailCount > 0)
                {
                    text.AppendLine();
                    text.AppendLine(AllUnread());
                }
                return text.ToString();
            }

            text.AppendLine(CountsLine());
            text.AppendLine();

            AppendGroup(EmailPriority.Action, "NEEDS ACTION");
            AppendGroup(EmailPriority.Fyi, "FYI");

            var quietCount = Items.Count(i => i.Priority == EmailPriority.Quiet);
            if (quietCount > 0)
            {
                if (ExpandQuietEmails)
                {
                    AppendGroup(EmailPriority.Quiet, $"QUIET ({quietCount})");
                }
                else
                {
                    // Collapsed, so these items are not numbered - and NumberedItems leaves them
                    // out for the same reason: an unshown item has no number to mark as read.
                    text.AppendLine($"Quiet: {quietCount} hidden (newsletters, receipts, notifications)");
                    text.AppendLine();
                }
            }

            if (EmailCount > 0)
            {
                text.AppendLine(AllUnread());
            }
            return text.ToString();

            void AppendGroup(EmailPriority priority, string header)
            {
                var group = numbered.Where(pair => pair.Item.Priority == priority).ToList();
                if (group.Count == 0)
                {
                    return;
                }
                text.AppendLine(Bold(header));
                foreach (var (number, item) in group)
                {
                    text.AppendLine(Line(number, item));
                }
                text.AppendLine();
            }
        }

        private string CountsLine()
        {
            var parts = new List<string> { $"{EmailCount} unread" };
            var action = Items.Count(i => i.Priority == EmailPriority.Action);
            var fyi = Items.Count(i => i.Priority == EmailPriority.Fyi);
            var quiet = Items.Count(i => i.Priority == EmailPriority.Quiet);
            if (action > 0) parts.Add($"{action} need action");
            if (fyi > 0) parts.Add($"{fyi} FYI");
            if (quiet > 0) parts.Add($"{quiet} quiet");
            return string.Join(" · ", parts);
        }

        private static string HtmlEscape(string value) => value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    public static class EmailDigestService
    {
        private static readonly JsonNode TriageSchema = JsonNode.Parse(
            """{"type":"object","properties":{"priority":{"type":"string","enum":["action","fyi","quiet"]}},"required":["priority"],"additionalProperties":false}""")!;

        public static async Task<EmailDigest> BuildAsync(
            GMailServiceClient gmailClient, OllamaServiceClient ollamaClient, Settings settings)
        {
            var unreadEmails = await gmailClient.GetUnreadEmails(settings.EmailDigestLabel);

            var ollamaAvailable = unreadEmails.Count == 0
                || await ollamaClient.WaitUntilAvailableAsync(TimeSpan.FromSeconds(30));

            var items = new List<DigestItem>(unreadEmails.Count);

            foreach (var email in unreadEmails)
            {
                var sender = string.IsNullOrWhiteSpace(email.From) ? "(unknown sender)" : email.From;
                var subject = string.IsNullOrWhiteSpace(email.Subject) ? "(no subject)" : email.Subject;

                if (!ollamaAvailable)
                {
                    items.Add(new DigestItem
                    {
                        Id = email.Id,
                        Sender = sender,
                        Subject = subject,
                        Summary = $"{sender} - {subject}",
                        Priority = EmailPriority.Fyi
                    });
                    continue;
                }

                var summary = await SummarizeAsync(ollamaClient, email, sender, subject, settings.LLM);

                EmailPriority priority;
                string? draftUrl = null;

                if (settings.DraftReplies.Enabled
                    && DraftAllowlist.Matches(settings.DraftReplies.Senders, email.From))
                {
                    priority = EmailPriority.Action;
                    draftUrl = await TryDraftReplyAsync(
                        gmailClient, ollamaClient, email, sender, subject, summary, settings.LLM);
                }
                else
                {
                    priority = await ClassifyAsync(ollamaClient, sender, subject, summary, settings.LLM);
                }

                items.Add(new DigestItem
                {
                    Id = email.Id,
                    Sender = sender,
                    Subject = subject,
                    Summary = summary,
                    Priority = priority,
                    DraftUrl = draftUrl
                });
            }

            return new EmailDigest
            {
                EmailCount = unreadEmails.Count,
                GeneratedAt = DateTimeOffset.Now,
                LlmAvailable = ollamaAvailable,
                Items = items,
                ExpandQuietEmails = settings.ExpandQuietEmailsInDigest
            };
        }

        private static async Task<string> SummarizeAsync(
            OllamaServiceClient ollamaClient, GMailMessage email, string sender, string subject, string model)
        {
            var instructions = CreateSummaryInstructions(sender, subject);
            var body = TruncateBody(email, instructions);
            var prompt = instructions + body;

            try
            {
                return (await ollamaClient.MakeRequest(
                    promptText: prompt,
                    options: OllamaRequestOptions.ConciseSummary,
                    think: false,
                    model: model
                )).Trim();
            }
            catch (Exception ex)
            {
                return $"{sender} - {subject} (summarization failed: {ex.Message})";
            }
        }

        private static async Task<EmailPriority> ClassifyAsync(
            OllamaServiceClient ollamaClient, string sender, string subject, string summary, string model)
        {
            try
            {
                var raw = await ollamaClient.MakeRequest(
                    promptText: CreateTriagePrompt(sender, subject, summary),
                    options: OllamaRequestOptions.Triage,
                    think: false,
                    model: model,
                    format: TriageSchema);
                return ParsePriority(raw);
            }
            catch
            {
                return EmailPriority.Fyi;
            }
        }

        /// <summary>
        /// Drafts a reply and saves it as an email draft in the email's thread. 
        /// A failed draftnever affects the rest of the digest.
        /// </summary>
        private static async Task<string?> TryDraftReplyAsync(
            GMailServiceClient gmailClient, OllamaServiceClient ollamaClient,
            GMailMessage email, string sender, string subject, string summary, string model)
        {
            try
            {
                var reply = (await ollamaClient.MakeRequest(
                    promptText: CreateDraftPrompt(sender, subject, summary, email),
                    options: OllamaRequestOptions.Drafting,
                    think: false,
                    model: model)).Trim();

                if (string.IsNullOrWhiteSpace(reply))
                {
                    return null;
                }

                reply += "\n\n[AI draft - review before sending]";

                var draftMessageId = await gmailClient.CreateDraftAsync(
                    threadId: email.ThreadId,
                    toAddress: GMailServiceClient.ParseAddress(email.From),
                    subject: subject,
                    inReplyToMessageId: email.MessageIdHeader,
                    bodyText: reply);

                var anchor = !string.IsNullOrEmpty(draftMessageId) ? draftMessageId
                    : !string.IsNullOrEmpty(email.ThreadId) ? email.ThreadId
                    : email.Id;
                return string.IsNullOrEmpty(anchor) ? null : EmailDigest.GmailInboxAllUrl + anchor;
            }
            catch (Exception ex)
            {
                RunLog.Append($"Draft reply failed for \"{subject}\": {ex.Message}");
                return null;
            }
        }

        private static EmailPriority ParsePriority(string raw)
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                var value = document.RootElement.GetProperty("priority").GetString();
                return value?.Trim().ToLowerInvariant() switch
                {
                    "action" => EmailPriority.Action,
                    "quiet" => EmailPriority.Quiet,
                    _ => EmailPriority.Fyi
                };
            }
            catch
            {
                return EmailPriority.Fyi;
            }
        }

        private static string CreateSummaryInstructions(string sender, string subject)
        {
            return "You are summarizing an email for a daily digest. Write ONE or TWO plain sentences that say "
                + "what the email is about and any action the recipient must take. Ignore images, links, "
                + "layout and boilerplate. Do not use markdown, headings or bullet points. Reply with the "
                + "summary only, no preamble.\n\n"
                + $"From: {sender}\n"
                + $"Subject: {subject}\n"
                + "Body:\n";
        }

        private static string CreateTriagePrompt(string sender, string subject, string summary)
        {
            return $$"""
                You are triaging one email for a personal daily digest. Choose its priority.

                action = a specific person is waiting on the recipient to reply, decide, pay, sign,
                         send something, or show up, OR there is a real deadline the recipient must meet.
                fyi    = worth reading but needs no action - personal or work updates, statements,
                         confirmations of things already done.
                quiet  = automated, promotional, or bulk mail - newsletters, marketing, receipts,
                         order and shipping notices, social and app notifications.

                When unsure between action and fyi, choose fyi. When unsure between fyi and quiet, choose fyi.

                Examples:
                From: Stripe <receipts@stripe.com> | Subject: Your receipt from Acme => quiet
                From: LinkedIn | Subject: You appeared in 9 searches this week => quiet
                From: Amazon | Subject: Your package has shipped => quiet
                From: Jane Doe | Subject: Re: contract - can you confirm the start date? => action
                From: HR | Subject: Reminder: submit your timesheet by Friday => action
                From: Mom | Subject: photos from the trip => fyi
                From: Bank | Subject: Your March statement is ready => fyi

                Email:
                From: {{sender}}
                Subject: {{subject}}
                Summary: {{summary}}

                Respond with JSON only: {"priority": "action" | "fyi" | "quiet"}.
                """;
        }

        private static string CreateDraftPrompt(string sender, string subject, string summary, GMailMessage email)
        {
            var instructions =
                "Write a brief, direct reply to the email below, speaking as the recipient. Two to four "
                + "sentences, plain text, no subject line and no signature. Answer the sender's actual "
                + "question or request. If a detail you would need (a date, an amount, a decision) is not "
                + "in the email, leave a short [...] placeholder rather than inventing it. Reply with the "
                + "message body only, no preamble.\n\n"
                + $"From: {sender}\n"
                + $"Subject: {subject}\n"
                + $"Summary: {summary}\n"
                + "Full email:\n";
            return instructions + TruncateBody(email, instructions);
        }

        private static string TruncateBody(GMailMessage email, string instructions)
        {
            // ~4 chars per token: 6000 chars ≈ 1500 tokens, leaving room in the 4096-token context for the reply.
            var bodyBudget = Math.Min(6000, OllamaServiceClient.MaxPromptLength - instructions.Length - 100);
            var body = (email.Body ?? string.Empty).Trim();
            if (body.Length > bodyBudget)
            {
                body = body[..bodyBudget];
            }
            if (body.Length < 10)
            {
                body = "(no readable body content)";
            }

            return body;
        }
    }

    /// <summary>
    /// Decides whether an email's sender is on the draft-reply allowlist.
    /// </summary>
    internal static class DraftAllowlist
    {
        public static bool Matches(IReadOnlyList<string> patterns, string fromHeader)
        {
            if (patterns.Count == 0)
            {
                return false;
            }

            var address = GMailServiceClient.ParseAddress(fromHeader);
            if (address.Length == 0)
            {
                return false;
            }

            var atIndex = address.IndexOf('@');
            var domain = atIndex >= 0 ? address[atIndex..] : string.Empty; // "@firm.com"

            foreach (var raw in patterns)
            {
                var pattern = raw.Trim().ToLowerInvariant();
                if (pattern.Length == 0)
                {
                    continue;
                }

                if (pattern.Contains('@') && !pattern.StartsWith('@'))
                {
                    if (address == pattern) return true;              // exact address
                }
                else
                {
                    var wanted = pattern.StartsWith('@') ? pattern : "@" + pattern;
                    if (domain == wanted) return true;                // whole domain
                }
            }

            return false;
        }
    }
}
