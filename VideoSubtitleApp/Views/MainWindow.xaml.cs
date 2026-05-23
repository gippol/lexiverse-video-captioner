using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VideoSubtitleApp.Models;
using VideoSubtitleApp.Services;

namespace VideoSubtitleApp.Views;

public partial class MainWindow : Window
{
    // ── サービス
    private readonly TranscriptionService _transcriptionSvc;
    private readonly AudioExtractorService _audioExtractorSvc;
    private readonly ILogger<MainWindow> _logger;

    // ── 状態
    private VideoEntry? _currentVideoEntry;
    private string? _currentWavPath;
    private TimeSpan _videoDuration;
    private bool _isPlaying;
    private bool _isDraggingSeekBar;
    private bool _isTranscribing;
    private CancellationTokenSource? _transcriptionCts;

    // ── データ
    private readonly ObservableCollection<VideoEntry> _videoList = new();
    private readonly ObservableCollection<SubtitleEntry> _allSubtitles = new();
    private readonly ObservableCollection<SubtitleEntry> _filteredSubtitles = new();

    // ── タイマー (再生位置更新・字幕ハイライト用)
    private readonly DispatcherTimer _playerTimer;
    private readonly DispatcherTimer _loadingTimer;
    private DateTime _loadingStartTime;

    // ── 一時ファイルディレクトリ
    private static readonly string TempDir   = Path.Combine(Path.GetTempPath(), "VideoSubtitleApp");
    private static readonly string FFmpegDir = Path.Combine(AppContext.BaseDirectory, "tools/");
    private static readonly string ModelDir  = Path.Combine(AppContext.BaseDirectory, "models/");

    // ────────────────────────────────
    // コンストラクター
    // ────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(LogLevel.Information)
                .AddFilter("Microsoft", LogLevel.Warning)
                .AddFilter("System", LogLevel.Warning)
                .AddFilter("VideoSubtitleApp.Program", LogLevel.Debug);
        });
        _logger            = loggerFactory.CreateLogger<MainWindow>();
        _transcriptionSvc  = new TranscriptionService(loggerFactory.CreateLogger<TranscriptionService>());
        _audioExtractorSvc = new AudioExtractorService();

        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _playerTimer.Tick += PlayerTimer_Tick;

        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _loadingTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _loadingStartTime;
            TxtElapsedTime.Text = $"経過: {(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        };

        VideoList.ItemsSource    = _videoList;
        SubtitleList.ItemsSource = _filteredSubtitles;

        ComboLanguageSettings.ItemsSource   = new string[] { "日本語", "英語" };
        ComboLanguageSettings.SelectedIndex = 0;
        ComboModelSettings.ItemsSource      = TranscriptionService.GgmlTypeStr;
        ComboModelSettings.SelectedIndex    = TranscriptionService.GgmlTypeStr.IndexOf(TranscriptionService.DefalutModelName);

        AllowDrop = true;
        Drop     += MainWindow_Drop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                        ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };

        VideoPlayer.Volume = VolumeSlider.Value;
        Loaded += async (_, _) => await InitializeServicesAsync();
    }

    // ────────────────────────────────
    // 初期化
    // ────────────────────────────────
    private async Task InitializeServicesAsync()
    {
        ShowLoadingOverlay(true, "初期化", "初期化中...");
        try
        {
            SetStatus("FFmpeg を準備中...");
            await _audioExtractorSvc.EnsureFFmpegAsync(
                FFmpegDir,
                new Progress<string>(msg => Dispatcher.Invoke(() => SetStatus(msg))));

            SetStatus("Transcript service を初期化中...");
            await _transcriptionSvc.InitializeAsync(
                new Progress<string>(msg => Dispatcher.Invoke(() => SetStatus(msg))));

            SetStatus("準備完了！動画ファイルを開いてください。");
        }
        catch (Exception ex)
        {
            SetStatus($"初期化エラー: {ex.Message}");
            _logger.LogError(ex, "初期化に失敗しました");
        }
        finally
        {
            ShowLoadingOverlay(false);
        }
    }

    // ────────────────────────────────
    // 動画ファイルを開く
    // ────────────────────────────────
    private void BtnOpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title       = "動画ファイルを選択",
            Filter      = "動画ファイル|*.mp4;*.mov;*.avi;*.mkv;*.wmv;*.webm|すべてのファイル|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
                LoadVideo(file);
        }
    }

    private void MainWindow_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        var videoFiles = files.Where(IsVideoFile).ToArray();
        var srtFiles   = files.Where(f => Path.GetExtension(f).Equals(".srt", StringComparison.OrdinalIgnoreCase)).ToArray();

        foreach (var file in videoFiles)
            LoadVideo(file);

        foreach (var srt in srtFiles)
        {
            // 同時にドロップされた動画に対応するSRTはLoadVideo内で自動読み込みされるためスキップ
            var matchingDroppedVideo = videoFiles.FirstOrDefault(v =>
                Path.GetFileNameWithoutExtension(v).Equals(
                    Path.GetFileNameWithoutExtension(srt),
                    StringComparison.OrdinalIgnoreCase));
            if (matchingDroppedVideo == null)
                LoadSrt(srt);
        }
    }

    private void LoadVideo(string path)
    {
        // 既にリストにある場合はそこに切り替えるだけ
        var existing = _videoList.FirstOrDefault(v => v.FilePath == path);
        if (existing != null)
        {
            VideoList.SelectedItem = existing;
            return;
        }

        var entry = new VideoEntry { FilePath = path };
        _videoList.Add(entry);

        // 拡張子以外が同名の .srt を自動読み込み
        var srtPath = Path.ChangeExtension(path, ".srt");
        if (File.Exists(srtPath))
        {
            try
            {
                entry.Subtitles = SrtImporter.Import(srtPath);
                SetStatus($"SRT を自動読み込みしました: {Path.GetFileName(srtPath)}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SRT 自動読み込みに失敗: {path}", srtPath);
            }
        }

        VideoList.SelectedItem = entry;
    }

    private void SwitchToVideo(VideoEntry entry)
    {
        StopPlayback();

        _currentVideoEntry = entry;
        VideoPlayer.Source = new Uri(entry.FilePath);
        VideoPlayer.Play();
        VideoPlayer.Pause();
        SeekBar.Value = 0;
        Seek(TimeSpan.Zero);

        DropGuide.Visibility = Visibility.Collapsed;
        TxtFileName.Text     = entry.FileName;
        SetStatus($"動画を読み込みました: {entry.FileName}");

        // 字幕を復元
        _allSubtitles.Clear();
        _filteredSubtitles.Clear();
        TxtSearch.Text = string.Empty;
        foreach (var sub in entry.Subtitles)
        {
            _allSubtitles.Add(sub);
            _filteredSubtitles.Add(sub);
        }

        bool hasSubs = entry.Subtitles.Count > 0;
        TxtSubtitleCount.Text       = $"{_filteredSubtitles.Count} 件";
        BtnExportSrt.IsEnabled      = hasSubs;
        BtnClearSubtitles.IsEnabled = hasSubs;

        BtnPlayPause.IsEnabled  = true;
        BtnRewind.IsEnabled     = true;
        BtnForward.IsEnabled    = true;
        SeekBar.IsEnabled       = true;
        BtnTranscribe.IsEnabled = true;
    }

    private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoList.SelectedItem is VideoEntry entry && entry != _currentVideoEntry)
            SwitchToVideo(entry);
    }

    // ────────────────────────────────
    // SRT 読み込み
    // ────────────────────────────────
    private void BtnLoadSrt_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVideoEntry == null)
        {
            MessageBox.Show("先に動画を選択してください。",
                            "動画未選択", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "SRT ファイルを選択",
            Filter           = "SRT 字幕ファイル|*.srt|すべてのファイル|*.*",
            InitialDirectory = Path.GetDirectoryName(_currentVideoEntry.FilePath) ?? string.Empty
        };

        // 同名SRTがあれば初期ファイル名として提案
        var matchingSrt = Path.ChangeExtension(_currentVideoEntry.FilePath, ".srt");
        if (File.Exists(matchingSrt))
            dlg.FileName = Path.GetFileName(matchingSrt);

        if (dlg.ShowDialog() == true)
            LoadSrt(dlg.FileName);
    }

    private void LoadSrt(string srtPath)
    {
        if (_currentVideoEntry == null)
        {
            SetStatus("動画を先に読み込んでください。");
            return;
        }

        try
        {
            var entries = SrtImporter.Import(srtPath);

            _currentVideoEntry.Subtitles = entries;

            _allSubtitles.Clear();
            _filteredSubtitles.Clear();
            TxtSearch.Text = string.Empty;
            foreach (var sub in entries)
            {
                _allSubtitles.Add(sub);
                _filteredSubtitles.Add(sub);
            }

            bool hasSubs = entries.Count > 0;
            TxtSubtitleCount.Text       = $"{entries.Count} 件";
            BtnExportSrt.IsEnabled      = hasSubs;
            BtnClearSubtitles.IsEnabled = hasSubs;
            SubtitleOverlay.Visibility  = Visibility.Collapsed;

            SetStatus($"SRT を読み込みました: {Path.GetFileName(srtPath)} ({entries.Count} 件)");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"SRT の読み込みに失敗しました:\n{ex.Message}",
                            "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            _logger.LogError(ex, "SRT 読み込みに失敗: {path}", srtPath);
        }
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".webm" or ".mp3";
    }

    // ────────────────────────────────
    // 動画プレーヤーイベント
    // ────────────────────────────────
    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoDuration  = VideoPlayer.NaturalDuration.HasTimeSpan
                          ? VideoPlayer.NaturalDuration.TimeSpan
                          : TimeSpan.Zero;
        SeekBar.Maximum = _videoDuration.TotalSeconds;
        UpdateTimecode(TimeSpan.Zero);
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        VideoPlayer.Position = TimeSpan.Zero;
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        MessageBox.Show($"動画の再生に失敗しました:\n{e.ErrorException?.Message}",
                        "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        SetStatus("動画の読み込みに失敗しました。");
    }

    // ────────────────────────────────
    // 再生コントロール
    // ────────────────────────────────
    private void BtnPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) PausePlayback();
        else            StartPlayback();
    }

    private void StartPlayback()
    {
        VideoPlayer.Play();
        _isPlaying           = true;
        BtnPlayPause.Content = "⏸ 一時停止";
        _playerTimer.Start();
    }

    private void PausePlayback()
    {
        VideoPlayer.Pause();
        _isPlaying           = false;
        BtnPlayPause.Content = "▶ 再生";
        _playerTimer.Stop();
    }

    private void StopPlayback()
    {
        VideoPlayer.Stop();
        _isPlaying           = false;
        BtnPlayPause.Content = "▶ 再生";
        _playerTimer.Stop();
    }

    private void BtnRewind_Click(object sender, RoutedEventArgs e)
        => Seek(VideoPlayer.Position - TimeSpan.FromSeconds(10));

    private void BtnForward_Click(object sender, RoutedEventArgs e)
        => Seek(VideoPlayer.Position + TimeSpan.FromSeconds(10));

    private void Seek(TimeSpan target)
    {
        var clamped = TimeSpan.FromSeconds(
            Math.Clamp(target.TotalSeconds, 0, _videoDuration.TotalSeconds));
        VideoPlayer.Position = clamped;
        UpdateTimecode(clamped);
        UpdateSubtitleOverlay(clamped);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => VideoPlayer.Volume = e.NewValue;

    private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedCombo.SelectedItem is ComboBoxItem item)
        {
            var text = item.Content?.ToString()?.Replace("x", "") ?? "1.0";
            if (double.TryParse(text, out double speed))
                VideoPlayer.SpeedRatio = speed;
        }
    }

    private void SeekBar_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isDraggingSeekBar = true;
        if (_isPlaying) VideoPlayer.Pause();
    }

    private void SeekBar_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDraggingSeekBar = false;
        Seek(TimeSpan.FromSeconds(SeekBar.Value));
        if (_isPlaying) VideoPlayer.Play();
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingSeekBar)
            UpdateTimecode(TimeSpan.FromSeconds(e.NewValue));
    }

    private void PlayerTimer_Tick(object? sender, EventArgs e)
    {
        if (_isDraggingSeekBar) return;
        var pos = VideoPlayer.Position;
        SeekBar.Value = pos.TotalSeconds;
        UpdateTimecode(pos);
        UpdateSubtitleOverlay(pos);
    }

    private void UpdateTimecode(TimeSpan pos)
    {
        static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";
        TxtTimeCode.Text = $"{Fmt(pos)} / {Fmt(_videoDuration)}";
    }

    // ────────────────────────────────
    // 字幕オーバーレイ
    // ────────────────────────────────
    private void UpdateSubtitleOverlay(TimeSpan pos)
    {
        var active = _allSubtitles.FirstOrDefault(s => s.IsActiveAt(pos));
        if (active != null)
        {
            TxtSubtitleOverlay.Text    = active.Text;
            SubtitleOverlay.Visibility = Visibility.Visible;
            SubtitleList.ScrollIntoView(active);
        }
        else
        {
            SubtitleOverlay.Visibility = Visibility.Collapsed;
        }
    }

    // ────────────────────────────────
    // 字幕生成
    // ────────────────────────────────
    private async void BtnTranscribe_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVideoEntry == null || _isTranscribing) return;

        if (_allSubtitles.Count > 0)
        {
            var result = MessageBox.Show(
                "既存の字幕を上書きしますか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        // 処理対象エントリをローカルに保持（処理中に動画切り替えが起きても正しいエントリに保存するため）
        var targetEntry = _currentVideoEntry;

        _isTranscribing   = true;
        _transcriptionCts = new CancellationTokenSource();

        ShowLoadingOverlay(true, "🎙 字幕を生成中...", "準備中...");
        BtnTranscribe.IsEnabled = false;
        await Task.Yield(); // オーバーレイを描画してから重い処理を開始する

        try
        {
            var audioProgress = new Progress<string>(msg =>
                Dispatcher.Invoke(() =>
                {
                    TxtLoadingDetail.Text = msg;
                    LoadingProgress.Value = 20;
                }));

            _currentWavPath = await _audioExtractorSvc.ExtractAudioAsync(
                targetEntry.FilePath, TempDir, audioProgress, _transcriptionCts.Token);

            var transcribeProgress = new Progress<(string Message, double Percent)>(p =>
                Dispatcher.Invoke(() =>
                {
                    TxtLoadingDetail.Text = p.Message;
                    LoadingProgress.Value = 20 + p.Percent * 0.8;
                }));

            List<SubtitleEntry> entries = await _transcriptionSvc.TranscribeAsync(
                _currentWavPath,
                ModelDir,
                modelName: ComboModelSettings.SelectedValue?.ToString() ?? "",
                language: ComboLanguageSettings.SelectedIndex == 0 ? "ja" : "en",
                progress: transcribeProgress,
                ct: _transcriptionCts.Token);

            targetEntry.Subtitles = entries;

            // 現在表示中の動画が対象と一致する場合のみUIを更新
            if (_currentVideoEntry == targetEntry)
            {
                _allSubtitles.Clear();
                _filteredSubtitles.Clear();
                foreach (var entry in entries)
                {
                    _allSubtitles.Add(entry);
                    _filteredSubtitles.Add(entry);
                }

                bool hasSubs = entries.Count > 0;
                TxtSubtitleCount.Text       = $"{entries.Count} 件";
                BtnExportSrt.IsEnabled      = hasSubs;
                BtnClearSubtitles.IsEnabled = hasSubs;
            }

            SetStatus($"字幕を生成しました: {entries.Count} 件");
        }
        catch (OperationCanceledException)
        {
            SetStatus("字幕生成をキャンセルしました。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"字幕生成に失敗しました:\n{ex.Message}",
                            "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus($"エラー: {ex.Message}");
            _logger.LogError(ex, "字幕生成に失敗しました");
        }
        finally
        {
            _isTranscribing         = false;
            BtnTranscribe.IsEnabled = true;
            ShowLoadingOverlay(false);

            if (_currentWavPath != null && File.Exists(_currentWavPath))
            {
                try { File.Delete(_currentWavPath); } catch { /* 無視 */ }
                _currentWavPath = null;
            }
        }
    }

    private void BtnCancelTranscription_Click(object sender, RoutedEventArgs e)
        => _transcriptionCts?.Cancel();

    // ────────────────────────────────
    // SRT エクスポート
    // ────────────────────────────────
    private async void BtnExportSrt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "SRT ファイルを保存",
            Filter     = "SRT 字幕ファイル|*.srt|すべてのファイル|*.*",
            DefaultExt = ".srt",
            FileName   = _currentVideoEntry != null
                         ? Path.GetFileNameWithoutExtension(_currentVideoEntry.FilePath)
                         : "subtitle"
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                await SrtExporter.ExportAsync(_allSubtitles, dlg.FileName);
                SetStatus($"SRT を保存しました: {dlg.FileName}");
                MessageBox.Show("SRT ファイルを保存しました！", "保存完了",
                                MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存に失敗しました:\n{ex.Message}",
                                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ────────────────────────────────
    // 字幕クリア
    // ────────────────────────────────
    private void BtnClearSubtitles_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show("字幕をすべて削除しますか？",
                                     "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        if (_currentVideoEntry != null)
            _currentVideoEntry.Subtitles = new List<SubtitleEntry>();

        _allSubtitles.Clear();
        _filteredSubtitles.Clear();
        TxtSubtitleCount.Text       = "0 件";
        BtnExportSrt.IsEnabled      = false;
        BtnClearSubtitles.IsEnabled = false;
        SubtitleOverlay.Visibility  = Visibility.Collapsed;
        SetStatus("字幕をクリアしました。");
    }

    // ────────────────────────────────
    // 字幕リスト選択
    // ────────────────────────────────
    private void SubtitleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubtitleList.SelectedItem is SubtitleEntry entry)
        {
            Seek(entry.StartTime);
            SeekBar.Value             = VideoPlayer.Position.TotalSeconds;
            TxtEditSubtitle.IsEnabled = true;
            TxtEditSubtitle.Text      = entry.Text;
            BtnApplyEdit.IsEnabled    = true;
        }
        else
        {
            TxtEditSubtitle.IsEnabled = false;
            TxtEditSubtitle.Text      = string.Empty;
            BtnApplyEdit.IsEnabled    = false;
        }
    }

    // ────────────────────────────────
    // 字幕編集
    // ────────────────────────────────
    private void TxtEditSubtitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        BtnApplyEdit.IsEnabled = SubtitleList.SelectedItem != null;
    }

    private void BtnApplyEdit_Click(object sender, RoutedEventArgs e)
    {
        if (SubtitleList.SelectedItem is SubtitleEntry entry)
        {
            entry.Text = TxtEditSubtitle.Text;

            var idx = _filteredSubtitles.IndexOf(entry);
            if (idx >= 0)
            {
                _filteredSubtitles.RemoveAt(idx);
                _filteredSubtitles.Insert(idx, entry);
                SubtitleList.SelectedIndex = idx;
            }

            SetStatus("字幕を更新しました。");
        }
    }

    // ────────────────────────────────
    // 検索フィルター
    // ────────────────────────────────
    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = TxtSearch.Text.Trim().ToLowerInvariant();
        _filteredSubtitles.Clear();

        foreach (var sub in _allSubtitles)
        {
            if (string.IsNullOrEmpty(query) || sub.Text.ToLowerInvariant().Contains(query))
                _filteredSubtitles.Add(sub);
        }

        TxtSubtitleCount.Text = $"{_filteredSubtitles.Count} 件";
    }

    // ────────────────────────────────
    // ヘルパー
    // ────────────────────────────────
    private void SetStatus(string message) => TxtStatus.Text = message;

    private void ShowLoadingOverlay(bool show, string? title = null, string? detail = null)
    {
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            LoadingProgress.Value = 0;
            TxtLoadingTitle.Text  = title  ?? TxtLoadingTitle.Text;
            TxtLoadingDetail.Text = detail ?? TxtLoadingDetail.Text;
            TxtElapsedTime.Text   = "経過: 00:00";
            _loadingStartTime     = DateTime.Now;
            _loadingTimer.Start();
        }
        else
        {
            _loadingTimer.Stop();
            TxtElapsedTime.Text = "";
        }
    }

    // ────────────────────────────────
    // ウィンドウクローズ時のクリーンアップ
    // ────────────────────────────────
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _transcriptionCts?.Cancel();
        _playerTimer.Stop();
        VideoPlayer.Stop();

        try
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, recursive: true);
        }
        catch { /* 無視 */ }
    }
}
