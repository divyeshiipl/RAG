using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rag.DocumentProcessing.Configuration;
using Rag.EmbeddingGenerator.Interfaces;
using Rag.EmbeddingGenerator.Services;
using Rag.VectorStore.Interfaces;
using Rag.VectorStore.Services;
using RAG.Testing.Desktop.Services;
using RAG.Testing.Desktop.Views;
using System.IO;
using System.Windows;

namespace RAG.Testing.Desktop;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddOptions<DocumentProcessingOptions>()
            .Bind(configuration.GetSection("RagPaths"));

        // RAG services
        services.AddHttpClient<IEmbeddingGenerator, OllamaEmbeddingGenerator>();
        services.AddHttpClient();
        services.AddSingleton<IVectorStore>(_ => new QdrantVectorStore("localhost"));

        // Logger 
        var paths = configuration
            .GetSection("RagPaths")
            .Get<DocumentProcessingOptions>()
            ?? throw new InvalidOperationException(
                "The 'RagPaths' configuration section is required.");

        services.AddSingleton(new TestLogger(
            Path.Combine(paths.LogsRootPath, "ResultLogs", "rag_execution_history.log")));

        // Pipeline
        services.AddSingleton<RagPipelineService>();

        // UI
        services.AddSingleton<MainWindow>();

        Services = services.BuildServiceProvider();

        var mainWindow = Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }
}