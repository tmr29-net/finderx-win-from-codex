# FinderX for Windows

Windows 向けの軽量ファイル管理アプリです。WPF / .NET 8 で作成しています。

## 開発時の起動

このコマンドは開発用です。Windows に .NET 8 SDK が必要です。SDK のインストール権限がない PC では、利用者が SDK を導入する必要のない配布版を別途作成してください。

```powershell
dotnet run --project .\Findr.csproj
```

## SDK なしで利用する配布版

開発用 PC に .NET 8 SDK がある場合は、自己完結型の Windows x64 配布版を作成できます。利用者の PC への SDK インストールは不要です。

```powershell
dotnet publish .\Findr.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

生成物は `bin\Release\net8.0-windows\win-x64\publish\Findr.exe` です。通常ユーザー権限で起動できます。書き込み権限のない場所の操作は Windows のアクセス許可に従います。

## 主な機能

- フォルダーとドライブの閲覧、戻る・進む・上の階層への移動
- タブ、フォルダー以下の名前検索、お気に入り、最近使った場所
- 詳細表示と大きなアイコン表示（フォルダーごとに保存）
- ファイルを開く、コピー、切り取り、貼り付け、名前変更、ごみ箱への削除
- 新規フォルダー、プロパティ表示、ドラッグ＆ドロップ
- フォルダーごとの画像アイコン設定
- 画像とテキスト系ファイルの簡易プレビュー
- ZIP 展開と、展開成功後に元 ZIP をごみ箱へ移動する選択

ユーザー設定は `%APPDATA%\Findr\settings.json` に保存されます。
