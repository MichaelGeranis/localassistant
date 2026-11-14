using LocalAssistant.Diagnostics;

namespace LocalAssistant.Configuration
{
    /// <summary>
    /// Where secrets are read from: Windows Credential Manager first, then (for setups made
    /// before it existed) a user environment variable.
    ///
    /// An environment variable can be read by any process running as the user and is copied into
    /// every child process; a Credential Manager entry is encrypted with the user's logon (DPAPI)
    /// and read only on request. <c>LocalAssistant secrets migrate</c> moves existing variables
    /// across. Off Windows there is no credential store, so only the environment is read.
    /// </summary>
    public sealed class SecretSource
    {
        public const string GmailClientId = "GMAIL_CLIENT_ID";
        public const string GmailClientSecret = "GMAIL_CLIENT_SECRET";
        public const string GmailRefreshToken = "GMAIL_REFRESH_TOKEN";
        public const string TelegramBotToken = "TELEGRAM_BOT_TOKEN";
        public const string TelegramChatId = "TELEGRAM_CHAT_ID";

        /// <summary>Every secret the app reads, by the name used for both stores.</summary>
        public static readonly IReadOnlyList<string> Names =
            [GmailClientId, GmailClientSecret, GmailRefreshToken, TelegramBotToken, TelegramChatId];

        private readonly ICredentialStore _store;
        private readonly Func<string, string?> _environment;
        private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

        public SecretSource(ICredentialStore store, Func<string, string?> environment)
        {
            _store = store;
            _environment = environment;
        }

        /// <summary>The app's source: Credential Manager on Windows, then the process environment.</summary>
        public static SecretSource Default { get; } = new(
            OperatingSystem.IsWindows() ? new WindowsCredentialStore() : new NoCredentialStore(),
            Environment.GetEnvironmentVariable);

        public bool HasCredentialStore => _store.IsAvailable;

        /// <summary>The credential's name in Credential Manager.</summary>
        public static string TargetName(string name) => $"LocalAssistant/{name}";

        /// <summary>The secret's value, or null when it is set nowhere.</summary>
        public string? Get(string name)
        {
            var stored = _store.Read(TargetName(name));
            if (!string.IsNullOrEmpty(stored))
            {
                return stored;
            }

            var fromEnvironment = _environment(name);
            if (string.IsNullOrEmpty(fromEnvironment))
            {
                return null;
            }

            if (_store.IsAvailable && _warned.Add(name))
            {
                RunLog.Append($"{name} is read from an environment variable. Run `LocalAssistant secrets migrate` to move it into Windows Credential Manager.");
            }
            return fromEnvironment;
        }

        /// <summary>Where each secret currently comes from - never its value.</summary>
        public IReadOnlyList<(string Name, string Source)> Describe() =>
            Names.Select(name => (name,
                !string.IsNullOrEmpty(_store.Read(TargetName(name))) ? "Credential Manager"
                : !string.IsNullOrEmpty(_environment(name)) ? "environment variable"
                : "not set")).ToList();

        /// <summary>Stores a secret in the credential store.</summary>
        public void Set(string name, string value)
        {
            EnsureKnown(name);
            if (!_store.IsAvailable)
            {
                throw new PlatformNotSupportedException("There is no credential store on this system; set an environment variable instead.");
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A secret cannot be empty.", nameof(value));
            }

            _store.Write(TargetName(name), value.Trim());
        }

        /// <summary>
        /// Copies every secret set as a user environment variable into the credential store, and
        /// removes the variable once the stored copy reads back identical.
        /// </summary>
        /// <param name="readUserVariable">Reads a user-scope environment variable</param>
        /// <param name="removeUserVariable">Removes a user-scope environment variable</param>
        /// <returns>The names moved, and the names left alone because they are already stored</returns>
        public (IReadOnlyList<string> Moved, IReadOnlyList<string> AlreadyStored) Migrate(
            Func<string, string?> readUserVariable, Action<string> removeUserVariable)
        {
            if (!_store.IsAvailable)
            {
                throw new PlatformNotSupportedException("There is no credential store on this system.");
            }

            var moved = new List<string>();
            var alreadyStored = new List<string>();
            foreach (var name in Names)
            {
                var value = readUserVariable(name);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var existing = _store.Read(TargetName(name));
                if (!string.IsNullOrEmpty(existing) && existing != value.Trim())
                {
                    // Credential Manager wins at runtime; keep the variable so nothing is lost silently
                    alreadyStored.Add(name);
                    continue;
                }

                _store.Write(TargetName(name), value.Trim());
                if (_store.Read(TargetName(name)) != value.Trim())
                {
                    throw new InvalidOperationException($"{name} did not read back from Credential Manager; the environment variable was kept.");
                }

                removeUserVariable(name);
                moved.Add(name);
            }

            return (moved, alreadyStored);
        }

        private static void EnsureKnown(string name)
        {
            if (!Names.Contains(name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown secret '{name}'. Known: {string.Join(", ", Names)}.", nameof(name));
            }
        }
    }

    /// <summary>A store for small secrets, keyed by name.</summary>
    public interface ICredentialStore
    {
        bool IsAvailable { get; }

        string? Read(string target);

        void Write(string target, string secret);
    }

    /// <summary>Used where the OS has no credential store the app supports.</summary>
    public sealed class NoCredentialStore : ICredentialStore
    {
        public bool IsAvailable => false;

        public string? Read(string target) => null;

        public void Write(string target, string secret) =>
            throw new PlatformNotSupportedException("There is no credential store on this system.");
    }
}
