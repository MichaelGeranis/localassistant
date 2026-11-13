namespace LocalAssistant.Chat
{
    /// <summary>
    /// Distinguishes <c>Obsidian: digest</c> (summarize the whole vault) from
    /// <c>Obsidian: &lt;anything else&gt;</c> (append that text as a note). Matches whole phrases
    /// only, never a mere mention of the word: "Obsidian: digest the pitch section first" is a
    /// note the user wants saved, not a request to rebuild the digest. Same reasoning as
    /// <see cref="MarkReadCommand"/> - a plain parser rather than an LLM call, because the two
    /// outcomes (append a line vs. overwrite a file) must never be confused.
    /// </summary>
    internal static class VaultDigestCommand
    {
        private static readonly HashSet<string> Phrases = new(StringComparer.OrdinalIgnoreCase)
        {
            "digest",
            "digest vault",
            "digest the vault",
            "vault digest",
            "summary",
            "summarise",
            "summarize",
            "summarise vault",
            "summarize vault",
            "summarise the vault",
            "summarize the vault",
            "vault summary"
        };

        /// <summary>True if an <c>Obsidian:</c> body asks for a vault digest rather than a note.</summary>
        public static bool IsDigestRequest(string body)
        {
            var normalized = body.Trim().TrimEnd('.', '!', '?').Trim();
            return Phrases.Contains(normalized);
        }
    }
}
