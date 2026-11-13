using System.Text;
using LocalAssistant.Configuration;
using LocalAssistant.Diagnostics;
using LocalAssistant.Storage;
using Ollama.Apis.v1;

namespace LocalAssistant.VaultDigest
{
    /// <summary>One note plus what the model made of it.</summary>
    public sealed class VaultDigestItem
    {
        public required VaultNote Note { get; init; }

        public required string Summary { get; init; }
    }

    /// <summary>The finished vault digest: per-note summaries, an overview, and the markdown file body.</summary>
    public sealed class VaultDigest
    {
        public required DateTimeOffset GeneratedAt { get; init; }

        public required string Model { get; init; }

        public required IReadOnlyList<VaultDigestItem> Items { get; init; }

        /// <summary>Cross-note synthesis, or empty when that pass failed or there was nothing to synthesize.</summary>
        public required string Overview { get; init; }

        public int NoteCount => Items.Count;

        /// <summary>
        /// The digest as the markdown written into the vault. Note titles are
        /// <c>[[wikilinks]]</c>, so the digest is a working index inside Obsidian rather than a
        /// dead-end summary.
        /// </summary>
        public string Markdown
        {
            get
            {
                var text = new StringBuilder();
                text.AppendLine("# Vault Digest");
                text.AppendLine();
                text.AppendLine($"*Generated {GeneratedAt:yyyy-MM-dd HH:mm} · {NoteCount} note(s) · {Model}*");
                text.AppendLine();
                text.AppendLine("> [!note] Written by LocalAssistant. Overwritten on the next `Obsidian: digest`, so don't edit by hand.");
                text.AppendLine();

                if (Overview.Length > 0)
                {
                    text.AppendLine("## Overview");
                    text.AppendLine();
                    text.AppendLine(Overview);
                    text.AppendLine();
                }

                text.AppendLine("## Notes");
                text.AppendLine();
                foreach (var item in Items)
                {
                    text.AppendLine($"### [[{item.Note.Title}]]");
                    text.AppendLine();
                    text.AppendLine($"*{item.Note.RelativePath} · updated {item.Note.ModifiedAt:yyyy-MM-dd}*");
                    text.AppendLine();
                    text.AppendLine(item.Summary);
                    text.AppendLine();
                }

                return text.ToString();
            }
        }

        /// <summary>Short confirmation for the chat reply - the overview, not the whole file.</summary>
        public string ChatSummary => Overview.Length > 0
            ? $"📓 Digested {NoteCount} note(s).\n\n{Overview}"
            : $"📓 Digested {NoteCount} note(s).";
    }

    /// <summary>
    /// Summarizes a whole Obsidian vault the way <c>EmailDigestService</c> summarizes an inbox:
    /// one cheap Ollama call per item, then one pass over the summaries. Keeping the per-note pass
    /// separate is what lets the vault outgrow a single context window - only the summaries, never
    /// the full notes, reach the overview call.
    /// </summary>
    public static class VaultDigestService
    {
        /// <summary>
        /// A note this short is already its own summary - sending three words to a 3B model invites
        /// invention rather than compression, and costs a round trip to get it.
        /// </summary>
        private const int MinLengthToSummarize = 120;

        /// <summary>~4 chars per token: 6000 chars ≈ 1500 tokens, leaving room in ConciseSummary's 4096 context.</summary>
        private const int NoteBudget = 6000;

        public static async Task<VaultDigest> BuildAsync(
            OllamaServiceClient ollamaClient, ObsidianSettings obsidian, string model)
        {
            var notes = ObsidianVaultReader.ReadNotes(obsidian);
            var items = new List<VaultDigestItem>(notes.Count);

            foreach (var note in notes)
            {
                items.Add(new VaultDigestItem
                {
                    Note = note,
                    Summary = await SummarizeAsync(ollamaClient, note, model)
                });
            }

            return new VaultDigest
            {
                GeneratedAt = DateTimeOffset.Now,
                Model = model,
                Items = items,
                Overview = await SummarizeAcrossNotesAsync(ollamaClient, items, model)
            };
        }

        private static async Task<string> SummarizeAsync(
            OllamaServiceClient ollamaClient, VaultNote note, string model)
        {
            if (note.Text.Length == 0)
            {
                return "(empty note)";
            }
            if (note.Text.Length < MinLengthToSummarize)
            {
                return note.Text.ReplaceLineEndings(" ");
            }

            var instructions =
                "You are summarizing one note from a personal knowledge vault for an index of that "
                + "vault. Write TWO or THREE plain sentences covering what the note is about and any "
                + "open question or decision it leaves. Ignore markdown syntax, links and image "
                + "embeds. Do not use headings or bullet points. Reply with the summary only, no "
                + "preamble.\n\n"
                + $"Note title: {note.Title}\n"
                + "Note:\n";

            try
            {
                var summary = (await ollamaClient.MakeRequest(
                    promptText: instructions + Truncate(note.Text, instructions),
                    options: OllamaRequestOptions.ConciseSummary,
                    think: false,
                    model: model)).Trim();

                return summary.Length > 0 ? summary : "(no summary generated)";
            }
            catch (Exception ex)
            {
                // Per-note tolerance, as in the email digest: one failed call costs one summary.
                RunLog.Append($"Vault note summary failed for \"{note.RelativePath}\": {ex.Message}");
                return $"(summary failed: {ex.Message})";
            }
        }

        /// <summary>
        /// The cross-note pass. Reuses the <c>Drafting</c> preset rather than adding a fifth one:
        /// this is exactly what that preset is tuned for - a short piece of prose with a 400-token
        /// budget and slightly more warmth than the extraction presets.
        /// </summary>
        private static async Task<string> SummarizeAcrossNotesAsync(
            OllamaServiceClient ollamaClient, IReadOnlyList<VaultDigestItem> items, string model)
        {
            if (items.Count < 2)
            {
                return string.Empty; // Nothing to synthesize across; the single summary already says it.
            }

            var instructions =
                "Below are summaries of every note in a personal knowledge vault. Write a short "
                + "overview - at most one paragraph - of what this vault is about as a whole, the "
                + "themes the notes share, and anything that looks unfinished or unresolved across "
                + "them. Do not list the notes one by one. Plain prose, no headings or bullet "
                + "points, no preamble.\n\n";

            var body = new StringBuilder();
            foreach (var item in items)
            {
                body.AppendLine($"{item.Note.Title}: {item.Summary}");
                body.AppendLine();
            }

            try
            {
                return (await ollamaClient.MakeRequest(
                    promptText: instructions + Truncate(body.ToString(), instructions),
                    options: OllamaRequestOptions.Drafting,
                    think: false,
                    model: model)).Trim();
            }
            catch (Exception ex)
            {
                // The per-note summaries are the digest; an overview is a bonus, so losing it is
                // not worth failing the run over.
                RunLog.Append($"Vault digest overview failed: {ex.Message}");
                return string.Empty;
            }
        }

        private static string Truncate(string text, string instructions)
        {
            var budget = Math.Min(NoteBudget, OllamaServiceClient.MaxPromptLength - instructions.Length - 100);
            var trimmed = text.Trim();
            return trimmed.Length > budget ? trimmed[..budget] : trimmed;
        }
    }
}
