using LexiVerseVideoCaptioner.Models;
using System.IO;

namespace LexiVerseVideoCaptioner.Services;

/// <summary>
/// 字幕エントリを SRT ファイルとして書き出すサービス
/// </summary>
public static class SrtExporter
{
    /// <summary>
    /// SRT ファイルを UTF-8 BOM 付きで書き出す（Windows の字幕ソフトとの互換性のため）
    /// </summary>
    public static async Task ExportAsync(
        IEnumerable<SubtitleEntry> entries,
        string outputPath)
    {
        var lines = entries.Select(e => e.ToSrtBlock());
        var content = string.Join("\r\n", lines);

        // UTF-8 BOM 付き（Windows 互換）
        await File.WriteAllTextAsync(outputPath, content, new System.Text.UTF8Encoding(true));
    }

    /// <summary>
    /// SRT ファイルを文字列として返す（プレビュー用）
    /// </summary>
    public static string ToSrtString(IEnumerable<SubtitleEntry> entries)
        => string.Join("\r\n", entries.Select(e => e.ToSrtBlock()));
}
