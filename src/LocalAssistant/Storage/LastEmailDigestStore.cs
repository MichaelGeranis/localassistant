using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAssistant.Diagnostics;
using LocalAssistant.Digest;

namespace LocalAssistant.Storage
{
    /// <summary>
    /// Persists the number Gmail message id mapping from the most recently delivered digest.
    /// </summary>
    public static class LastEmailDigestStore
    {
        private static string FilePath => AppPaths.LastDigestFile;

        /// <summary>Overwrites the stored state with this digest's numbering.</summary>
        public static async Task SaveAsync(EmailDigest digest)
        {
            try
            {
                var state = new LastEmailDigest
                {
                    GeneratedAt = digest.GeneratedAt,
                    Items = digest.NumberedItems
                        .Select(pair => new LastEmailDigestItem
                        {
                            Number = pair.Number,
                            Id = pair.Item.Id,
                            Subject = pair.Item.Subject
                        })
                        .ToList()
                };

                AppPaths.EnsureRoot();
                await File.WriteAllTextAsync(
                    FilePath,
                    JsonSerializer.Serialize(state, LastEmailDigestJsonContext.Default.LastEmailDigest));
            }
            catch (Exception ex)
            {
                // Non-fatal: worst case a later "mark as read" command finds nothing to reference.
                RunLog.Append($"Could not persist last-digest state: {ex.Message}");
            }
        }

        /// <summary>Loads the last saved state, or null if there isn't one / it can't be read.</summary>
        public static async Task<LastEmailDigest?> LoadAsync()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return null;
                }
                var json = await File.ReadAllTextAsync(FilePath);
                return JsonSerializer.Deserialize(json, LastEmailDigestJsonContext.Default.LastEmailDigest);
            }
            catch (Exception ex)
            {
                RunLog.Append($"Could not read last-digest state: {ex.Message}");
                return null;
            }
        }
    }

    public sealed class LastEmailDigest
    {
        public DateTimeOffset GeneratedAt { get; set; }
        public List<LastEmailDigestItem> Items { get; set; } = new();
    }

    public sealed class LastEmailDigestItem
    {
        public int Number { get; set; }
        public string Id { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(LastEmailDigest))]
    internal partial class LastEmailDigestJsonContext : JsonSerializerContext
    {
    }
}
