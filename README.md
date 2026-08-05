# Builder

Windows デスクトップ向けのプロジェクト管理・ビルドランチャーアプリケーションです。複数のプロジェクトを一元管理し、ビルド・起動コマンドやカスタムスクリプトを統一されたインターフェースから実行できます。

## クイックスタート（コピペで実行）

以下を PowerShell にそのまま貼り付けると、クローン → ビルド → 起動まで実行されます。（要 [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) / [Git](https://git-scm.com/downloads)）

```powershell
git clone https://github.com/sk0ya/Builder.git "$env:USERPROFILE\Builder"; cd "$env:USERPROFILE\Builder"; dotnet build -c Release; dotnet run --project Builder -c Release
```

すでにクローン済みの場合は、リポジトリのルートで以下を実行すると最新版に更新して起動します。

```powershell
git pull; dotnet build -c Release; dotnet run --project Builder -c Release
```

## スクリーンショット

<!-- スクリーンショットをここに追加 -->

## 機能

- **プロジェクト管理** - フォルダ選択でプロジェクトを登録・管理
- **ビルド & 起動** - プロジェクトごとにビルドコマンド・起動コマンドを設定・実行
- **Git 連携** - Git リポジトリの自動検出と Git Pull の実行
- **カスタムアクション** - プロジェクトごとに PowerShell スクリプトを作成・実行
- **リアルタイム出力** - コマンド実行結果をコンソール風にリアルタイム表示
- **テーマカスタマイズ** - 背景色・アクセントカラーの変更に対応

## 技術スタック

- **.NET 9.0** / **C# 13**
- **WPF** (Windows Presentation Foundation)
- **Material Design In XAML** - モダンな UI テーマ
- **CommunityToolkit.Mvvm** - MVVM パターンの実装

## プロジェクト構成

```
Builder/
├── Models/
│   ├── ProjectEntry.cs       # プロジェクトデータモデル
│   └── ProjectAction.cs      # カスタムアクションモデル
├── ViewModels/
│   └── MainViewModel.cs      # メインビューモデル
├── Services/
│   ├── ProcessService.cs     # プロセス実行サービス
│   └── SettingsService.cs    # 設定永続化サービス
├── Converters/               # 値コンバーター
├── MainWindow.xaml           # メインウィンドウ
├── ActionEditDialog.xaml     # アクション編集ダイアログ
├── SettingsDialog.xaml       # テーマ設定ダイアログ
└── Builder.csproj
```

## ビルド方法

### 前提条件

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### ビルド & 実行

```bash
dotnet build
dotnet run --project Builder
```

## 設定

設定ファイルは `%AppData%/Builder/settings.json` に保存されます。

## ライセンス

<!-- ライセンスをここに記載 -->
