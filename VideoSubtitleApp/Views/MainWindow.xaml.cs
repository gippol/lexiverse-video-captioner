using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using VideoSubtitleApp.Models;
using VideoSubtitleApp.Services;

namespace VideoSubtitleApp.Views;

// ────────────────────────────────────────────────────────────────────
// メインウィンドウ
// ────────────────────────────────────────────────────────────────────
public partial class MainWindow : Window
{
    // ── サービス
    private readonly TranscriptionService _transcriptionSvc;
    private readonly AudioExtractorService _audioExtractorSvc;
    private readonly ILogger<MainWindow> _logger;

    // ── 状態
    private string? _currentVideoPath;
    private string? _currentWavPath;
    private TimeSpan _videoDuration;
    private bool _isPlaying;
    private bool _isDraggingSeekBar;
    private bool _isTranscribing;
    private CancellationTokenSource? _transcriptionCts;

    // ── データ
    private readonly ObservableCollection<SubtitleEntry> _allSubtitles  = new();
    private readonly ObservableCollection<SubtitleEntry> _filteredSubtitles = new();

    // ── タイマー (再生位置更新・字幕ハイライト用)
    private readonly DispatcherTimer _playerTimer;

    // ── 一時ファイルディレクトリ
    private static readonly string TempDir    = Path.Combine(Path.GetTempPath(), "VideoSubtitleApp");
    private static readonly string FFmpegDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoSubtitleApp", "ffmpeg");

    // ────────────────────────────────
    // コンストラクター
    // ────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
            // 最小ログレベルを「Information」に設定（DebugやTraceは除外される）
            .SetMinimumLevel(LogLevel.Information)
            
            // 特定のカテゴリ（名前空間など）ごとに個別のログレベルを設定
            .AddFilter("Microsoft", LogLevel.Warning)
            .AddFilter("System", LogLevel.Warning)
            .AddFilter("VideoSubtitleApp.Program", LogLevel.Debug);
        });
        _logger              = loggerFactory.CreateLogger<MainWindow>();
        _transcriptionSvc    = new TranscriptionService(loggerFactory.CreateLogger<TranscriptionService>());
        _audioExtractorSvc   = new AudioExtractorService();

        // プレーヤータイマー
        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _playerTimer.Tick += PlayerTimer_Tick;

        // 字幕リストのバインディング
        SubtitleList.ItemsSource = _filteredSubtitles;

        // 言語設定
        ComboLanguageSettings.ItemsSource = new string[] { "日本語", "英語" };
        ComboLanguageSettings.SelectedIndex = 0;

        // モデル設定
        ComboModelSettings.ItemsSource = TranscriptionService.GgmlTypeStr;
        ComboModelSettings.SelectedIndex = TranscriptionService.GgmlTypeStr.IndexOf(TranscriptionService.DefalutModelName);

        // ドラッグ＆ドロップ
        AllowDrop = true;
        Drop += MainWindow_Drop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                        ? DragDropEffects.Copy
                        : DragDropEffects.None;
            e.Handled = true;
        };

        // ボリューム初期値
        VideoPlayer.Volume = VolumeSlider.Value;

        // 初期化（非同期）
        Loaded += async (_, _) => await InitializeServicesAsync();
    }

    // ────────────────────────────────
    // 初期化
    // ────────────────────────────────
    private async Task InitializeServicesAsync()
    {
        try
        {
            SetStatus("FFmpeg を準備中...");
            await _audioExtractorSvc.EnsureFFmpegAsync(
                FFmpegDir,
                new Progress<string>(msg => Dispatcher.Invoke(() => SetStatus(msg))));

            SetStatus("Foundry Local SDK を初期化中...");
            await _transcriptionSvc.InitializeAsync(
                new Progress<string>(msg => Dispatcher.Invoke(() => SetStatus(msg))));

            SetStatus("準備完了！動画ファイルを開いてください。");
        }
        catch (Exception ex)
        {
            SetStatus($"初期化エラー: {ex.Message}");
            _logger.LogError(ex, "初期化に失敗しました");
        }
    }

    // ────────────────────────────────
    // 動画ファイルを開く
    // ────────────────────────────────
    private void BtnOpenVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "動画ファイルを選択",
            Filter = "動画ファイル|*.mp4;*.mov;*.avi;*.mkv;*.wmv;*.webm|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() == true)
            LoadVideo(dlg.FileName);
    }

    private void MainWindow_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadVideo(files[0]);
    }

    private void LoadVideo(string path)
    {
        _currentVideoPath = path;
        VideoPlayer.Source = new Uri(path);
        VideoPlayer.Play();
        VideoPlayer.Pause();

        DropGuide.Visibility = Visibility.Collapsed;
        TxtFileName.Text    = Path.GetFileName(path);
        SetStatus($"動画を読み込みました: {Path.GetFileName(path)}");

        // 再生コントロールを有効化
        BtnPlayPause.IsEnabled = true;
        BtnRewind.IsEnabled    = true;
        BtnForward.IsEnabled   = true;
        SeekBar.IsEnabled      = true;
        BtnTranscribe.IsEnabled = true;
    }

    // ────────────────────────────────
    // 動画プレーヤーイベント
    // ────────────────────────────────
    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoDuration    = VideoPlayer.NaturalDuration.HasTimeSpan
                            ? VideoPlayer.NaturalDuration.TimeSpan
                            : TimeSpan.Zero;
        SeekBar.Maximum   = _videoDuration.TotalSeconds;
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
        _isPlaying             = true;
        BtnPlayPause.Content   = "⏸ 一時停止";
        _playerTimer.Start();
    }

    private void PausePlayback()
    {
        VideoPlayer.Pause();
        _isPlaying             = false;
        BtnPlayPause.Content   = "▶ 再生";
        _playerTimer.Stop();
    }

    private void StopPlayback()
    {
        VideoPlayer.Stop();
        _isPlaying             = false;
        BtnPlayPause.Content   = "▶ 再生";
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

    // シークバー
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

    // タイマーで再生位置を更新
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
            TxtSubtitleOverlay.Text     = active.Text;
            SubtitleOverlay.Visibility  = Visibility.Visible;

            // リストも自動スクロール（選択はしない）
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
        if (_currentVideoPath == null) return;
        if (_isTranscribing)           return;

        // 既存の字幕がある場合は確認
        if (_allSubtitles.Count > 0)
        {
            var result = MessageBox.Show(
                "既存の字幕を上書きしますか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        _isTranscribing   = true;
        _transcriptionCts = new CancellationTokenSource();

        ShowLoadingOverlay(true);
        BtnTranscribe.IsEnabled = false;

        try
        {
            // Step 1: 音声抽出
            var audioProgress = new Progress<string>(msg =>
                Dispatcher.Invoke(() =>
                {
                    TxtLoadingDetail.Text = msg;
                    LoadingProgress.Value = 20;
                }));

            _currentWavPath = await _audioExtractorSvc.ExtractAudioAsync(
                _currentVideoPath, TempDir, audioProgress, _transcriptionCts.Token);

            // Step 2: 文字起こし
            var transcribeProgress = new Progress<(string Message, double Percent)>(p =>
                Dispatcher.Invoke(() =>
                {
                    TxtLoadingDetail.Text = p.Message;
                    LoadingProgress.Value = 20 + p.Percent * 0.8;
                }));

            List<SubtitleEntry> entries = await _transcriptionSvc.TranscribeAsync(
                    _currentWavPath,
                    modelName: ComboModelSettings.SelectedValue.ToString() ?? "",
                    language: ComboLanguageSettings.SelectedIndex == 0 ? "ja" : "en",
                    progress: transcribeProgress,
                    ct: _transcriptionCts.Token);


            // UIに反映
            _allSubtitles.Clear();
            _filteredSubtitles.Clear();
            foreach (var entry in entries)
            {
                _allSubtitles.Add(entry);
                _filteredSubtitles.Add(entry);
            }

            TxtSubtitleCount.Text   = $"{entries.Count} 件";
            BtnExportSrt.IsEnabled  = entries.Count > 0;
            BtnClearSubtitles.IsEnabled = entries.Count > 0;
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

            // 一時WAVを削除
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
            FileName   = _currentVideoPath != null
                         ? Path.GetFileNameWithoutExtension(_currentVideoPath)
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

        _allSubtitles.Clear();
        _filteredSubtitles.Clear();
        TxtSubtitleCount.Text         = "0 件";
        BtnExportSrt.IsEnabled        = false;
        BtnClearSubtitles.IsEnabled   = false;
        SubtitleOverlay.Visibility    = Visibility.Collapsed;
        SetStatus("字幕をクリアしました。");
    }

    // ────────────────────────────────
    // 字幕リスト選択
    // ────────────────────────────────
    private void SubtitleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubtitleList.SelectedItem is SubtitleEntry entry)
        {
            // 字幕の開始時刻にシーク
            Seek(entry.StartTime);
            var pos = VideoPlayer.Position;
            SeekBar.Value = pos.TotalSeconds;

            // 編集エリアを更新
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
        // 変更があったら適用ボタンを強調
        BtnApplyEdit.IsEnabled = SubtitleList.SelectedItem != null;
    }

    private void BtnApplyEdit_Click(object sender, RoutedEventArgs e)
    {
        if (SubtitleList.SelectedItem is SubtitleEntry entry)
        {
            entry.Text = TxtEditSubtitle.Text;

            // ObservableCollection はプロパティ変更を自動通知しないので
            // リストを手動でリフレッシュ
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
            if (string.IsNullOrEmpty(query) ||
                sub.Text.ToLowerInvariant().Contains(query))
            {
                _filteredSubtitles.Add(sub);
            }
        }

        TxtSubtitleCount.Text = $"{_filteredSubtitles.Count} 件";
    }

    // ────────────────────────────────
    // ヘルパー
    // ────────────────────────────────
    private void SetStatus(string message)
    {
        TxtStatus.Text = message;
    }

    private void ShowLoadingOverlay(bool show)
    {
        LoadingOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            LoadingProgress.Value = 0;
            TxtLoadingDetail.Text = "準備中...";
        }
    }

    string ToSrtTime(TimeSpan time)
    {
        return time.ToString(@"hh\:mm\:ss\,fff");
    }

    // ────────────────────────────────
    // ウィンドウクローズ時のクリーンアップ
    // ────────────────────────────────
    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _transcriptionCts?.Cancel();
        _playerTimer.Stop();
        VideoPlayer.Stop();
        //await _transcriptionSvc.DisposeAsync();

        // 一時ディレクトリを掃除
        try
        {
            if (Directory.Exists(TempDir))
                Directory.Delete(TempDir, recursive: true);
        }
        catch { /* 無視 */ }
    }
}
