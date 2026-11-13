using LocalAssistant.Configuration;
using LocalAssistant.Diagnostics;

namespace LocalAssistant.Storage
{
    /// <summary>One markdown note read off disk.</summary>
    public sealed class VaultNote
    {
        /// <summary>File name without the .md extension - also what an Obsidian [[wikilink]] targets.</summary>
        public required string Title { get; init; }

        /// <summary>Path relative to the vault root, for notes tucked in subfolders.</summary>
        public required string RelativePath { get; init; }

        public required string Text { get; init; }

        public required DateTimeOffset ModifiedAt { get; init; }
    }

    /// <summary>
    /// The read half of <see cref="ObsidianVaultWriter"/>. A vault is a folder of plain markdown,
    /// so reading it is a directory walk - no API, no credential, same as the append path.
    /// </summary>
    public static class ObsidianVaultReader
    {
        /// <summary>
        /// Ceiling on notes returned by one read. The vault digest makes an Ollama call per note,
        /// so an unexpectedly huge vault would otherwise turn one chat message into a very long
        /// job; newest-first ordering means the cap drops the stalest notes, not arbitrary ones.
        /// </summary>
        public const int MaxNotes = 100;

        /// <summary>
        /// Every <c>.md</c> file in the vault, newest first. Skips dot-folders (<c>.obsidian</c>,
        /// <c>.trash</c>) and the digest file itself - digesting a digest would compound the
        /// model's own paraphrasing run after run.
        /// </summary>
        public static IReadOnlyList<VaultNote> ReadNotes(ObsidianSettings settings)
        {
            if (!Directory.Exists(settings.VaultPath))
            {
                throw new DirectoryNotFoundException($"Obsidian vault folder not found: {settings.VaultPath}");
            }

            var root = Path.GetFullPath(settings.VaultPath);
            var digestPath = Path.GetFullPath(Path.Combine(root, settings.DigestFilename));

            var notes = new List<VaultNote>();
            foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(path);
                var relative = Path.GetRelativePath(root, full);

                if (string.Equals(full, digestPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            .Any(segment => segment.StartsWith('.')))
                {
                    continue;
                }

                try
                {
                    notes.Add(new VaultNote
                    {
                        Title = Path.GetFileNameWithoutExtension(full),
                        RelativePath = relative,
                        Text = File.ReadAllText(full).Trim(),
                        ModifiedAt = File.GetLastWriteTime(full)
                    });
                }
                catch (Exception ex)
                {
                    // One unreadable note (locked by a sync client, odd permissions) must not cost
                    // the whole digest - the same per-item tolerance the email pipeline has.
                    RunLog.Append($"Skipped vault note \"{relative}\": {ex.Message}");
                }
            }

            if (notes.Count > MaxNotes)
            {
                RunLog.Append($"Vault has {notes.Count} notes; digesting the {MaxNotes} most recently modified.");
            }

            return notes
                .OrderByDescending(note => note.ModifiedAt)
                .Take(MaxNotes)
                .ToList();
        }
    }
}
