using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DotaTune.Core.Diagnostics;

public enum LogLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Plain text log, one UTF-8 file per day. Timestamps are ISO 8601 in the
/// invariant culture so the file reads the same on any machine.
/// </summary>
public sealed class FileLog
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;

    public FileLog(string directory, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Days of logs kept on disk; older files are removed when a new day starts.</summary>
    public int KeepDays { get; init; } = 14;

    public string CurrentFile => FileFor(_clock());

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warning(string message) => Write(LogLevel.Warning, message);

    public void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    public void Write(LogLevel level, string message)
    {
        DateTimeOffset now = _clock();
        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-ddTHH:mm:ss.fffzzz} {level.ToString().ToUpperInvariant(),-7} {message}{Environment.NewLine}");

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                string file = FileFor(now);
                bool newFile = !File.Exists(file);
                File.AppendAllText(file, line, Utf8NoBom);
                if (newFile)
                {
                    RemoveOldFiles(now);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log is the last resort for errors; if it cannot be written,
                // the debugger output is all that is left.
                Trace.WriteLine($"DotaTune log write failed: {ex.Message}. Line: {line}");
            }
        }
    }

    private string FileFor(DateTimeOffset moment) =>
        Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"dotatune-{moment:yyyy-MM-dd}.log"));

    private void RemoveOldFiles(DateTimeOffset now)
    {
        DateTime border = now.Date.AddDays(-KeepDays);
        foreach (string file in Directory.EnumerateFiles(_directory, "dotatune-*.log"))
        {
            string stamp = Path.GetFileNameWithoutExtension(file)["dotatune-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day)
                && day < border)
            {
                File.Delete(file);
            }
        }
    }
}
