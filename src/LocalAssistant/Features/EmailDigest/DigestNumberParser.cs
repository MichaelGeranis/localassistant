using System.Text.RegularExpressions;

namespace LocalAssistant.Digest
{
    /// <summary>
    /// Reads the digest's own vocabulary - the numbers printed next to items in a rendered
    /// digest (see <see cref="EmailDigest.NumberedItems"/>) - out of a free-text phrase.
    /// Recognizes digit runs (with optional 1st/2nd/3rd suffixes), ordinal words ("first",
    /// "second", ...), and <c>N-M</c> / <c>N to M</c> / <c>N through M</c> ranges.
    ///
    /// Lives with the digest rather than with the chat commands that call it: what a digest
    /// number means is the digest's business, and a second caller (a "draft a reply to 2"
    /// command, say) shouldn't have to reach into the chat feature to ask. Chat owns the
    /// surface syntax - a <c>Gmail:</c> prefix, a <c>/read</c> shortcut - and hands the rest here.
    ///
    /// Deliberately a plain parser, not an LLM call: this feeds a real Gmail mutation, so it
    /// needs to be predictable and auditable rather than occasionally creative.
    /// </summary>
    internal static class DigestNumberParser
    {
        private static readonly Dictionary<string, int> WordOrdinals = new(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1,
            ["second"] = 2,
            ["third"] = 3,
            ["fourth"] = 4,
            ["fifth"] = 5,
            ["sixth"] = 6,
            ["seventh"] = 7,
            ["eighth"] = 8,
            ["ninth"] = 9,
            ["tenth"] = 10,
            ["eleventh"] = 11,
            ["twelfth"] = 12,
            ["thirteenth"] = 13,
            ["fourteenth"] = 14,
            ["fifteenth"] = 15,
            ["sixteenth"] = 16,
            ["seventeenth"] = 17,
            ["eighteenth"] = 18,
            ["nineteenth"] = 19,
            ["twentieth"] = 20
        };

        // "3-5" or "3 to 5" / "3 through 5" -> expanded to "3 4 5" before tokenizing.
        /// <summary>The longest range accepted; far more items than any one digest holds.</summary>
        internal const int MaxRangeLength = 100;

        private static readonly Regex RangeExpander = new(@"(?<a>\d+)\s*(?:-|to|through)\s*(?<b>\d+)", RegexOptions.IgnoreCase);

        // A digit run (optionally with an ordinal suffix) or a bare word - the word alternative is
        // what lets WordOrdinals match "first", "second", etc.
        private static readonly Regex Token = new(@"\d+(?:st|nd|rd|th)?|[A-Za-z]+", RegexOptions.Compiled);

        /// <summary>
        /// Extracts the distinct digest numbers (1-based, ascending) named in
        /// <paramref name="text"/>. False if there are none - e.g. "archive everything" - or if a
        /// range can't be a run of digest items ("0-3", "1-500"); the caller should report either
        /// back as an unrecognized command rather than silently doing nothing, or doing part of it.
        /// </summary>
        public static bool TryParseNumbers(string text, out IReadOnlyList<int> numbers)
        {
            numbers = Array.Empty<int>();

            // A range that can't be a run of digest items rejects the whole phrase. Leaving it in
            // place would let the token scan below pick up its two ends, so "1-500" would mark
            // items 1 and 500 - a mutation the user never asked for.
            var validRanges = true;
            text = RangeExpander.Replace(text, match =>
            {
                if (!int.TryParse(match.Groups["a"].Value, out var a) || !int.TryParse(match.Groups["b"].Value, out var b))
                {
                    validRanges = false;
                    return match.Value;
                }
                var (from, to) = a <= b ? (a, b) : (b, a); // "5-3" means 3, 4 and 5
                if (from < 1 || to - from > MaxRangeLength)
                {
                    validRanges = false;
                    return match.Value;
                }
                return string.Join(' ', Enumerable.Range(from, to - from + 1));
            });

            if (!validRanges)
            {
                return false;
            }

            var found = new SortedSet<int>();
            foreach (Match match in Token.Matches(text))
            {
                var token = match.Value;
                var digits = Regex.Match(token, @"\d+").Value;
                if (digits.Length > 0 && int.TryParse(digits, out var n) && n > 0)
                {
                    found.Add(n);
                }
                else if (WordOrdinals.TryGetValue(token, out var wordNumber))
                {
                    found.Add(wordNumber);
                }
            }

            if (found.Count == 0)
            {
                return false;
            }

            numbers = found.ToList();
            return true;
        }
    }
}
