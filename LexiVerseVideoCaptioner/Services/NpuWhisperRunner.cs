using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LexiVerseVideoCaptioner.Services;

/// <summary>
/// AMD Ryzen AI の NPU 対応版 whisper.cpp (amd/whisper.cpp を -DWHISPER_VITISAI=1 でビルドしたもの) の
/// whisper-cli.exe を別プロセスで実行して書き起こす。
/// エンコーダーは NPU で動き、デコーダーは CPU で動く。
/// </summary>
/// <remarks>
/// 必要なもの:
/// - Ryzen AI 300 シリーズの NPU と、NPU ドライバ .280 以降
/// - アプリのフォルダの npu\ に whisper-cli.exe・whisper.dll・ggml*.dll・flexmlrt.dll
///   (csproj がリポジトリの whisperCpp\build\bin\Release からコピーする)
/// - モデル (ggml_*.bin) と同じフォルダに、エンコーダーをコンパイル済みの *-encoder-vitisai.rai
///   (初回に AMD の Hugging Face リポジトリからダウンロードする)
/// </remarks>
public class NpuWhisperRunner
{
    private readonly ILogger _logger;

    public NpuWhisperRunner(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>NPU 版 whisper-cli.exe のパス（アプリのフォルダ内に固定。PATH からは探さない）</summary>
    public static string CliPath { get; } = Path.Combine(AppContext.BaseDirectory, "npu", "whisper-cli.exe");

    /// <summary>NPU 版 whisper-cli.exe が同梱されているか</summary>
    public static bool IsAvailable => File.Exists(CliPath);

    /// <summary>指定モデル用の NPU エンコーダー (.rai) が AMD から提供されているか</summary>
    public static bool SupportsModel(string modelName) => RaiFiles.ContainsKey(modelName);

    /// <summary>
    /// AMD が配布している NPU 用エンコーダー (.rai)。
    /// 出典: https://huggingface.co/collections/amd/ryzen-ai-whisper-npu-optimized-onnx-models
    /// リビジョンを固定し、SHA-256 は https://huggingface.co/api/models/{Repo}/tree/{Revision} の lfs.oid。
    /// 更新するときはリビジョンとハッシュを一緒に変えること。
    /// </summary>
    private static readonly Dictionary<string, (string Repo, string Revision, string FileName, string Sha256)> RaiFiles = new()
    {
        ["Tiny"]         = ("amd/whisper-tiny-onnx-npu",        "4b8eefaf2357208772620a40bcf0e5250671511a", "ggml-tiny-encoder-vitisai.rai",           "cbddaf952baf963cc08220c17a027d3110a62840dcd839ec39c7987b1172c1ea"),
        ["Base"]         = ("amd/whisper-base-onnx-npu",        "53b319489a56d56927b98d3855a26158f64b6507", "ggml-base-encoder-vitisai.rai",           "adf0c707d29cf8e53fa08f9c0fb7d5fa5a337b216e8cbb3c6f66ea2aab00a145"),
        ["Small"]        = ("amd/whisper-small-onnx-npu",       "066b086723c5da7f18d4838e88448ec81321fd81", "ggml-small-encoder-vitisai.rai",          "03ee01732e2b6c46ebf3f5913703368b91eb3aef838b95babdc5a3ac98e1f161"),
        ["SmallEn"]      = ("amd/whisper-small-en-onnx-npu",    "2ee3e6a3be14f7730cb7b9bb19d7f7a3dfa89077", "ggml-small-en-encoder-vitisai.rai",       "46dc365b92d2fc4a57f330ca4823687d512b71efafa386fb2475f8d4c48be7fc"),
        ["Medium"]       = ("amd/whisper-medium-onnx-npu",      "8d3f2be23b32c4082edeca14322de112f1f0d709", "ggml-medium-encoder-vitisai.rai",         "62d40c5d0de4ad0f87f1c90eddabd46dd50e51f738b407fb55cdb68a85344e8f"),
        ["LargeV3"]      = ("amd/whisper-large-v3-onnx-npu",    "3126aba8f8c6a81b4f30821b3aac942b35464681", "ggml-large-v3-encoder-vitisai.rai",       "1b0bdff756b39542af20b3a7af1ca85e4b1bec523bd0247023847a71ce879b08"),
        ["LargeV3Turbo"] = ("amd/whisper-large-turbo-onnx-npu", "149424fe9377d71dfbd474dddb2aa47bb53076db", "ggml-large-v3-turbo-encoder-vitisai.rai", "c73bbabc7210ccc64376e81bbffb5b397b6b4ff5f5020d5cca90ed815c6708d0"),
    };

    /// <summary>
    /// whisper.cpp はモデルのパスから ".bin" を取り除き "-encoder-vitisai.rai" を付けたファイルを読む。
    /// </summary>
    public static string GetRaiPath(string modelPath)
        => Path.Combine(Path.GetDirectoryName(modelPath)!,
                        Path.GetFileNameWithoutExtension(modelPath) + "-encoder-vitisai.rai");

    /// <summary>
    /// NPU 用エンコーダー (.rai) がなければダウンロードし、SHA-256 を検証してから配置する。
    /// </summary>
    public async Task EnsureRaiAsync(
        string modelName,
        string modelPath,
        HttpClient http,
        IProgress<(string Message, double Percent)>? progress,
        CancellationToken ct)
    {
        if (!RaiFiles.TryGetValue(modelName, out var rai))
            throw new NotSupportedException(
                $"モデル {modelName} は NPU に対応していません。\n" +
                $"NPU で使えるモデル: {string.Join(", ", RaiFiles.Keys)}");

        string raiPath = GetRaiPath(modelPath);
        if (File.Exists(raiPath)) return;

        string url = $"https://huggingface.co/{rai.Repo}/resolve/{rai.Revision}/{rai.FileName}";
        string partialPath = raiPath + ".partial";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var fs = File.Create(partialPath);

                var buffer = new byte[1024 * 1024];
                long total = 0;
                long lastReportedMb = -1;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;

                    long mb = total / (1024 * 1024);
                    if (mb != lastReportedMb && mb % 10 == 0)
                    {
                        lastReportedMb = mb;
                        progress?.Report(($"NPU 用エンコーダーをダウンロード中 (初回のみ)... {mb} MB", 45));
                    }
                }
            }

            progress?.Report(("NPU 用エンコーダーを検証中...", 45));
            HashVerifier.VerifyFile(partialPath, rai.Sha256, $"NPU 用エンコーダー ({modelName})");

            File.Move(partialPath, raiPath, overwrite: true);
            _logger.LogInformation("NPU 用エンコーダーをダウンロードしました: {path}", raiPath);
        }
        catch
        {
            try { File.Delete(partialPath); } catch { /* 無視 */ }
            throw;
        }
    }

    /// <summary>
    /// whisper-cli.exe で WAV を書き起こし、(テキスト, 開始, 終了) のリストを返す。
    /// </summary>
    public async Task<List<(string Text, TimeSpan Start, TimeSpan End)>> TranscribeAsync(
        string wavPath,
        string modelPath,
        string language,
        string workDir,
        IProgress<(string Message, double Percent)>? progress,
        CancellationToken ct)
    {
        if (!IsAvailable)
            throw new FileNotFoundException("NPU 版 whisper-cli.exe が見つかりません。", CliPath);

        Directory.CreateDirectory(workDir);
        // whisper-cli は -of に拡張子 .json を付けて保存する
        string outBase = Path.Combine(workDir, $"npu_{Guid.NewGuid():N}");
        string jsonPath = outBase + ".json";

        var psi = new ProcessStartInfo
        {
            FileName = CliPath, // フルパスで指定（名前解決による別の exe の実行を防ぐ）
            WorkingDirectory = Path.GetDirectoryName(CliPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-m", modelPath,
            "-f", wavPath,
            "-l", language,
            "-t", Math.Clamp(Environment.ProcessorCount, 1, 8).ToString(CultureInfo.InvariantCulture),
            "-pp",              // 進捗を stderr に出す
            "-oj",              // 結果を JSON で保存する
            "-of", outBase,
        })
        {
            psi.ArgumentList.Add(arg);
        }

        // エラー時の原因調査用に stderr の末尾を残しておく
        var stderrTail = new Queue<string>();
        bool vitisLoadFailed = false;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            if (e.Data.Contains("failed to load Vitis AI model", StringComparison.Ordinal))
                vitisLoadFailed = true;

            var m = ProgressRegex.Match(e.Data);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct))
            {
                // 書き起こしは全体の 55%〜95% に割り当てる
                progress?.Report(($"音声を書き起こし中 (NPU)... {pct}%", 55 + Math.Clamp(pct, 0, 100) * 0.4));
            }

            lock (stderrTail)
            {
                stderrTail.Enqueue(e.Data);
                if (stderrTail.Count > 30) stderrTail.Dequeue();
            }
        };
        // 標準出力も読み捨てないとバッファが詰まってプロセスが止まる
        process.OutputDataReceived += (_, _) => { };

        progress?.Report(("NPU を準備中...", 50));

        try
        {
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await using (ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* 既に終了していた場合は無視 */ }
            }))
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }

            ct.ThrowIfCancellationRequested();

            if (process.ExitCode != 0 || vitisLoadFailed || !File.Exists(jsonPath))
            {
                string tail;
                lock (stderrTail) tail = string.Join(Environment.NewLine, stderrTail);
                _logger.LogError("whisper-cli (NPU) が失敗しました (exit code: {code})\n{stderr}", process.ExitCode, tail);

                if (vitisLoadFailed)
                    throw new InvalidOperationException(
                        "NPU 用エンコーダーを読み込めませんでした。\n" +
                        "NPU ドライバ (.280 以降) と Ryzen AI ランタイム (flexmlrt) が入っているか確認してください。");

                string lastLine;
                lock (stderrTail) lastLine = stderrTail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
                throw new InvalidOperationException(
                    $"whisper-cli (NPU) が異常終了しました (exit code: {process.ExitCode})。\n" +
                    (lastLine.Length > 0 ? $"{lastLine}\n" : "") +
                    "他のアプリが NPU を使っていないか確認してください。詳細はログを確認してください。");
            }

            return ParseJson(jsonPath);
        }
        finally
        {
            try { File.Delete(jsonPath); } catch { /* 無視 */ }
        }
    }

    private static readonly Regex ProgressRegex = new(@"progress\s*=\s*(\d+)%", RegexOptions.Compiled);

    /// <summary>
    /// whisper-cli の JSON 出力 (transcription[].offsets.from/to はミリ秒) を読む。
    /// </summary>
    private static List<(string Text, TimeSpan Start, TimeSpan End)> ParseJson(string jsonPath)
    {
        // トークン境界で UTF-8 が途中で切れることがあるため、置換文字を許して文字列にしてから解析する
        string json = Encoding.UTF8.GetString(File.ReadAllBytes(jsonPath));
        using var doc = JsonDocument.Parse(json);

        var result = new List<(string, TimeSpan, TimeSpan)>();
        foreach (var seg in doc.RootElement.GetProperty("transcription").EnumerateArray())
        {
            var offsets = seg.GetProperty("offsets");
            string text = seg.GetProperty("text").GetString()?.Trim() ?? "";
            if (text.Length == 0) continue;

            result.Add((
                text,
                TimeSpan.FromMilliseconds(offsets.GetProperty("from").GetInt64()),
                TimeSpan.FromMilliseconds(offsets.GetProperty("to").GetInt64())));
        }
        return result;
    }
}
