namespace LocalAssistant.Chat
{
    /// <summary>
    /// The service-prefix command grammar used across chat passthrough
    /// </summary>
    internal enum ChatCommandTarget
    {
        Obsidian,
        Gmail,
    }

    internal static class ChatCommandRouter
    {
        private static readonly Dictionary<string, ChatCommandTarget> Prefixes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["obsidian"] = ChatCommandTarget.Obsidian,
            ["gmail"] = ChatCommandTarget.Gmail,
        };

        /// <summary>
        /// True if <paramref name="text"/> starts with a registered "&lt;Service&gt;:" prefix,
        /// with <paramref name="target"/> the matched service and <paramref name="body"/> whatever
        /// follows the first colon, trimmed. Only the text before the first colon is matched
        /// against the prefix table, so a body containing its own colons (times, URLs, ...) is
        /// passed through untouched.
        /// </summary>
        public static bool TryParse(string text, out ChatCommandTarget target, out string body)
        {
            target = default;
            body = string.Empty;

            var colon = text.IndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            var prefix = text[..colon].Trim();
            if (!Prefixes.TryGetValue(prefix, out target))
            {
                return false;
            }

            body = text[(colon + 1)..].Trim();
            return true;
        }
    }
}
