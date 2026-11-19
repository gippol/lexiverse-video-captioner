using Microsoft.Extensions.Logging;
using System.Text;
using System.IO;
using LexiVerseVideoCaptioner.Models;
using Whisper.net;
using Whisper.net.Ggml;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using System.Net.Http;


namespace LexiVerseVideoCaptioner.Services;

/// <summary>
/// Foundry Local の Whisper モデルを使って音声ファイルをテキスト化し、
/// 字幕エントリのリストを生成するサービス
/// </summary>
public class TranscriptionService : IDisposable
{
    // FoundryLocalManager はシングルトン — アプリ起動時に一度だけ初期化する
    private bool _initialized;
    private readonly ILogger<TranscriptionService> _logger;

    // ロード済みモデルのキャッシュ（モデルが変わったときだけ再ロードする）
    private WhisperFactory? _factory;
    private string? _factoryModelPath;
    private bool _busy;
    private bool _disposed;

    // 字幕分割の目安（文字数）— 日本語は 1 文字が広いので短めに
    private const int MaxCharsPerSubtitle = 30;
    // 字幕の表示時間の最小値 (ms)
    private const int MinSubtitleDurationMs = 1000;
    // 字幕の表示時間の最大値 (ms)
    private const int MaxSubtitleDurationMs = 5000;

    public TranscriptionService(ILogger<TranscriptionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Foundry Local SDK を初期化する。アプリ起動後に一度だけ呼ぶ。
    /// </summary>
    public async Task InitializeAsync(IProgress<string>? progress = null)
    {
        if (_initialized) return;

        _initialized = true;
    }

    /// <summary>
    /// WAV ファイルを Whisper で書き起こし、字幕エントリのリストを返す。
    /// </summary>
    /// <param name="wavPath">書き起こすWAVファイルのパス</param>
    /// <param name="modelName">Whisper モデル名</param>
    /// <param name="language">言語コード ("ja" など)</param>
    /// <param name="progress">進捗コールバック</param>
    /// <param name="ct">キャンセルトークン</param>
    /// <summary>
    /// WAV ファイルを Whisper で書き起こし、字幕エントリのリストを返す。
    /// </summary>
    public async Task<List<SubtitleEntry>> TranscribeAsync(
      string wavPath,
      string modelDir,
      string modelName,
      string language = "ja",
      IProgress<(string Message, double Percent)>? progress = null,
      CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("InitializeAsync を先に呼んでください。");

        string fixdeModelName = GgmlTypeStr.Contains(modelName) ? modelName : DefalutModelName;
        string modelPath = Path.Combine(modelDir, $"ggml_{fixdeModelName}.bin");

        Directory.CreateDirectory(modelDir);

        progress?.Report(("Whisper モデルをロード中...", 0));

        if (!File.Exists(modelPath))
        {
            progress?.Report(("Whisper モデルをダウンロード中 (初回のみ)...", 5));

            await DownloadModelAsync(fixdeModelName, modelPath, progress, ct);
        }

        progress?.Report(("モデルをメモリにロード中...", 45));

        // ロード済みのモデルは使い回す（2回目以降はロード時間なし）
        var factory = GetOrLoadFactory(modelPath);

        _busy = true;
        try
        {
            // DisposeAsync は処理中の推論が止まるのを待ってから解放する（キャンセル直後も安全）
            await using var processor = factory.CreateBuilder()
                .WithLanguage(language)
                .WithThreads(Environment.ProcessorCount)
                .Build();

            progress?.Report(("音声を書き起こし中...", 55));

            var rawChunks = new List<(string Text, TimeSpan Start, TimeSpan End)>();

            await using var fileStream = File.OpenRead(wavPath);

            // ct を Whisper.net に渡すと、内部の処理（エンコーダー開始時）でも
            // キャンセルを確認して中断してくれる
            await foreach (var result in processor.ProcessAsync(fileStream, ct))
            {
                ct.ThrowIfCancellationRequested();

                progress?.Report(("音声を書き起こし中..", 55));

                rawChunks.Add((
                            result.Text.Trim(),
                            result.Start,
                            result.End
                        ));
            }

            // 中断時に列挙が例外なしで終わる場合もあるため、途中までの結果を完了扱いにしない
            ct.ThrowIfCancellationRequested();

            progress?.Report(("字幕データを生成中...", 95));
            var subtitles = BuildSubtitles(rawChunks);

            progress?.Report(("完了!", 100));
            return subtitles;
        }
        finally
        {
            _busy = false;
            // 書き起こし中に Dispose が呼ばれていた場合はここで解放する
            if (_disposed)
                ReleaseFactory();
        }
    }

    /// <summary>
    /// モデルを一時ファイルにダウンロードし、完了してから正式なファイル名に変える。
    /// 途中で失敗・キャンセルしても壊れたモデルファイルが残らない。
    /// </summary>
    private async Task DownloadModelAsync(
        string modelName,
        string modelPath,
        IProgress<(string Message, double Percent)>? progress,
        CancellationToken ct)
    {
        string partialPath = modelPath + ".partial";
        try
        {
            var downloader = new WhisperGgmlDownloader(SharedHttpClient);
            await using (var modelStream = await downloader.GetGgmlModelAsync(
                StrToGgmlType(modelName), QuantizationType.Q8_0, ct))
            await using (var fs = File.Create(partialPath))
            {
                var buffer = new byte[1024 * 1024];
                long total = 0;
                long lastReportedMb = -1;
                int read;
                while ((read = await modelStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;

                    long mb = total / (1024 * 1024);
                    if (mb != lastReportedMb && mb % 10 == 0)
                    {
                        lastReportedMb = mb;
                        progress?.Report(($"Whisper モデルをダウンロード中 (初回のみ)... {mb} MB", 5));
                    }
                }
            }

            File.Move(partialPath, modelPath, overwrite: true);
            _logger.LogInformation("モデルをダウンロードしました: {path}", modelPath);
        }
        catch
        {
            try { File.Delete(partialPath); } catch { /* 無視 */ }
            throw;
        }
    }

    // HttpClient は使い回す（毎回 new するとソケットを使い切る恐れがある）
    private static readonly HttpClient SharedHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// 指定パスのモデルがロード済みならそれを返し、違うモデルなら入れ替える。
    /// </summary>
    private WhisperFactory GetOrLoadFactory(string modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_factory != null && _factoryModelPath == modelPath)
            return _factory;

        ReleaseFactory();
        _factory = WhisperFactory.FromPath(modelPath);
        _factoryModelPath = modelPath;
        return _factory;
    }

    private void ReleaseFactory()
    {
        _factory?.Dispose();
        _factory = null;
        _factoryModelPath = null;
    }

    /// <summary>
    /// キャッシュしているモデルを解放する。書き起こし中なら終了後に解放する。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!_busy)
            ReleaseFactory();
    }

    /// <summary>
    /// 生チャンクから字幕エントリを構築する。
    /// </summary>
    private static List<SubtitleEntry> BuildSubtitles(
        List<(string Text, TimeSpan Start, TimeSpan End)> rawChunks)
    {
        var entries = new List<SubtitleEntry>();
        int index = 1;

        foreach (var (text, chunkStart, chunkEnd) in rawChunks)
        {
            entries.Add(new SubtitleEntry
            {
                Index = index++,
                StartTime = chunkStart,
                EndTime = chunkEnd,
                Text = text
            });
        }

        return entries;
    }

    public static string[] GgmlTypeStr = Enum.GetNames(typeof(GgmlType));
    public static GgmlType StrToGgmlType(string typename)
    {
        return (GgmlType)Enum.Parse(typeof(GgmlType), typename);
    }

    public static string DefalutModelName = GgmlType.LargeV3Turbo.ToString();
}
