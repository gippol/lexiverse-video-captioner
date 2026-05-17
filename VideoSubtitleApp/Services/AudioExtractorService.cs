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
    private const string ffmpegName = "ffmpeg"; // ffmpeg
    private const string ffmpegExeName = ffmpegName + ".exe"; // ffmpeg.exe

    private bool _ffmpegReady;
    private bool _isFfmpegInstalled;
    private string? _ffmpegPath;

    /// <summary>
    /// FFmpeg バイナリを自動ダウンロード＆セットアップする（初回のみ）
    /// </summary>
    public async Task EnsureFFmpegAsync(string ffmpegDir, IProgress<string>? progress = null)
    {
        if (_ffmpegReady) return;

        Directory.CreateDirectory(ffmpegDir);

        var ffmpegExe = Path.Combine(ffmpegDir, ffmpegExeName);

        if (IsFfmpegInstalled())
        {
            _isFfmpegInstalled = true;
        }
        else if (!File.Exists(ffmpegExe))
        {
            progress?.Report("FFmpeg をダウンロード中（初回のみ）...");
            await DownloadFFmpegFromGitHubAsync(ffmpegDir, ffmpegExe, progress);
        }

        _ffmpegPath = ffmpegExe;
        _ffmpegReady = true;
    }

    /// <summary>
    /// GitHub Releases から ffmpeg.exe をダウンロードして展開する
    /// </summary>
    private static async Task DownloadFFmpegFromGitHubAsync(
        string destDir,
        string ffmpegExe,
        IProgress<string>? progress)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);

        // GitHub API は User-Agent 必須
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MyApp/1.0");

        progress?.Report("GitHub Releases 情報を取得中...");

        // 最新 release 情報
        const string apiUrl =
            "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";

        var json = await http.GetStringAsync(apiUrl);

        using var doc = System.Text.Json.JsonDocument.Parse(json);

        var assets = doc.RootElement.GetProperty("assets");

        // Windows 64bit build を探す
        string? zipUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();

            if (name is not null
                && name.Contains("win64")
                && name.Contains("lgpl")
                && !name.Contains("shared")
                && name.EndsWith(".zip"))
            {
                zipUrl = asset
                    .GetProperty("browser_download_url")
                    .GetString();

                break;
            }
        }

        if (zipUrl is null)
        {
            throw new InvalidOperationException(
                "GitHub Releases から ffmpeg zip を見つけられませんでした");
        }

        progress?.Report("ffmpeg.zip をダウンロード中...");

        var tmpZip = Path.Combine(
            Path.GetTempPath(),
            $"ffmpeg_{Guid.NewGuid()}.zip");

        await using (var stream = await http.GetStreamAsync(zipUrl))
        await using (var fs = File.Create(tmpZip))
        {
            await stream.CopyToAsync(fs);
        }

        progress?.Report("展開中...");

        using (var archive =
               System.IO.Compression.ZipFile.OpenRead(tmpZip))
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                e.Name.Equals(
                    ffmpegExeName,
                    StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                throw new FileNotFoundException(
                    "zip 内に ffmpeg.exe が見つかりませんでした");
            }

            Directory.CreateDirectory(destDir);

            entry.ExtractToFile(ffmpegExe, overwrite: true);
        }

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
        if (!_ffmpegReady || (!_isFfmpegInstalled && string.IsNullOrEmpty(_ffmpegPath)))
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
            FileName = _isFfmpegInstalled ? ffmpegName : _ffmpegPath, // ffmpeg実行ファイルのパス
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

    /// <summary>
    /// ffmpegがインストールされているかチェックする
    /// </summary>
    /// <returns></returns>
    public static bool IsFfmpegInstalled()
    {
        try
        {
            using var process = new Process();

            process.StartInfo.FileName = ffmpegName;
            process.StartInfo.Arguments = "-version";

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;

            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;

            process.Start();

            // 数秒待つ
            process.WaitForExit(3000);

            // ffmpeg は version 情報を stdout に出す
            string output = process.StandardOutput.ReadToEnd();

            return output.Contains("ffmpeg version");
        }
        catch
        {
            return false;
        }
    }
}