# 🎬 LexiVerse Video Captioner

**Whisper** を使ってローカルで動く動画字幕生成 WPF アプリ

---

## ✨ 主な機能

| 機能 | 詳細 |
|------|------|
| 🎙 音声→字幕 | Whisper モデルでオフライン文字起こし |
| 🇯🇵 日本語対応 | `language = "ja"` で日本語を優先認識 |
| ▶ 動画プレーヤー | WPF MediaElement による MP4 再生 |
| 🟡 字幕オーバーレイ | 再生位置に合わせてリアルタイムに字幕表示 |
| ✏ 字幕編集 | リスト上で字幕テキストをその場で編集 |
| 🔍 検索フィルター | テキスト検索で字幕を絞り込み |
| 💾 SRT 出力 | 標準的な SRT ファイルとして書き出し |

---

## 🔧 セットアップ

### 必要環境

- **Windows 10 22H2 以降**
- **.NET 10 SDK**
- **Visual Studio**
- インターネット接続（初回のモデルダウンロードに必要）

### ビルド手順

```powershell
# 1. プロジェクトをクローン / フォルダを開く
cd VideoSubtitleApp

# 2. WinML NuGet パッケージは ORT フィードが必要なので nuget.config が同梱されています

# 3. リストア & ビルド（win-x64 をターゲット指定）
dotnet restore -r win-x64
dotnet build -r win-x64 -c Release

# 4. 実行
dotnet run -r win-x64
```

> **注意**: `net10.0-windows10.0.26100.0` と RuntimeIdentifier `win-x64` の指定が必須です。

---

## 📦 依存パッケージ

| パッケージ | 用途 | ライセンス |
|-----------|------|-------|
| `Whisper.net` | Whisper ローカル推論 | MIT (https://www.nuget.org/packages/Whisper.net/1.9.0/license) |
| `Microsoft.Extensions.Logging` | ロギング | MIT |

---

## 🗂 プロジェクト構成

```
VideoSubtitleApp/
├── App.xaml / App.xaml.cs          # アプリエントリポイント & グローバルスタイル
├── VideoSubtitleApp.csproj         # プロジェクト設定
├── nuget.config                    # ORT フィード設定
├── Models/
│   └── SubtitleEntry.cs            # 字幕データモデル
├── Services/
│   ├── TranscriptionService.cs     # Foundry Local × Whisper
│   ├── AudioExtractorService.cs    # FFmpeg で MP4→WAV
│   └── SrtExporter.cs             # SRT ファイル出力
└── Views/
    ├── MainWindow.xaml             # メイン UI
    └── MainWindow.xaml.cs          # コードビハインド
```

---

## 🎨 UI の操作方法

1. **「📂 動画を開く」** でMP4を選択（ドラッグ＆ドロップも可）
2. **「🎙 字幕を生成」** をクリック → Whisper が音声を文字起こし
   - 初回はモデルのダウンロード（数分）が発生します
3. 右パネルの字幕リストで内容を確認・編集
4. 動画を再生すると、現在位置に対応する字幕がオーバーレイ表示
5. **「💾 SRT 保存」** で字幕ファイルを書き出し

---

## ⚠ よくある問題

### 日本語が文字化けする

→ Whisper の自動言語検出は英語に偏ることがあります。
   `TranscriptionService.cs` の `language` を `"ja"` に明示していますが、
   `whisper-base` や `whisper-medium` に切り替えると精度が上がります。
   
### Nugetのエラーでたとき、やってみること
→ dotnet nuget locals all --clear
  bin/ obj/ ディレクトリ削除
  で再び `dotnet restore` から実行


---

## 📄 ライセンス

MIT License
