# 🎬 LexiVerse Video Captioner

![screenshot](images/doc/app_image.png)

**Whisper** を使ってローカルで動く動画字幕生成 WPF アプリ

---

## ✨ 主な機能

| 機能 | 詳細 |
|------|------|
| 🎙 音声→字幕 | Whisper モデルでオフライン文字起こし（クラウド API 不要） |
| ⚡ GPU 対応 | Vulkan 対応 GPU があれば自動で使用（NVIDIA / AMD / Intel、内蔵 GPU も可）。無い場合は CPU で動作 |
| 🌐 言語選択 | 日本語 / 英語を画面上で切り替え |
| 🧠 モデル選択 | Tiny 〜 LargeV3Turbo を画面上で切り替え（標準は `LargeV3Turbo`） |
| ▶ 動画プレーヤー | WPF MediaElement による動画再生（複数動画をリストで切り替え） |
| 🟡 字幕オーバーレイ | 再生位置に合わせてリアルタイムに字幕表示 |
| ✏ 字幕編集 | リスト上で字幕テキストをその場で編集 |
| 🔍 検索フィルター | テキスト検索で字幕を絞り込み |
| 📄 SRT 読み込み | 既存の SRT を開く。動画と同名の `.srt` は自動で読み込み |
| 💾 SRT 出力 | 標準的な SRT ファイル（UTF-8 BOM 付き）として書き出し |

---

## 🔧 セットアップ

### 必要環境

- **Windows 10 22H2 以降**
- **.NET 10 SDK**
- **Visual Studio**（任意）
- インターネット接続（初回の FFmpeg・モデルのダウンロードに必要）

### ビルド手順

```powershell
# 1. プロジェクトをクローン / フォルダを開く
cd lexiverse-video-captioner

# 2. リストア & ビルド（win-x64 をターゲット指定）
dotnet restore LexiVerseVideoCaptioner -r win-x64
dotnet build LexiVerseVideoCaptioner -r win-x64 -c Release

# 3. 実行
dotnet run --project LexiVerseVideoCaptioner -r win-x64

# （任意）単一 exe として発行
dotnet publish LexiVerseVideoCaptioner -r win-x64 -c Release
```

> **注意**: `net10.0-windows10.0.26100.0` と RuntimeIdentifier `win-x64` の指定が必須です。

---

## 📦 依存パッケージ

| パッケージ | 用途 | ライセンス |
|-----------|------|-------|
| `Whisper.net` | Whisper ローカル推論 | MIT (https://www.nuget.org/packages/Whisper.net/1.9.0/license) |
| `Whisper.net.Runtime` | CPU 推論用ネイティブランタイム | MIT |
| `Whisper.net.Runtime.Vulkan` | GPU (Vulkan) 推論用ネイティブランタイム | MIT |
| `Microsoft.Extensions.Logging` / `.Debug` | ロギング | MIT |

実行時にダウンロードするもの:

| もの | 取得元 | ライセンス |
|------|--------|-----------|
| FFmpeg（`ffmpeg.exe`） | [FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds) の固定バージョン（n9.0.2, win64 LGPL ビルド） | LGPL |
| Whisper モデル（Q8_0 量子化） | [sandrohanea/whisper.net](https://huggingface.co/sandrohanea/whisper.net)（Hugging Face） | MIT |

---

## 🗂 プロジェクト構成

```
LexiVerseVideoCaptioner/
├── App.xaml / App.xaml.cs           # アプリエントリポイント & グローバルスタイル
├── LexiVerseVideoCaptioner.csproj   # プロジェクト設定
├── Models/
│   ├── SubtitleEntry.cs             # 字幕データモデル
│   └── VideoEntry.cs                # 動画リストの 1 項目（パス + 字幕）
├── Services/
│   ├── TranscriptionService.cs      # Whisper による書き起こし・モデル管理
│   ├── AudioExtractorService.cs     # FFmpeg で動画→WAV（16kHz モノラル）
│   ├── SrtExporter.cs               # SRT ファイル出力
│   ├── SrtImporter.cs               # SRT ファイル読み込み
│   ├── HashVerifier.cs              # ダウンロードファイルの SHA-256 検証
│   └── FileLoggerProvider.cs        # ログファイル出力
├── converter/
│   └── TimeSpanToStringConverter.cs # MM:SS 表示用コンバーター
└── Views/
    ├── MainWindow.xaml              # メイン UI
    └── MainWindow.xaml.cs           # コードビハインド
```

---

## 🎨 UI の操作方法

1. **「📂 動画を開く」** で動画を選択（ドラッグ＆ドロップも可。複数可）
2. 画面上部で **言語** と **モデル** を選択
3. **「🎙 字幕を生成」** をクリック → Whisper が音声を文字起こし
   - 初回は FFmpeg とモデルのダウンロードが発生します（`LargeV3Turbo` は約 870MB）
   - 処理時間は PC の性能・GPU の有無・モデルによって大きく変わります
   - 途中で **「⏹ キャンセル」** で中断できます
4. 右パネルの字幕リストで内容を確認・編集
5. 動画を再生すると、現在位置に対応する字幕がオーバーレイ表示
6. **「💾 SRT 保存」** で字幕ファイルを書き出し（**「📄 SRT を開く」** で既存の字幕も読み込めます）

---

## 💡 変換を速くするには

- **GPU を使う**: Vulkan 対応 GPU があれば自動で使われます。グラフィックドライバーを最新にしてください。
  変換中にタスクマネージャーの「GPU」使用率が上がっていれば GPU で動いています。
- **モデルを選ぶ**: `LargeV3Turbo`（標準）は Large-v3 に近い精度でより高速です。
  精度より速度を優先する場合は `Small` / `Base` を選んでください。
- **2 回目以降は速い**: 一度読み込んだモデルはアプリを閉じるまで使い回します。

---

## 📁 データの保存場所

| もの | 場所 |
|------|------|
| FFmpeg | `%LOCALAPPDATA%\LexiVerseVideoCaptioner\tools\` |
| Whisper モデル | `%LOCALAPPDATA%\LexiVerseVideoCaptioner\models\` |
| ログ | `%LOCALAPPDATA%\LexiVerseVideoCaptioner\logs\app.log` |
| 一時 WAV | `%TEMP%\LexiVerseVideoCaptioner\`（終了時に削除） |

> 旧バージョンでアプリのフォルダ（`tools\` / `models\`）に保存していたファイルは、起動時に自動で上記へ移動します。

---

## 🔒 セキュリティ

- FFmpeg は **バージョンを固定** し、ダウンロードした zip と `ffmpeg.exe` を **SHA-256 で検証** してから使用します。
  起動時にも `ffmpeg.exe` を検証し、一致しない場合は再ダウンロードします。
- Whisper モデルもダウンロード後に **SHA-256 で検証** します。途中で中断しても壊れたファイルは残りません。
- PATH 上に FFmpeg がインストールされている場合はそちらを使いますが、**フルパスで起動** します
  （カレントディレクトリに置かれた別の `ffmpeg.exe` が実行されることはありません）。

---

## ⚠ よくある問題

### 文字起こし結果が変・別の言語になる

→ 画面上部の **言語** が動画の言語と合っているか確認してください。
  精度が足りない場合は、より大きいモデル（`LargeV3Turbo` など）を選んでください。

### 動かない・エラーになる

→ ログファイル `%LOCALAPPDATA%\LexiVerseVideoCaptioner\logs\app.log` にエラーの詳細が出ています。

### モデルの読み込みに失敗する

→ `%LOCALAPPDATA%\LexiVerseVideoCaptioner\models\` から該当する `ggml_*.bin` を削除して、再度字幕を生成してください（再ダウンロードされます）。

### Nugetのエラーが出たとき、やってみること

→ `dotnet nuget locals all --clear`

  bin/ obj/ ディレクトリ削除後、再び `dotnet restore` から実行

---

## 🌐 インターネット接続

以下のときにインターネットに接続します。すでにダウンロード済みの場合は接続しません。

* Whisper のモデル（Whisper.net が使用）のダウンロード — Hugging Face
* FFmpeg のダウンロード — GitHub（PATH 上に FFmpeg がある場合は不要）

動画や音声、字幕の内容が外部に送信されることはありません。

---

## 📄 ライセンス

MIT License
