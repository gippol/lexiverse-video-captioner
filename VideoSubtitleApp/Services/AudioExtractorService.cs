using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace VideoSubtitleApp.Services;

/// <summary>
/// MP4 動画から音声を WAV として抽出するサービス（Xabe.FFmpeg を使用）
/// </summary>
public class AudioExtractorService
{
    private bool _ffmpegReady;
    private string? _ffmpegPath;

    /// <summary>
    /// FFmpeg バイナリを自動ダウンロード＆セットアップする（初回のみ）
    /// </summary>
    public async Task EnsureFFmpegAsync(string ffmpegDir, IProgress<string>? progress = null)
    {
        if (_ffmpegReady) return;

        Directory.CreateDirectory(ffmpegDir);

        var ffmpegExe = Path.Combine(ffmpegDir, "ffmpeg.exe");

        if (!File.Exists(ffmpegExe))
        {
            progress?.Report("FFmpeg をダウンロード中（初回のみ）...");
            await DownloadFFmpegFromFfbinariesAsync(ffmpegDir, ffmpegExe, progress);
        }

        _ffmpegPath = ffmpegExe;
        _ffmpegReady = true;
    }

    /// <summary>
    /// ffbinaries.com の API 経由で ffmpeg.exe をダウンロードして展開する
    /// </summary>
    private static async Task DownloadFFmpegFromFfbinariesAsync(
        string destDir, string ffmpegExe, IProgress<string>? progress)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        // ① ffbinaries API でダウンロード URL を取得
        progress?.Report("バージョン情報を取得中...");
        const string ApiUrl = "https://ffbinaries.com/api/v1/version/latest";
        var json = await http.GetStringAsync(ApiUrl);

        // ② JSON から windows-64 の ffmpeg zip URL をパース
        //    レスポンス例:
        //    { "bin": { "windows-64": { "ffmpeg": "https://...ffmpeg...zip" } } }
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var zipUrl = doc.RootElement
            .GetProperty("bin")
            .GetProperty("windows-64")
            .GetProperty("ffmpeg")
            .GetString()
            ?? throw new InvalidOperationException("ffbinaries から URL を取得できませんでした");

        // ③ zip をダウンロード（一時ファイルへ）
        progress?.Report("ffmpeg.zip をダウンロード中...");
        var tmpZip = Path.Combine(Path.GetTempPath(), "ffmpeg_tmp.zip");
        var zipBytes = await http.GetByteArrayAsync(zipUrl);
        await File.WriteAllBytesAsync(tmpZip, zipBytes);

        // ④ zip を展開して ffmpeg.exe だけ取り出す
        progress?.Report("展開中...");
        using (var archive = System.IO.Compression.ZipFile.OpenRead(tmpZip))
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("zip 内に ffmpeg.exe が見つかりませんでした");

            entry.ExtractToFile(ffmpegExe, overwrite: true);
        }

        // ⑤ 後片付け
        File.Delete(tmpZip);
        progress?.Report("FFmpeg のセットアップが完了しました！");
    }

    /// <summary>
    /// MP4 から モノラル 16kHz WAV を抽出して返す（Whisper に最適な形式）
    /// </summary>
    /// <param name="mp4Path">入力動画パス</param>
    /// <param name="outputDir">WAV 出力先ディレクトリ</param>
    /// <param name="progress">進捗コールバック</param>
    /// <param name="ct">キャンセルトークン</param>
    /// <returns>抽出した WAV ファイルのパス</returns>
    public async Task<string> ExtractAudioAsync(
    string mp4Path,
    string outputDir,
    IProgress<string>? progress = null,
    CancellationToken ct = default)
    {
        if (!_ffmpegReady || string.IsNullOrEmpty(_ffmpegPath))
            throw new InvalidOperationException("EnsureFFmpegAsync を先に呼んでください。");

        Directory.CreateDirectory(outputDir);

        var fileName = Path.GetFileNameWithoutExtension(mp4Path);
        var wavPath = Path.Combine(outputDir, $"{fileName}_{Guid.NewGuid():N}.wav");

        progress?.Report($"音声を抽出中: {Path.GetFileName(mp4Path)}");

        // ffmpegの引数を組み立て
        // -i 入力 → -vn（映像無効）→ -ar 16000 → -ac 1 → -c:a pcm_s16le → 出力
        var args = string.Join(" ",
            $"-i \"{mp4Path}\"",
            "-vn",            // 映像ストリームを無効化
            "-ar 16000",      // サンプルレート 16kHz（Whisper推奨）
            "-ac 1",          // モノラル
            "-c:a pcm_s16le", // WAV/PCM 16bit リニア
            "-y",             // 上書き許可（SetOverwriteOutput相当）
            $"\"{wavPath}\""
        );

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath, // ffmpeg実行ファイルのパス
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,  // ffmpegの進捗はstderrに出る
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // ── 進捗パース用: stderrをリアルタイム読み取り ──────────────────────
        TimeSpan totalDuration = TimeSpan.Zero;

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            // 総再生時間を取得: "Duration: HH:mm:ss.ff"
            if (totalDuration == TimeSpan.Zero)
            {
                var durationMatch = Regex.Match(e.Data,
                    @"Duration:\s*(\d+):(\d+):(\d+\.\d+)");
                if (durationMatch.Success)
                {
                    totalDuration = new TimeSpan(
                        0,
                        int.Parse(durationMatch.Groups[1].Value),   // 時
                        int.Parse(durationMatch.Groups[2].Value),   // 分
                        (int)double.Parse(durationMatch.Groups[3].Value,
                            System.Globalization.CultureInfo.InvariantCulture) // 秒
                    );
                }
            }

            // 現在位置を取得: "time=HH:mm:ss.ff"
            var timeMatch = Regex.Match(e.Data,
                @"time=(\d+):(\d+):(\d+\.\d+)");
            if (timeMatch.Success && totalDuration > TimeSpan.Zero)
            {
                var current = new TimeSpan(
                    0,
                    int.Parse(timeMatch.Groups[1].Value),
                    int.Parse(timeMatch.Groups[2].Value),
                    (int)double.Parse(timeMatch.Groups[3].Value,
                        System.Globalization.CultureInfo.InvariantCulture)
                );
                var pct = (int)(current.TotalSeconds / totalDuration.TotalSeconds * 100);
                progress?.Report($"音声抽出中... {Math.Min(pct, 100)}%");
            }
        };

        // ── プロセス起動 ────────────────────────────────────────────────────
        process.Start();
        process.BeginErrorReadLine(); // stderr非同期読み取り開始

        // CancellationToken対応: キャンセル時にプロセスをKill
        await using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* 既に終了していた場合は無視 */ }
        });

        await process.WaitForExitAsync(ct);

        // ── 終了コードチェック ──────────────────────────────────────────────
        if (process.ExitCode != 0)
        {
            // 失敗時は出力ファイルを削除してクリーンアップ
            if (File.Exists(wavPath))
                File.Delete(wavPath);

            throw new InvalidOperationException(
                $"ffmpeg が異常終了しました (exit code: {process.ExitCode})。" +
                $"ffmpegのパス・入力ファイルを確認してください。");
        }

        progress?.Report("音声抽出完了！");
        return wavPath;
    }
}