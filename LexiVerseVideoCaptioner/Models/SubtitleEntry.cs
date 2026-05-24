namespace LexiVerseVideoCaptioner.Models;

/// <summary>
/// 字幕の1エントリ（開始時刻・終了時刻・テキスト）
/// </summary>
public class SubtitleEntry
{
    public int Index { get; set; }
    public TimeSpan StartTime { get; set; }
    public TimeSpan EndTime { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// SRT 形式の時間文字列 (HH:MM:SS,mmm)
    /// </summary>
    public static string FormatSrtTime(TimeSpan t)
        => $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2},{t.Milliseconds:D3}";

    public string ToSrtBlock()
        => $"{Index}\r\n{FormatSrtTime(StartTime)} --> {FormatSrtTime(EndTime)}\r\n{Text}\r\n";

    /// <summary>
    /// 現在の再生位置がこの字幕の表示範囲内か
    /// </summary>
    public bool IsActiveAt(TimeSpan position)
        => position >= StartTime && position < EndTime;
}
