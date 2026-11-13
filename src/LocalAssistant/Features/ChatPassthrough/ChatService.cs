using LocalAssistant.Configuration;
using Ollama.Apis.v1;

namespace LocalAssistant.Chat
{
    /// <summary>One turn of chat history, for the rolling transcript.</summary>
    public sealed record ChatTurn(string Role, string Text);

    /// <summary>
    /// Pure passthrough from a messenger service to the local model: no tools, no email or
    /// calendar access. Builds a system prompt + rolling transcript into a single
    /// <c>/api/generate</c> prompt (the codebase's one Ollama endpoint) and returns the reply.
    /// </summary>
    public static class ChatService
    {
        /// <summary>Keep the chat model warm between messages so casual conversation isn't a reload each time.</summary>
        public const string ChatKeepAlive = "30m";

        public static async Task<string> ReplyAsync(
            OllamaServiceClient ollamaClient, IReadOnlyList<ChatTurn> history, string userText, Settings settings)
        {
            var prompt = BuildPrompt(settings.Chat.PrefixedPrompt, history, userText);

            // think:false - this is a small instruct model; a hidden reasoning pass would just
            // burn the output budget on chit-chat instead of the visible reply 
            var reply = await ollamaClient.MakeRequest(
                promptText: prompt,
                options: OllamaRequestOptions.Chat,
                keepAlive: ChatKeepAlive,
                think: false,
                model: settings.LLM);

            return reply.Trim();
        }

        private static string BuildPrompt(string systemPrompt, IReadOnlyList<ChatTurn> history, string userText)
        {
            var prompt = new System.Text.StringBuilder();
            prompt.AppendLine(systemPrompt);
            prompt.AppendLine();

            foreach (var turn in history)
            {
                prompt.AppendLine($"{turn.Role}: {turn.Text}");
            }

            prompt.AppendLine($"User: {userText}");
            prompt.Append("Assistant:");

            var text = prompt.ToString();

            // MakeRequest rejects anything over MaxPromptLength - trim the oldest history first
            // rather than fail the message outright (the caller's history list stays untouched;
            // this only affects what's sent to the model this one call).
            if (text.Length <= OllamaServiceClient.MaxPromptLength)
            {
                return text;
            }

            for (var keep = history.Count - 1; keep >= 0; keep--)
            {
                var trimmed = BuildPrompt(systemPrompt, history.Skip(history.Count - keep).ToList(), userText);
                if (trimmed.Length <= OllamaServiceClient.MaxPromptLength)
                {
                    return trimmed;
                }
            }

            // Even bare system prompt + user text doesn't fit - hard-truncate the user text
            // itself. A pathologically long chat.systemPrompt could push this budget to zero or
            // below; fail loudly here rather than silently ship the model an empty user turn -
            // ChatLoop's caller already turns any exception from this call into a plain "try
            // again" reply, so this is still a graceful outcome for the person chatting.
            var budget = OllamaServiceClient.MaxPromptLength - systemPrompt.Length - "User: \nAssistant:".Length - 20;
            if (budget <= 0)
            {
                throw new InvalidOperationException(
                    "chat.systemPrompt is too long to leave any room for a user message - shorten it in settings.json.");
            }
            var shortUserText = userText.Length > budget ? userText[..budget] : userText;
            return BuildPrompt(systemPrompt, [], shortUserText);
        }
    }
}
