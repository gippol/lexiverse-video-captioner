using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LexiVerseVideoCaptioner.Services;

/// <summary>
/// ログを 1 つのテキストファイルに追記するシンプルなロガー。
/// 配布後のユーザー環境でもエラー原因を追えるようにするためのもの。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    // ログファイルがこのサイズを超えたら、起動時に .old へ退避する
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly StreamWriter? _writer;

    public string FilePath { get; }

    public FileLoggerProvider(string filePath)
    {
        FilePath = filePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            try
            {
                if (File.Exists(filePath) && new FileInfo(filePath).Length > MaxFileBytes)
                    File.Move(filePath, filePath + ".old", overwrite: true);
            }
            catch
            {
                // 別インスタンスが使用中などで退避できない場合はそのまま追記する
            }

            var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            // ログが書けなくてもアプリ本体は動かす
            _writer = null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private void Write(string line)
    {
        if (_writer == null) return;
        lock (_lock)
        {
            try { _writer.WriteLine(line); } catch { /* 書き込み失敗は無視 */ }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(logLevel).Append("] ")
                .Append(category).Append(": ")
                .Append(formatter(state, exception));

            if (exception != null)
                sb.AppendLine().Append(exception);

            provider.Write(sb.ToString());
        }
    }
}
