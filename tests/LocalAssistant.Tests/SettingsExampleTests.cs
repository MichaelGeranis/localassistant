using System.Text.Json;
using LocalAssistant.Configuration;

namespace LocalAssistant.Tests;

/// <summary>
/// settings.example.json documents the settings file. Every key in it must bind to a setting,
/// or a user copying it would get code defaults without noticing.
/// </summary>
public class SettingsExampleTests
{
    private static readonly string ExampleJson =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "settings.example.json"));

    [Fact]
    public void Example_ShouldHaveOnlyKeysThatBindToSettings()
    {
        var loaded = JsonSerializer.Deserialize(ExampleJson, SettingsJsonContext.Default.Settings);
        Assert.NotNull(loaded);

        var recognized = JsonSerializer.SerializeToElement(loaded, SettingsJsonContext.Default.Settings);
        using var example = JsonDocument.Parse(ExampleJson);

        Assert.Empty(UnknownKeys(example.RootElement, recognized, ""));
    }

    [Fact]
    public void Example_ShouldDocumentEverySetting()
    {
        var recognized = JsonSerializer.SerializeToElement(new Settings(), SettingsJsonContext.Default.Settings);
        using var example = JsonDocument.Parse(ExampleJson);

        Assert.Empty(UnknownKeys(recognized, example.RootElement, ""));
    }

    private static List<string> UnknownKeys(JsonElement from, JsonElement known, string prefix)
    {
        var unknown = new List<string>();
        foreach (var property in from.EnumerateObject())
        {
            var match = known.EnumerateObject()
                .Where(k => string.Equals(k.Name, property.Name, StringComparison.OrdinalIgnoreCase))
                .Select(k => (JsonElement?)k.Value)
                .FirstOrDefault();
            if (match is null)
            {
                unknown.Add(prefix + property.Name);
            }
            else if (property.Value.ValueKind == JsonValueKind.Object && match.Value.ValueKind == JsonValueKind.Object)
            {
                unknown.AddRange(UnknownKeys(property.Value, match.Value, prefix + property.Name + "."));
            }
        }
        return unknown;
    }
}
