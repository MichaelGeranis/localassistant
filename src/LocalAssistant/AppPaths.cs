namespace LocalAssistant
{
    /// <summary>
    /// The one place that knows where the app keeps its files: a single per-user folder holding
    /// the run log, settings, digest history, the last digest's numbering, and the messenger
    /// offset. Deliberately outside the repo and outside <c>bin/Publish</c>, so republishing the
    /// app never clobbers state.
    ///
    /// Every one of these paths used to be spelled out at its point of use, which meant the
    /// folder name lived in five files at once. Moving it (or making it configurable) is now one
    /// edit rather than a search.
    /// </summary>
    public static class AppPaths
    {
        /// <summary><c>%LOCALAPPDATA%\LocalAssistant</c> - the root every file below sits in.</summary>
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalAssistant");

        /// <summary>Behavioural settings (never secrets). See <c>LocalAssistant.Configuration.Settings</c>.</summary>
        public static readonly string SettingsFile = Path.Combine(Root, "settings.json");

        /// <summary>Append-only trace of what each run did - the primary way to debug scheduled runs.</summary>
        public static readonly string RunLogFile = Path.Combine(Root, "digest-run-log.txt");

        /// <summary>One JSON line per digest run. Contains email subjects and summaries - treat as sensitive.</summary>
        public static readonly string DigestHistoryFile = Path.Combine(Root, "digest-history.jsonl");

        /// <summary>The most recently delivered digest's number to message-id mapping, overwritten each run.</summary>
        public static readonly string LastDigestFile = Path.Combine(Root, "last-digest.json");

        /// <summary>The messenger's <c>getUpdates</c> offset, so a restart doesn't replay seen messages.</summary>
        public static readonly string TelegramOffsetFile = Path.Combine(Root, "telegram-offset.txt");

        /// <summary>
        /// Creates <see cref="Root"/> if it isn't there. Every writer calls this immediately
        /// before writing, rather than assuming an earlier component already did.
        /// </summary>
        public static void EnsureRoot() => Directory.CreateDirectory(Root);
    }
}
