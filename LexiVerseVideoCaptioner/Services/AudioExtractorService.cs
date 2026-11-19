using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace LexiVerseVideoCaptioner.Services;

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
        else if (!await Task.Run(() => IsBundledFfmpegValid(ffmpegExe)))
        {
            progress?.Report("FFmpeg をダウンロード中（初回のみ）...");
            await DownloadFFmpegFromGitHubAsync(ffmpegDir, ffmpegExe, progress);
        }

        _ffmpegPath = ffmpegExe;
        _ffmpegReady = true;
    }

    // ── ダウンロードする FFmpeg（バージョン固定・ハッシュ検証）────────────────
    // BtbN/FFmpeg-Builds の月末ビルドは長期間保持されるため、それに固定する。
    // 更新するときは URL と 2 つの SHA-256 をまとめて差し替えること。
    //   zip のハッシュ: GitHub Releases の asset の digest
    //   exe のハッシュ: zip 内 bin/ffmpeg.exe を sha256sum した値
    private const string FFmpegZipUrl =
        "https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/" +
        "autobuild-2026-10-31-12-59/ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-9.0.zip";
    private const string FFmpegZipSha256 =
        "9d37c564e0d01c18cde184a079fa6ef04d7d2783ada41da81d04165cc8f1a650";
    private const string FFmpegExeSha256 =
        "d03c858a130cae1382902925d9f46eb15d7de42e025d34cc29b7ddca6cfb4c62";

    /// <summary>
    /// 固定バージョンの FFmpeg をダウンロードし、ハッシュを確認してから展開する
    /// </summary>
    private static async Task DownloadFFmpegFromGitHubAsync(
        string destDir,
        string ffmpegExe,
        IProgress<string>? progress)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        progress?.Report("ffmpeg.zip をダウンロード中...");

        var tmpZip = Path.Combine(
            Path.GetTempPath(),
            $"ffmpeg_{Guid.NewGuid()}.zip");
        var tmpExe = ffmpegExe + ".partial";

        try
        {
            await using (var stream = await http.GetStreamAsync(FFmpegZipUrl))
            await using (var fs = File.Create(tmpZip))
            {
                await stream.CopyToAsync(fs);
            }

            progress?.Report("ダウンロードしたファイルを検証中...");
            HashVerifier.VerifyFile(tmpZip, FFmpegZipSha256, "ffmpeg.zip");

            progress?.Report("展開中...");

            using (var archive = ZipFile.OpenRead(tmpZip))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                    e.Name.Equals(ffmpegExeName, StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                    throw new FileNotFoundException("zip 内に ffmpeg.exe が見つかりませんでした");

                Directory.CreateDirectory(destDir);
                entry.ExtractToFile(tmpExe, overwrite: true);
            }

            HashVerifier.VerifyFile(tmpExe, FFmpegExeSha256, ffmpegExeName);
            File.Move(tmpExe, ffmpegExe, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmpZip); } catch { /* 無視 */ }
            try { File.Delete(tmpExe); } catch { /* 無視 */ }
        }

        progress?.Report("FFmpeg のセットアップが完了しました！");
    }

    /// <summary>
    /// 同梱の ffmpeg.exe が固定バージョンのものと一致するか
    /// （壊れている・差し替えられている・古いバージョンの場合は false）
    /// </summary>
    private static bool IsBundledFfmpegValid(string ffmpegExe)
        => File.Exists(ffmpegExe) && HashVerifier.Matches(ffmpegExe, FFmpegExeSha256);

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