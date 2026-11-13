namespace LocalAssistant.Diagnostics
{
    /// <summary>
    /// A durable, append-only record of what each run of the app actually did - independent of
    /// Task Scheduler, whose own history only tells you the process started/exited, not why.
    /// </summary>
    public static class RunLog
    {
        public static string LogFilePath => AppPaths.RunLogFile;

        public static void Append(string message)
        {
            try
            {
                AppPaths.EnsureRoot();
                File.AppendAllText(LogFilePath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never be the reason the actual run fails.
            }
        }
    }
}
