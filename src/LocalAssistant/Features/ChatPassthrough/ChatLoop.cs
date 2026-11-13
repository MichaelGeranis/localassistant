using Google.Apis.Gmail.v1;
using LocalAssistant.Configuration;
using LocalAssistant.Diagnostics;
using LocalAssistant.Digest;
using LocalAssistant.Storage;
using LocalAssistant.VaultDigest;
using Ollama.Apis.v1;
using Telegram.Apis.v1;

namespace LocalAssistant.Chat
{
    /// <summary>
    /// Resident loop for <c>serve</c> mode: polls messenger app for messages sent to the bot and
    /// passes each through to the local model (see <see cref="ChatService"/>). Runs until killed;
    /// intended to be started by a logon scheduled task with restart-on-failure.
    ///
    /// Security: every update is checked against the single authorized chat id before it reaches the model or gets a reply. A message from any
    /// other chat is logged and silently dropped - no reply, so a stranger probing the bot learns
    /// nothing about it being live.
    ///
    /// A message never reaches the model or the transcript if it matches one command prefix, since these drive real mutations and need to be predictable
    /// rather than occasionally creative. Anything without a recognized prefix is ordinary chat.
    /// </summary>
    public static class ChatLoop
    {
        public static async Task RunAsync(Settings settings)
        {
            RunLog.Append("Chat service starting.");

            if (!settings.Chat.Enabled)
            {
                RunLog.Append("Chat disabled (settings.json: chat.enabled=false); idling without polling.");
                Console.WriteLine("Chat passthrough is disabled (settings.json: chat.enabled=false). Set it to true and restart to activate.");
                return;
            }

            TelegramServiceClient telegram;
            try
            {
                telegram = new TelegramServiceClient();
            }
            catch (InvalidOperationException ex)
            {
                RunLog.Append($"Chat service cannot start - Telegram not configured: {ex.Message}");
                Console.WriteLine($"Telegram not configured: {ex.Message}");
                return;
            }

            var ollamaClient = new OllamaServiceClient { OllamaUrl = settings.OllamaUrl };

            GMailServiceClient? gmailClient = null;
            try
            {
                gmailClient = new GMailServiceClient(digestLabel: settings.EmailDigestLabel);
            }
            catch (InvalidOperationException ex)
            {
                RunLog.Append($"\"mark as read\" will be unavailable - Gmail not configured: {ex.Message}");
            }

            var offset = await LoadOffsetAsync(telegram);
            var history = new List<ChatTurn>();

            Console.WriteLine($"Chat service running. Long-polling ({settings.Chat.LongPollSeconds}s timeout). Authorized chat: {telegram.ChatId}.");
            RunLog.Append($"Chat service running (long-poll timeout {settings.Chat.LongPollSeconds}s, model {settings.LLM}).");

            while (true)
            {
                try
                {
                    var updates = await telegram.GetUpdatesAsync(offset, timeoutSeconds: settings.Chat.LongPollSeconds);
                    foreach (var update in updates)
                    {
                        offset = update.UpdateId + 1;
                        await HandleUpdateAsync(telegram, ollamaClient, gmailClient, settings, history, update);
                        await SaveOffsetAsync(offset);
                    }
                }
                catch (Exception ex)
                {
                    // One bad poll (network blip, messenger hiccup) must not kill the daemon - log
                    // and back off briefly before retrying, so a persistent failure (e.g. a bad
                    // token) doesn't spin in a tight failure loop hammering the network/log.
                    RunLog.Append($"Chat poll failed: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }

        private static async Task HandleUpdateAsync(
            TelegramServiceClient telegram, OllamaServiceClient ollamaClient, GMailServiceClient? gmailClient,
            Settings settings, List<ChatTurn> history, TelegramUpdate update)
        {
            var message = update.Message;
            if (message is null || string.IsNullOrWhiteSpace(message.Text))
            {
                return;
            }

            var chatId = message.Chat.Id.ToString();
            if (chatId != telegram.ChatId)
            {
                // Never log the message text or sender username here - just enough to see it
                // happened. This is the one security-critical line.
                RunLog.Append($"Ignored message from unauthorized chat id {chatId}.");
                return;
            }

            var text = message.Text.Trim();

            if (text.Equals("/reset", StringComparison.OrdinalIgnoreCase))
            {
                history.Clear();
                await TrySendAsync(telegram, "Conversation history cleared.");
                RunLog.Append("Chat history reset.");
                return;
            }
            if (text.Equals("/help", StringComparison.OrdinalIgnoreCase) || text.Equals("/start", StringComparison.OrdinalIgnoreCase))
            {
                await TrySendAsync(telegram,
                    "Send any message to chat with the local model. /reset clears conversation history. "
                    + "\"Gmail: mark 1, 2 and 3 as read\" (or /read 1 3 5) marks those digest items read. "
                    + "\"Obsidian: <note>\" appends a note to your vault, and \"Obsidian: digest\" "
                    + "summarizes every note in it into your vault's digest file.");
                return;
            }

            if (MarkReadCommand.TryParse(text, out var readNumbers))
            {
                await HandleMarkReadAsync(telegram, gmailClient, readNumbers);
                return;
            }

            if (ChatCommandRouter.TryParse(text, out var target, out var body))
            {
                await HandleServiceCommandAsync(telegram, ollamaClient, gmailClient, settings, target, body);
                return;
            }

            await telegram.TrySendTypingAsync();

            string reply;
            try
            {
                if (!await ollamaClient.WaitUntilAvailableAsync(TimeSpan.FromSeconds(5)))
                {
                    reply = "The local model is unreachable right now - try again in a bit.";
                }
                else
                {
                    reply = await ChatService.ReplyAsync(ollamaClient, history, text, settings);
                    if (string.IsNullOrWhiteSpace(reply))
                    {
                        reply = "(no reply generated - try rephrasing)";
                    }
                }
            }
            catch (Exception ex)
            {
                RunLog.Append($"Chat reply generation failed: {ex.Message}");
                reply = "Something went wrong generating a reply - try again in a bit.";
            }

            if (!await TrySendAsync(telegram, reply))
            {
                return; // Don't add an unsent reply to history.
            }

            history.Add(new ChatTurn("User", text));
            history.Add(new ChatTurn("Assistant", reply));
            TrimHistory(history, settings.Chat.HistoryTurns);
        }

        /// <summary>
        /// Dispatches a <see cref="ChatCommandRouter"/> match to its service handler. 
        /// </summary>
        private static async Task HandleServiceCommandAsync(
            TelegramServiceClient telegram, OllamaServiceClient ollamaClient, GMailServiceClient? gmailClient,
            Settings settings, ChatCommandTarget target, string body)
        {
            switch (target)
            {
                case ChatCommandTarget.Gmail:
                    if (DigestNumberParser.TryParseNumbers(body, out var numbers))
                    {
                        await HandleMarkReadAsync(telegram, gmailClient, numbers);
                    }
                    else
                    {
                        await TrySendAsync(telegram,
                            "Didn't understand that Gmail command - try \"Gmail: mark 1, 2 and 3 as read\".");
                    }
                    return;

                case ChatCommandTarget.Obsidian:
                    await HandleObsidianAsync(telegram, ollamaClient, settings, body);
                    return;
            }
        }

        /// <summary>
        /// Handles an <c>Obsidian:</c> command: <c>digest</c> summarizes the whole vault (the one
        /// path here that uses the model), anything else is appended verbatim as a note via
        /// <see cref="ObsidianVaultWriter"/>.
        /// </summary>
        private static async Task HandleObsidianAsync(
            TelegramServiceClient telegram, OllamaServiceClient ollamaClient, Settings settings, string body)
        {
            if (!settings.Obsidian.Enabled || string.IsNullOrWhiteSpace(settings.Obsidian.VaultPath))
            {
                await TrySendAsync(telegram, "Obsidian isn't configured (set obsidian.enabled and obsidian.vaultPath in settings.json).");
                return;
            }
            if (string.IsNullOrWhiteSpace(body))
            {
                await TrySendAsync(telegram, "Nothing to note - add text after \"Obsidian:\".");
                return;
            }
            if (VaultDigestCommand.IsDigestRequest(body))
            {
                await HandleVaultDigestAsync(telegram, ollamaClient, settings);
                return;
            }

            try
            {
                await ObsidianVaultWriter.AppendAsync(settings.Obsidian, body);
                await TrySendAsync(telegram, "📝 Noted.");
                RunLog.Append("Chat appended a note to the Obsidian vault.");
            }
            catch (Exception ex)
            {
                RunLog.Append($"Obsidian note failed: {ex.Message}");
                await TrySendAsync(telegram, "Couldn't save that to Obsidian - try again in a bit.");
            }
        }

        /// <summary>
        /// Summarizes every note in the vault and overwrites the digest file with the result.
        /// Unlike the rest of the <c>Obsidian:</c> command this is slow - one model call per note
        /// - so it acknowledges up front rather than leaving the chat silent for a minute.
        /// </summary>
        private static async Task HandleVaultDigestAsync(
            TelegramServiceClient telegram, OllamaServiceClient ollamaClient, Settings settings)
        {
            if (!await ollamaClient.WaitUntilAvailableAsync(TimeSpan.FromSeconds(5)))
            {
                await TrySendAsync(telegram, "The local model is unreachable right now, so I can't digest the vault - try again in a bit.");
                return;
            }

            try
            {
                await TrySendAsync(telegram, "📓 Reading the vault - this takes a moment per note.");
                await telegram.TrySendTypingAsync();

                var digest = await VaultDigestService.BuildAsync(ollamaClient, settings.Obsidian, settings.LLM);
                if (digest.NoteCount == 0)
                {
                    await TrySendAsync(telegram, "No notes found in the vault, so there's nothing to digest.");
                    return;
                }

                await ObsidianVaultWriter.WriteDigestAsync(settings.Obsidian, digest.Markdown);
                await TrySendAsync(telegram, $"{digest.ChatSummary}\n\nSaved to {settings.Obsidian.DigestFilename}.");
                RunLog.Append($"Chat digested {digest.NoteCount} vault note(s) into {settings.Obsidian.DigestFilename}.");
            }
            catch (Exception ex)
            {
                RunLog.Append($"Vault digest failed: {ex.Message}");
                await TrySendAsync(telegram, "Couldn't digest the vault - try again in a bit.");
            }
        }

        /// <summary>
        /// Resolves the digest numbers a <see cref="MarkReadCommand"/> named against the last saved
        /// <see cref="LastEmailDigestStore"/> state and marks the matching Gmail messages read.
        /// </summary>
        private static async Task HandleMarkReadAsync(
            TelegramServiceClient telegram, GMailServiceClient? gmailClient, IReadOnlyList<int> numbers)
        {
            if (gmailClient is null)
            {
                await TrySendAsync(telegram, "Email access isn't configured, so I can't mark anything as read.");
                return;
            }

            var state = await LastEmailDigestStore.LoadAsync();
            if (state is null || state.Items.Count == 0)
            {
                await TrySendAsync(telegram, "I don't have a digest to reference yet.");
                return;
            }

            var byNumber = state.Items.ToDictionary(item => item.Number);
            var toMark = new List<string>();
            var found = new List<int>();
            var missing = new List<int>();
            foreach (var number in numbers)
            {
                if (byNumber.TryGetValue(number, out var item))
                {
                    toMark.Add(item.Id);
                    found.Add(number);
                }
                else
                {
                    missing.Add(number);
                }
            }

            if (toMark.Count == 0)
            {
                await TrySendAsync(telegram,
                    $"None of {string.Join(", ", numbers)} match the last digest ({state.Items.Count} item(s)).");
                return;
            }

            try
            {
                await gmailClient.MarkAsReadAsync(toMark);
                var reply = $"Marked {toMark.Count} as read: {string.Join(", ", found)}.";
                if (missing.Count > 0)
                {
                    reply += $" (no item {string.Join(", ", missing)} in the last digest)";
                }
                await TrySendAsync(telegram, reply);
                RunLog.Append($"Chat marked {toMark.Count} message(s) as read (digest #{string.Join(",", found)}).");
            }
            catch (Exception ex)
            {
                RunLog.Append($"Chat mark-as-read failed: {ex.Message}");
                await TrySendAsync(telegram, "Failed to mark those as read - try again in a bit.");
            }
        }

        private static async Task<bool> TrySendAsync(TelegramServiceClient telegram, string text)
        {
            try
            {
                await telegram.SendLongText(text);
                return true;
            }
            catch (Exception ex)
            {
                RunLog.Append($"Chat send failed: {ex.Message}");
                return false;
            }
        }

        private static void TrimHistory(List<ChatTurn> history, int turns)
        {
            var maxEntries = turns * 2; // one User + one Assistant entry per turn
            while (history.Count > maxEntries)
            {
                history.RemoveAt(0);
            }
        }

        /// <summary>
        /// Loads the persisted <c>getUpdates</c> offset. 
        /// </summary>
        private static async Task<long> LoadOffsetAsync(TelegramServiceClient telegram)
        {
            try
            {
                if (File.Exists(AppPaths.TelegramOffsetFile))
                {
                    var text = (await File.ReadAllTextAsync(AppPaths.TelegramOffsetFile)).Trim();
                    if (long.TryParse(text, out var stored))
                    {
                        return stored;
                    }
                    RunLog.Append("telegram-offset.txt is corrupt; skipping any backlog instead of replaying it.");
                }
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not read telegram-offset.txt ({ex.Message}); skipping any backlog.");
            }

            try
            {
                var pending = await telegram.GetUpdatesAsync(offset: 0, timeoutSeconds: 0);
                if (pending.Count > 0)
                {
                    // These are skipped without the per-message chat-id check HandleUpdateAsync
                    // normally does, so log a count here - otherwise a stranger's messages queued
                    // before this first startup would leave no trace anywhere, unlike every
                    // unauthorized message the steady-state loop sees afterward.
                    var skipTo = pending.Max(u => u.UpdateId) + 1;
                    RunLog.Append($"Skipping {pending.Count} backlog message(s) queued before startup.");
                    await SaveOffsetAsync(skipTo);
                    return skipTo;
                }
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not check for a Telegram backlog ({ex.Message}); starting from 0.");
            }

            return 0;
        }

        private static async Task SaveOffsetAsync(long offset)
        {
            try
            {
                AppPaths.EnsureRoot();
                await File.WriteAllTextAsync(AppPaths.TelegramOffsetFile, offset.ToString());
            }
            catch (Exception ex)
            {
                // Non-fatal: worst case the next start re-checks for a backlog instead of resuming exactly.
                RunLog.Append($"Could not persist Telegram offset: {ex.Message}");
            }
        }
    }
}
