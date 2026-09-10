using Microsoft.Extensions.Logging;

namespace DevelopTi.Services;

/// <summary>Log mínimo em arquivo texto — fallback pra quando não dá pra abrir o DevTools do WebView2
/// (bloqueado por política de grupo em máquina corporativa) nem rodar o app via Visual Studio pra ver a
/// janela de Saída. Captura os mesmos erros que o Blazor já loga via ILogger antes de mostrar o banner
/// "Ocorreu um erro inesperado".</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
    private readonly object _writeLock = new();

    public FileLoggerProvider(string filePath)
    {
        _filePath = filePath;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _filePath, _writeLock);

    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _filePath;
        private readonly object _writeLock;

        public FileLogger(string categoryName, string filePath, object writeLock)
        {
            _categoryName = categoryName;
            _filePath = filePath;
            _writeLock = writeLock;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {_categoryName}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;

            try
            {
                lock (_writeLock)
                {
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Log é best-effort — não pode derrubar o app se o arquivo estiver bloqueado ou sem espaço.
            }
        }
    }
}
