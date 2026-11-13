using LocalAssistant.Configuration;

namespace LocalAssistant.Storage
{
    /// <summary>
    /// Backs the chat passthrough's <c>Obsidian:</c> command. An Obsidian vault is just a folder
    /// of plain markdown files, so - like every other piece of local state in this app
    /// (settings.json, digest-history.jsonl, last-digest.json) - "save a note" is nothing more
    /// than a local file append.
    /// </summary>
    public static class ObsidianVaultWriter
    {
        /// <summary>
        /// Appends a timestamped line to <see cref="ObsidianSettings.Filename"/> inside
        /// <see cref="ObsidianSettings.VaultPath"/>.
        /// </summary>
        public static async Task AppendAsync(ObsidianSettings settings, string text)
        {
            if (!Directory.Exists(settings.VaultPath))
            {
                throw new DirectoryNotFoundException($"Obsidian vault folder not found: {settings.VaultPath}");
            }

            var path = Path.Combine(settings.VaultPath, settings.Filename);
            var line = $"- {DateTimeOffset.Now:yyyy-MM-dd HH:mm} {text}{Environment.NewLine}";
            await File.AppendAllTextAsync(path, line);
        }

        /// <summary>
        /// Overwrites <see cref="ObsidianSettings.DigestFilename"/> inside the vault with a freshly
        /// built digest. Overwrite rather than append: the digest is a view of the vault's current
        /// state, so a stale copy underneath it would only ever be wrong.
        /// </summary>
        public static async Task WriteDigestAsync(ObsidianSettings settings, string markdown)
        {
            if (!Directory.Exists(settings.VaultPath))
            {
                throw new DirectoryNotFoundException($"Obsidian vault folder not found: {settings.VaultPath}");
            }

            var path = Path.Combine(settings.VaultPath, settings.DigestFilename);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, markdown);
        }
    }
}
