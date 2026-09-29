using System.Text;
using System.Text.Json;

namespace DeveloperIsland.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Minimal structured logger: one JSON object per line in <c>logs/app-yyyyMMdd.log</c>.
/// Callers pass metadata only (categories, counts, paths, exception types) and never prompt,
/// chat or code content. Logging never throws.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? s_directory;
    private static StreamWriter? s_writer;
    private static DateOnly s_writerDay;

    public static LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>Test hook: receives every written line.</summary>
    public static Action<string>? Sink { get; set; }

    public static void Initialize(string directory, int retainDays = 7)
    {
        lock (Gate)
        {
            s_directory = directory;
            try
            {
                Directory.CreateDirectory(directory);
                var cutoff = DateTime.Now.AddDays(-retainDays);
                foreach (var file in Directory.EnumerateFiles(directory, "app-*.log"))
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    public static void Debug(string category, string message, object? data = null) => Write(LogLevel.Debug, category, message, data, null);

    public static void Info(string category, string message, object? data = null) => Write(LogLevel.Info, category, message, data, null);

    public static void Warn(string category, string message, object? data = null, Exception? ex = null) => Write(LogLevel.Warning, category, message, data, ex);

    public static void Error(string category, string message, Exception? ex = null, object? data = null) => Write(LogLevel.Error, category, message, data, ex);

    public static void Write(LogLevel level, string category, string message, object? data, Exception? ex)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        try
        {
            var line = Format(level, category, message, data, ex);
            Sink?.Invoke(line);
            lock (Gate)
            {
                var writer = GetWriter();
                writer?.WriteLine(line);
            }
        }
        catch
        {
            // Swallow: logging failures are not actionable at runtime.
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            try { s_writer?.Flush(); } catch { }
        }
    }

    internal static string Format(LogLevel level, string category, string message, object? data, Exception? ex)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("ts", DateTimeOffset.Now.ToString("O"));
            json.WriteString("level", level.ToString().ToLowerInvariant());
            json.WriteString("cat", category);
            json.WriteString("msg", message);
            if (data is not null)
            {
                json.WritePropertyName("data");
                WriteData(json, data);
            }

            if (ex is not null)
            {
                // Exception type, message and stack only. Messages from our own code never embed content.
                json.WriteString("exType", ex.GetType().FullName);
                json.WriteString("exMsg", ex.Message);
                json.WriteString("stack", ex.StackTrace);
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Writes the public properties of a (usually anonymous) object as JSON primitives. This avoids
    /// reflection-based System.Text.Json, which may be disabled in trimmed builds.
    /// </summary>
    private static void WriteData(Utf8JsonWriter json, object data)
    {
        json.WriteStartObject();
        foreach (var property in data.GetType().GetProperties())
        {
            var value = property.GetValue(data);
            switch (value)
            {
                case null:
                    json.WriteNull(property.Name);
                    break;
                case bool b:
                    json.WriteBoolean(property.Name, b);
                    break;
                case int or long or short or byte or uint or ulong:
                    json.WriteNumber(property.Name, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case double or float or decimal:
                    json.WriteNumber(property.Name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case IFormattable formattable:
                    json.WriteString(property.Name, formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    json.WriteString(property.Name, value.ToString());
                    break;
            }
        }

        json.WriteEndObject();
    }

    private static StreamWriter? GetWriter()
    {
        if (s_directory is null)
        {
            return null;
        }

        var today = DateOnly.FromDateTime(DateTime.Now);
        if (s_writer is not null && s_writerDay == today)
        {
            return s_writer;
        }

        s_writer?.Dispose();
        var path = Path.Combine(s_directory, $"app-{today:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        s_writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        s_writerDay = today;
        return s_writer;
    }
}
