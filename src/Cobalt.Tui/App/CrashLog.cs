using System.Globalization;

namespace Cobalt.Tui.App;

/// <summary>
/// Formats and persists an unrecoverable exception to a log file (ADR 0013). Pure
/// and deterministic — the timestamp is passed in, never read from the clock — so
/// the crash boundary can be unit-tested without a real crash.
/// </summary>
public static class CrashLog
{
    /// <summary>Renders a crash entry: an ISO-8601 timestamp header, the exception type and
    /// message, and the full stack (including inner exceptions via <see cref="Exception.ToString"/>).</summary>
    public static string Format(Exception exception, DateTimeOffset timestamp)
    {
        var when = timestamp.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);
        return $"""
            ===== cobalt crash {when} =====
            {exception.GetType().FullName}: {exception.Message}
            {exception}

            """;
    }

    /// <summary>Appends a formatted crash entry to <paramref name="path"/>, creating the
    /// containing directory if needed. Returns the path so callers can report it.</summary>
    public static string Write(string path, Exception exception, DateTimeOffset timestamp)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.AppendAllText(path, Format(exception, timestamp));
        return path;
    }
}
