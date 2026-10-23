using System.IO;
using System.Text;

namespace RAG.Testing.Desktop.Services;

public class TestLogger
{
    private readonly string _logFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TestLogger(string logFilePath)
    {
        _logFilePath = logFilePath;

        var directory = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task AppendTestResultAsync(
        string question,
        string answer,
        long elapsedMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var entry = new StringBuilder();
        entry.AppendLine("==================================================");
        entry.AppendLine($"Timestamp : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        entry.AppendLine($"Elapsed   : {elapsedMilliseconds} ms");
        entry.AppendLine("--------------------------------------------------");
        entry.AppendLine("Question:");
        entry.AppendLine(question);
        entry.AppendLine("--------------------------------------------------");
        entry.AppendLine("Answer:");
        entry.AppendLine(answer);
        entry.AppendLine("==================================================");
        entry.AppendLine();

        await _lock.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(
                _logFilePath,
                entry.ToString(),
                cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }
}