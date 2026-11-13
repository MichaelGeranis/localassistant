using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAssistant.Diagnostics;

namespace LocalAssistant.Configuration
{
    /// <summary>
    /// Behavioural settings for a run, loaded from <see cref="AppPaths.SettingsFile"/>.
    /// Secrets never live here.
    /// The file sits outside the repo and outside <c>bin/Publish</c>, so republishing the app (which overwrites the publish dir) leaves it untouched.
    ///
    /// Every property carries an explicit <see cref="JsonPropertyNameAttribute"/>: the key in the
    /// JSON file is a contract with a file already sitting on the user's disk, so it must not
    /// move when a C# property is renamed. (It silently did once - a rename left five settings
    /// reading their code defaults, which nothing noticed because a missing key is indistinguishable
    /// from an absent one. Hence also <see cref="LogUnrecognizedKeys"/>.)
    /// </summary>
    public sealed class Settings
    {
        public const string DefaultModel = "llama3.2:3b";
        public const string DefaultDigestLabel = "Digested";
        public const string DefaultOllamaUrl = "http://127.0.0.1:11434";

        [JsonPropertyName("model")]
        public string LLM { get; set; } = DefaultModel;

        [JsonPropertyName("digestLabel")]
        public string EmailDigestLabel { get; set; } = DefaultDigestLabel;

        [JsonPropertyName("ollamaUrl")]
        public string OllamaUrl { get; set; } = DefaultOllamaUrl;

        [JsonPropertyName("includeQuiet")]
        public bool ExpandQuietEmailsInDigest { get; set; }

        [JsonPropertyName("draftReplies")]
        public DraftReplySettings DraftReplies { get; set; } = new();

        [JsonPropertyName("chat")]
        public ChatSettings Chat { get; set; } = new();

        [JsonPropertyName("obsidian")]
        public ObsidianSettings Obsidian { get; set; } = new();

        public static string AppSettingsFilePath => AppPaths.SettingsFile;

        public static Settings Load()
        {
            try
            {
                if (!File.Exists(AppSettingsFilePath))
                {
                    var defaults = new Settings();
                    TryWriteTemplate(defaults);
                    return defaults.WithEnvOverrides();
                }

                var json = File.ReadAllText(AppSettingsFilePath);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings);

                if (loaded is null)
                {
                    RunLog.Append("settings.json deserialized to null; using defaults.");
                    return new Settings().WithEnvOverrides();
                }

                loaded.Normalize();
                LogUnrecognizedKeys(json, loaded);
                return loaded.WithEnvOverrides();
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not load settings.json ({ex.Message}); using defaults.");
                return new Settings().WithEnvOverrides();
            }
        }

        /// <summary>Replace any missing / blank value.</summary>
        private void Normalize()
        {
            if (string.IsNullOrWhiteSpace(LLM)) LLM = DefaultModel;
            if (string.IsNullOrWhiteSpace(EmailDigestLabel)) EmailDigestLabel = DefaultDigestLabel;
            if (string.IsNullOrWhiteSpace(OllamaUrl)) OllamaUrl = DefaultOllamaUrl;
            DraftReplies ??= new DraftReplySettings();
            DraftReplies.Senders ??= new List<string>();
            Chat ??= new ChatSettings();
            Chat.Normalize();
            Obsidian ??= new ObsidianSettings();
            Obsidian.Normalize();
        }

        private Settings WithEnvOverrides()
        {
            var legacy = Environment.GetEnvironmentVariable("DIGEST_INCLUDE_QUIET");
            if (legacy == "1"
                || string.Equals(legacy, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(legacy, "yes", StringComparison.OrdinalIgnoreCase))
            {
                ExpandQuietEmailsInDigest = true;
            }
            return this;
        }

        /// <summary>
        /// Logs any key in the file that didn't bind to a property, so a typo or a stale key
        /// (from an older version of the app) is visible in the run log instead of silently
        /// reading as "absent, use the default" - the failure mode that makes a defaults-for-
        /// everything config so quietly wrong.
        ///
        /// The set of valid keys is whatever serializing <paramref name="loaded"/> produces, so
        /// this stays correct on its own as properties come and go - no second list to maintain.
        /// Best-effort by design: a problem here must never cost a run.
        /// </summary>
        private static void LogUnrecognizedKeys(string fileJson, Settings loaded)
        {
            try
            {
                using var fromFile = JsonDocument.Parse(fileJson);
                using var recognized = JsonDocument.Parse(
                    JsonSerializer.Serialize(loaded, SettingsJsonContext.Default.Settings));

                var unknown = new List<string>();
                CollectUnrecognized(fromFile.RootElement, recognized.RootElement, prefix: string.Empty, unknown);

                if (unknown.Count > 0)
                {
                    RunLog.Append(
                        $"settings.json: {unknown.Count} key(s) not recognized and ignored ({string.Join(", ", unknown)}) "
                        + "- those settings are using their code defaults. Check settings.example.json for the current names.");
                }
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not check settings.json for unrecognized keys ({ex.Message}); continuing.");
            }
        }

        private static void CollectUnrecognized(JsonElement fromFile, JsonElement recognized, string prefix, List<string> unknown)
        {
            if (fromFile.ValueKind != JsonValueKind.Object || recognized.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in fromFile.EnumerateObject())
            {
                var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                var match = FindPropertyIgnoreCase(recognized, property.Name);

                if (match is null)
                {
                    unknown.Add(path);
                    continue;
                }

                // Only descend into nested objects; arrays hold values, not keys.
                CollectUnrecognized(property.Value, match.Value, path, unknown);
            }
        }

        /// <summary>Deserialization is case-insensitive, so the check has to be too.</summary>
        private static JsonElement? FindPropertyIgnoreCase(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }
            return null;
        }

        private static void TryWriteTemplate(Settings settings)
        {
            try
            {
                AppPaths.EnsureRoot();
                File.WriteAllText(
                    AppSettingsFilePath,
                    JsonSerializer.Serialize(settings, SettingsJsonContext.Default.Settings));
                RunLog.Append($"Wrote a default settings.json to {AppSettingsFilePath}.");
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not write a default settings.json ({ex.Message}); using in-memory defaults.");
            }
        }
    }

    public sealed class DraftReplySettings
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("senders")]
        public List<string> Senders { get; set; } = new();
    }

    public sealed class ChatSettings
    {
        public const int DefaultLongPollSeconds = 25;
        public const int MaxLongPollSeconds = 50;
        public const int DefaultHistoryTurns = 10;
        public const string DefaultSystemPrompt = "You are a concise, helpful assistant running locally.";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        /// <summary>
        /// Each call to the messenger holds the
        /// connection open server-side and returns the instant a message arrives, or after this
        /// many seconds if none do - so replies are near-instant without polling in a tight
        /// interval loop. Capped at <see cref="MaxLongPollSeconds"/>, comfortably under the ~60s
        /// range where proxies/load balancers tend to kill an idle HTTP connection.
        /// </summary>
        [JsonPropertyName("longPollSeconds")]
        public int LongPollSeconds { get; set; } = DefaultLongPollSeconds;

        /// <summary>Prepended to every chat prompt.</summary>
        [JsonPropertyName("systemPrompt")]
        public string PrefixedPrompt { get; set; } = DefaultSystemPrompt;

        /// <summary>How many prior user/assistant turns to keep in the rolling in-memory transcript.</summary>
        [JsonPropertyName("historyTurns")]
        public int HistoryTurns { get; set; } = DefaultHistoryTurns;

        internal void Normalize()
        {
            if (LongPollSeconds < 1 || LongPollSeconds > MaxLongPollSeconds) LongPollSeconds = DefaultLongPollSeconds;
            if (string.IsNullOrWhiteSpace(PrefixedPrompt)) PrefixedPrompt = DefaultSystemPrompt;
            if (HistoryTurns < 0) HistoryTurns = DefaultHistoryTurns;
        }
    }

    public sealed class ObsidianSettings
    {
        public const string DefaultInboxFile = "Inbox.md";
        public const string DefaultDigestFile = "Vault Digest.md";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("vaultPath")]
        public string VaultPath { get; set; } = string.Empty;

        [JsonPropertyName("inboxFile")]
        public string Filename { get; set; } = DefaultInboxFile;

        /// <summary>
        /// File the <c>Obsidian: digest</c> command overwrites with the vault digest, and the one
        /// file the digest's own read skips - so a digest is never built from the last digest.
        /// </summary>
        [JsonPropertyName("digestFile")]
        public string DigestFilename { get; set; } = DefaultDigestFile;

        internal void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Filename)) Filename = DefaultInboxFile;
            if (string.IsNullOrWhiteSpace(DigestFilename)) DigestFilename = DefaultDigestFile;
        }
    }

    [JsonSourceGenerationOptions(
        WriteIndented = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(Settings))]
    internal partial class SettingsJsonContext : JsonSerializerContext
    {
    }
}
