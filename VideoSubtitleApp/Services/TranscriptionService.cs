using Microsoft.Extensions.Logging;
using System.Text;
using System.IO;
using VideoSubtitleApp.Models;
using Whisper.net;
using Whisper.net.Ggml;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using System.Net.Http;


namespace VideoSubtitleApp.Services;

/// <summary>
/// Foundry Local の Whisper モデルを使って音声ファイルをテキスト化し、
/// 字幕エントリのリストを生成するサービス
/// </summary>
public class TranscriptionService 
{
    // FoundryLocalManager はシングルトン — アプリ起動時に一度だけ初期化する
    private bool _initialized;
    private readonly ILogger<TranscriptionService> _logger;

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
      string modelName,
      string language = "ja",
      IProgress<(string Message, double Percent)>? progress = null,
      CancellationToken ct = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("InitializeAsync を先に呼んでください。");

        string modelPath = "ggml.bin";

        progress?.Report(("Whisper モデルをロード中...", 0));

        if (!File.Exists(modelPath))
        {
            progress?.Report(("Whisper モデルをダウンロード中 (初回のみ)...", 5));

            //Thread thread = new(
            //    p => progress?.Report($"ダウンロード中... {p:F0}%", p * 0.4)
            //    );

            HttpClient httpClient = new HttpClient();
            WhisperGgmlDownloader downloader = new WhisperGgmlDownloader(httpClient);
            using (var modelStream = await downloader.GetGgmlModelAsync(
                StrToGgmlType(GgmlTypeStr.Contains(modelName) ? modelName : DefalutModelName),
                QuantizationType.Q8_0,
                CancellationToken.None
            ))
            {
                await using FileStream fs = File.Create(modelPath);
                await modelStream.CopyToAsync(fs);
            }
        }

        using var factory = WhisperFactory.FromPath(modelPath);

        progress?.Report(("モデルをメモリにロード中...", 45));

        using var processor = factory.CreateBuilder()
            .WithLanguage(language)
            .Build();

        progress?.Report(("音声を書き起こし中...", 55));

        var rawChunks = new List<(string Text, TimeSpan Start, TimeSpan End)>();

        await using var fileStream = File.OpenRead(wavPath);

        await foreach (var result in processor.ProcessAsync(fileStream))
        {
            if (ct.IsCancellationRequested)
            {
                await processor.DisposeAsync();
                ct.ThrowIfCancellationRequested();
            }

            progress?.Report(("音声を書き起こし中..", 55));

            rawChunks.Add((
                        result.Text.Trim(),
                        result.Start,
                        result.End
                    ));
        }

        progress?.Report(("字幕データを生成中...", 95));
        var subtitles = BuildSubtitles(rawChunks);

        progress?.Report(("完了!", 100));
        return subtitles;
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

    public static string DefalutModelName = GgmlType.Small.ToString();
}
