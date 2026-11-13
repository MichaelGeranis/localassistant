using LocalAssistant.Digest;

namespace LocalAssistant.Chat
{
    /// <summary>
    /// The chat-side syntax for "mark digest items as read": the bare <c>/read</c> shortcut,
    /// checked before <see cref="ChatCommandRouter"/> since it's already unambiguous. The
    /// <c>Gmail:</c> prefix reaches the same behaviour through the router.
    ///
    /// Only the shortcut itself lives here - which numbers a phrase names is
    /// <see cref="DigestNumberParser"/>'s job, on the digest's side of the fence.
    /// </summary>
    internal static class MarkReadCommand
    {
        /// <summary>
        /// True if <paramref name="text"/> is the bare <c>/read ...</c> shortcut, with the
        /// distinct digest numbers it named (1-based) in <paramref name="numbers"/>.
        /// </summary>
        public static bool TryParse(string text, out IReadOnlyList<int> numbers)
        {
            numbers = Array.Empty<int>();
            var trimmed = text.Trim();

            if (!trimmed.StartsWith("/read", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return DigestNumberParser.TryParseNumbers(trimmed[5..], out numbers);
        }
    }
}
