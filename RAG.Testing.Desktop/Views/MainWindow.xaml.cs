using RAG.Testing.Desktop.Services;
using System.Windows;
using System.Windows.Documents;

namespace RAG.Testing.Desktop.Views;

public partial class MainWindow : Window
{
    private readonly RagPipelineService _pipeline;
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public MainWindow(RagPipelineService pipeline)
    {
        InitializeComponent();
        _pipeline = pipeline;

        // Pre-create a paragraph so AppendToken can just add Runs.
        AnswerBox.Document.Blocks.Clear();
        AnswerBox.Document.Blocks.Add(new Paragraph());
    }

    private async void AskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        var question = QuestionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            StatusText.Text = "Please enter a question.";
            return;
        }

        _isRunning = true;
        AskButton.IsEnabled = false;
        ClearButton.IsEnabled = false;
        CancelButton.IsEnabled = true;

        AnswerBox.Document.Blocks.Clear();
        AnswerBox.Document.Blocks.Add(new Paragraph());

        StatusText.Text = "Retrieving evidence and generating answer…";

        _cts = new CancellationTokenSource();

        try
        {
            var result = await _pipeline.AskAsync(
                question,
                onAnswer: answer =>
                {
                    Dispatcher.Invoke(() => AppendToken(answer));
                },
                cancellationToken: _cts.Token);

            StatusText.Text =
                $"Done. Docs: {string.Join(", ", result.DocumentNames)} | " +
                $"Embedding {result.EmbeddingMs} ms, " +
                $"Retrieval {result.RetrievalMs} ms, " +
                $"Generation {result.GenerationMs} ms, " +
                $"Total {result.TotalMs} ms";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled by user.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error: " + ex.Message;
        }
        finally
        {
            _isRunning = false;
            AskButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        QuestionBox.Clear();
        AnswerBox.Document.Blocks.Clear();
        AnswerBox.Document.Blocks.Add(new Paragraph());
        StatusText.Text = string.Empty;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private void AppendToken(string token)
    {
        if (AnswerBox.Document.Blocks.LastBlock is not Paragraph paragraph)
        {
            paragraph = new Paragraph();
            AnswerBox.Document.Blocks.Add(paragraph);
        }

        paragraph.Inlines.Add(new Run(token));
        AnswerBox.ScrollToEnd();
    }
}