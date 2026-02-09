using System.Collections.Concurrent;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Simple thread-safe file logger. Writes to %LOCALAPPDATA%\CcDirector\logs\director-YYYY-MM-DD.log.
/// </summary>
public static class FileLog
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CcDirector", "logs");

    private static readonly BlockingCollection<string> _queue = new(1024);
    private static Thread? _writerThread;
    private static int _started;

    /// <summary>Start the background writer thread. Safe to call multiple times.</summary>
    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        Directory.CreateDirectory(LogDir);

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "FileLog-Writer"
        };
        _writerThread.Start();
    }

    /// <summary>Log a message with a timestamp prefix.</summary>
    public static void Write(string message)
    {
        if (_started == 0) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        _queue.TryAdd(line);
        System.Diagnostics.Debug.WriteLine(line);
    }

    /// <summary>Flush remaining messages and stop the writer thread.</summary>
    public static void Stop()
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1)
            return;
        _queue.CompleteAdding();
        _writerThread?.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>Returns the current log file path (useful for display).</summary>
    public static string CurrentLogPath =>
        Path.Combine(LogDir, $"director-{DateTime.Now:yyyy-MM-dd}.log");

    private static void WriterLoop()
    {
        StreamWriter? writer = null;
        string? currentDate = null;

        try
        {
            foreach (var line in _queue.GetConsumingEnumerable())
            {
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                if (today != currentDate)
                {
                    writer?.Flush();
                    writer?.Dispose();
                    currentDate = today;
                    var path = Path.Combine(LogDir, $"director-{currentDate}.log");
                    writer = new StreamWriter(path, append: true) { AutoFlush = false };
                }

                writer!.WriteLine(line);

                // Flush if queue is empty (no more pending writes)
                if (_queue.Count == 0)
                    writer.Flush();
            }
        }
        catch (InvalidOperationException)
        {
            // GetConsumingEnumerable throws when CompleteAdding is called
        }
        finally
        {
            writer?.Flush();
            writer?.Dispose();
        }
    }
}
