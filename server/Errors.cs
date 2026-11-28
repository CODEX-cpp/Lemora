namespace Lemora.Server;

/// <summary>internal errors (500 response): they are written to errors.log in the data folder, so the cause is visible instead of an empty response</summary>
internal static class Errors
{
    private static readonly object Gate = new();

    public static void Log(HttpContext c, Exception ex)
    {
        try
        {
            lock (Gate)
            {
                var f = Path.Combine(State.ConfigDir, "errors.log");
                if (File.Exists(f) && new FileInfo(f).Length > 1_000_000) File.Delete(f); // at most about 1 MB
                File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {c.Request.Method} {c.Request.Path}: {ex}\n\n");
            }
        }
        catch { /* writing the log must never cause another error */ }
    }
}
