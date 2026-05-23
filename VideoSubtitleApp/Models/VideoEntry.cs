using System.ComponentModel;
using System.IO;

namespace VideoSubtitleApp.Models;

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

    public event PropertyChangedEventHandler? PropertyChanged;
}
