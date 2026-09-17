using System.Text;

namespace RAG.Testing.Console.Services;

public class TestLogger
{
    private readonly string _logFilePath;

    public TestLogger(string logFilePath = @"C:\Dev\RAG RND\Logs\ResultLogs\rag_execution_history.log")
    {
        _logFilePath = logFilePath;
    }

    public async Task AppendTestResultAsync(string question, string answer, long elapsedMs)
    {
        var sb = new StringBuilder();

        sb.AppendLine("================================================================================");
        sb.AppendLine($"TIMESTAMP   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"EXEC TIME   : {elapsedMs} ms");
        sb.AppendLine($"QUESTION    : {question}");
        sb.AppendLine("GENERATED ANSWER:");
        sb.AppendLine(answer);
        sb.AppendLine("================================================================================\n\n");

        // Appends to file, creates it if it doesn't exist
        await File.AppendAllTextAsync(_logFilePath, sb.ToString());
    }
}
