using System.Text;

namespace LocalAssistant.Configuration
{
    /// <summary>
    /// <c>LocalAssistant secrets ...</c>: puts the app's secrets in Windows Credential Manager.
    /// Prints names and where they are kept, never a value.
    /// </summary>
    internal static class SecretsCommand
    {
        private static readonly string Usage =
            "Usage:\n"
            + "  LocalAssistant secrets status        Where each secret is kept (never its value)\n"
            + "  LocalAssistant secrets set NAME      Store NAME in Credential Manager; the value is read from the console, hidden\n"
            + "  LocalAssistant secrets migrate       Move secrets from user environment variables into Credential Manager\n"
            + $"Names: {string.Join(", ", SecretSource.Names)}";

        public static int Run(IReadOnlyList<string> args, SecretSource secrets, TextWriter output)
        {
            var command = args.Count > 0 ? args[0].ToLowerInvariant() : "status";
            try
            {
                switch (command)
                {
                    case "status":
                        foreach (var (name, source) in secrets.Describe())
                        {
                            output.WriteLine($"{name,-22} {source}");
                        }
                        if (!secrets.HasCredentialStore)
                        {
                            output.WriteLine("(No credential store on this system: secrets come from environment variables.)");
                        }
                        return 0;

                    case "set" when args.Count == 2:
                        output.Write($"Value for {args[1]}: ");
                        var value = ReadHidden();
                        output.WriteLine();
                        secrets.Set(args[1], value);
                        output.WriteLine($"{args[1]} stored in Credential Manager as {SecretSource.TargetName(args[1])}.");
                        return 0;

                    case "migrate":
                        if (!OperatingSystem.IsWindows())
                        {
                            output.WriteLine("Credential Manager is Windows-only; nothing to migrate here.");
                            return 1;
                        }
                        var (moved, alreadyStored) = secrets.Migrate(
                            name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User),
                            name => Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User));
                        output.WriteLine(moved.Count == 0
                            ? "No secrets were left in user environment variables."
                            : $"Moved to Credential Manager, and removed from your environment variables: {string.Join(", ", moved)}.");
                        if (alreadyStored.Count > 0)
                        {
                            output.WriteLine(
                                $"Kept as environment variables because Credential Manager already holds a different value: {string.Join(", ", alreadyStored)}. "
                                + "The Credential Manager value is the one used; remove the variables once you have checked.");
                        }
                        output.WriteLine("Re-run the Register-*.ps1 scripts, or sign out and back in, so running tasks stop seeing the old variables.");
                        return 0;

                    default:
                        output.WriteLine(Usage);
                        return 1;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                output.WriteLine(ex.Message);
                return 1;
            }
        }

        /// <summary>Reads a line from the console without echoing it; from a pipe, reads it plainly.</summary>
        private static string ReadHidden()
        {
            if (Console.IsInputRedirected)
            {
                return Console.In.ReadLine() ?? string.Empty;
            }

            var value = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    return value.ToString();
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (value.Length > 0) value.Length--;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    value.Append(key.KeyChar);
                }
            }
        }
    }
}
