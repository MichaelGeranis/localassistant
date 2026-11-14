using LocalAssistant.Configuration;

namespace LocalAssistant.Tests;

/// <summary>
/// Secrets come from the credential store first and the environment second, and migrating moves
/// them across without ever losing one.
/// </summary>
public class SecretSourceTests
{
    private sealed class FakeStore : ICredentialStore
    {
        public Dictionary<string, string> Entries { get; } = new();

        public bool IsAvailable { get; init; } = true;

        public string? Read(string target) => Entries.GetValueOrDefault(target);

        public void Write(string target, string secret) => Entries[target] = secret;
    }

    private static SecretSource Source(FakeStore store, Dictionary<string, string> environment) =>
        new(store, name => environment.GetValueOrDefault(name));

    [Fact]
    public void Get_WhenStoredAndInEnvironment_ShouldPreferTheStore()
    {
        var store = new FakeStore();
        store.Entries[SecretSource.TargetName("TELEGRAM_BOT_TOKEN")] = "from-store";

        var value = Source(store, new() { ["TELEGRAM_BOT_TOKEN"] = "from-env" }).Get("TELEGRAM_BOT_TOKEN");

        Assert.Equal("from-store", value);
    }

    [Fact]
    public void Get_WhenOnlyInEnvironment_ShouldFallBackToIt()
    {
        var value = Source(new FakeStore(), new() { ["TELEGRAM_BOT_TOKEN"] = "from-env" }).Get("TELEGRAM_BOT_TOKEN");

        Assert.Equal("from-env", value);
    }

    [Fact]
    public void Get_WhenSetNowhere_ShouldReturnNull()
    {
        Assert.Null(Source(new FakeStore(), new()).Get("GMAIL_REFRESH_TOKEN"));
    }

    [Fact]
    public void Migrate_ShouldMoveEachVariableAndRemoveIt()
    {
        var store = new FakeStore();
        var userVariables = new Dictionary<string, string> { ["GMAIL_REFRESH_TOKEN"] = "refresh", ["TELEGRAM_CHAT_ID"] = " 42 " };
        var source = Source(store, new());

        var (moved, alreadyStored) = source.Migrate(name => userVariables.GetValueOrDefault(name), name => userVariables.Remove(name));

        Assert.Equal(new[] { "GMAIL_REFRESH_TOKEN", "TELEGRAM_CHAT_ID" }, moved);
        Assert.Empty(alreadyStored);
        Assert.Empty(userVariables);
        Assert.Equal("refresh", source.Get("GMAIL_REFRESH_TOKEN"));
        Assert.Equal("42", source.Get("TELEGRAM_CHAT_ID"));
    }

    [Fact]
    public void Migrate_WhenTheStoreHoldsADifferentValue_ShouldKeepTheVariable()
    {
        var store = new FakeStore();
        store.Entries[SecretSource.TargetName("GMAIL_REFRESH_TOKEN")] = "newer";
        var userVariables = new Dictionary<string, string> { ["GMAIL_REFRESH_TOKEN"] = "older" };

        var (moved, alreadyStored) = Source(store, new()).Migrate(name => userVariables.GetValueOrDefault(name), name => userVariables.Remove(name));

        Assert.Empty(moved);
        Assert.Equal(new[] { "GMAIL_REFRESH_TOKEN" }, alreadyStored);
        Assert.Equal("older", userVariables["GMAIL_REFRESH_TOKEN"]);
        Assert.Equal("newer", store.Entries[SecretSource.TargetName("GMAIL_REFRESH_TOKEN")]);
    }

    [Fact]
    public void Migrate_WithoutACredentialStore_ShouldThrowAndChangeNothing()
    {
        var userVariables = new Dictionary<string, string> { ["GMAIL_REFRESH_TOKEN"] = "refresh" };
        var source = Source(new FakeStore { IsAvailable = false }, new());

        Assert.Throws<PlatformNotSupportedException>(() =>
            source.Migrate(name => userVariables.GetValueOrDefault(name), name => userVariables.Remove(name)));
        Assert.True(userVariables.ContainsKey("GMAIL_REFRESH_TOKEN"));
    }

    [Fact]
    public void Set_WithAnUnknownName_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => Source(new FakeStore(), new()).Set("GMAIL_PASSWORD", "x"));
    }

    [Fact]
    public void SecretsStatus_ShouldNameWhereEachSecretIsKept_AndNeverItsValue()
    {
        var store = new FakeStore();
        store.Entries[SecretSource.TargetName("GMAIL_REFRESH_TOKEN")] = "very-secret-refresh";
        var output = new StringWriter();

        var exit = SecretsCommand.Run(["status"], Source(store, new() { ["TELEGRAM_BOT_TOKEN"] = "very-secret-bot" }), output);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("GMAIL_REFRESH_TOKEN", text);
        Assert.Contains("Credential Manager", text);
        Assert.Contains("environment variable", text);
        Assert.DoesNotContain("very-secret", text);
    }

    [Fact]
    public void WindowsCredentialStore_ShouldRoundTripASecret()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Credential Manager exists only on Windows.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialStore();
        var target = $"LocalAssistant.Tests/{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Read(target));
            store.Write(target, "secret-value-ü");
            Assert.Equal("secret-value-ü", store.Read(target));
        }
        finally
        {
            store.Delete(target);
        }
    }
}
