using System.ComponentModel;
using System.IO;

namespace LexiVerseVideoCaptioner.Models;

public class VideoEntry : INotifyPropertyChanged
{
    private List<SubtitleEntry> _subtitles = new();

    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);

    public List<SubtitleEntry> Subtitles
    {
        get => _subtitles;
        set
        {
            _subtitles = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitles)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SubtitleCount)));
        }
    }

    public int SubtitleCount => _subtitles.Count;

    /// <summary>
    /// 字幕を生成・編集してから SRT に保存していないか（SRT を読み込んだ・保存した時点で false に戻す）
    /// </summary>
    public bool HasUnsavedChanges { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
