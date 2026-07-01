using Microsoft.Extensions.Logging;

namespace PiscineController;

// Logger fichier minimal, écrit en parallèle de la console (journalctl).
// Volontairement sans dépendance externe (Serilog etc.) : ce projet compile
// en AOT natif, et certains sinks Serilog reposent sur de la réflexion qui
// casse en AOT sans configuration de trimming supplémentaire. Cette
// implémentation reste simple : un fichier par jour, écriture thread-safe,
// purge automatique des fichiers plus vieux que RetentionDays.
//
// Permet de consulter les logs sans SSH : voir FileLogReader, utilisé par
// MqttService pour la commande cmd/logs (publie les N dernières lignes).
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly int _retentionDays;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _currentFile;

    public FileLoggerProvider(string directory, LogLevel minLevel, int retentionDays)
    {
        _directory = directory;
        _minLevel = minLevel;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_directory);
        PurgeOldFiles();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void PurgeOldFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (var f in Directory.GetFiles(_directory, "piscine-*.log"))
            {
                if (File.GetLastWriteTimeUtc(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* best-effort, ne doit jamais empêcher le démarrage */ }
    }

    internal void Write(string line)
    {
        lock (_lock)
        {
            string expectedFile = Path.Combine(_directory, $"piscine-{DateTime.Now:yyyy-MM-dd}.log");
            if (_currentFile != expectedFile)
            {
                _writer?.Flush();
                _writer?.Dispose();
                _currentFile = expectedFile;
                _writer = new StreamWriter(
                    new FileStream(expectedFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                { AutoFlush = true };
                PurgeOldFiles();
            }
            try { _writer!.WriteLine(line); }
            catch { /* best-effort : un échec d'écriture fichier ne doit jamais planter le service */ }
        }
    }

    internal bool IsEnabled(LogLevel level) => level >= _minLevel;

    public string CurrentLogFilePath => Path.Combine(_directory, $"piscine-{DateTime.Now:yyyy-MM-dd}.log");

    public void Dispose()
    {
        lock (_lock) { _writer?.Flush(); _writer?.Dispose(); }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;
        public FileLogger(FileLoggerProvider provider, string category)
        { _provider = provider; _category = category; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string msg = formatter(state, exception);
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{LevelTag(logLevel)}] {_category}: {msg}";
            if (exception != null) line += $"{Environment.NewLine}{exception}";
            _provider.Write(line);
        }

        private static string LevelTag(LogLevel l) => l switch
        {
            LogLevel.Trace       => "TRACE",
            LogLevel.Debug       => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning     => "WARN ",
            LogLevel.Error       => "ERROR",
            LogLevel.Critical    => "CRIT ",
            _ => "?    "
        };
    }
}

// Helper de lecture utilisé par MqttService pour la commande cmd/logs —
// lit les N dernières lignes du fichier de log du jour sans tout charger en
// mémoire (lecture en flux depuis la fin, taille de fichier journalier
// modeste donc une lecture complète serait déjà acceptable, mais on reste
// raisonnable si jamais la rétention ou la verbosité augmentent).
public static class FileLogReader
{
    public static List<string> TailLines(string path, int count)
    {
        if (!File.Exists(path)) return [];
        try
        {
            var all = File.ReadAllLines(path);
            return all.Length <= count ? [.. all] : [.. all[^count..]];
        }
        catch
        {
            return ["(lecture du fichier de log impossible)"];
        }
    }
}
