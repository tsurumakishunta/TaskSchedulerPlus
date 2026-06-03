# インストーラー手順

このドキュメントは、Task Scheduler Plus を Windows 環境へインストールするための手順をまとめたものです。

## 方式

現時点のインストーラーは ZIP 型の配布パッケージです。

- Web と Worker を `dotnet publish` で `win-x64` 向けに出力する
- `install.ps1` で Windows Service を登録する
- `uninstall.ps1` で Windows Service を削除する
- `Parameter` と `App_Data` を Web / Worker で共有する

将来的に MSI が必要になった場合は、この構成を WiX Toolset などへ載せ替える。

## パッケージ作成

リポジトリルートで以下を実行する。

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

作成先:

```text
artifacts/installer/TaskSchedulerPlus-Installer-win-x64.zip
```

既定では自己完結型として publish するため、インストール先に .NET Runtime が入っていなくても起動できる。

.NET Runtime を別途インストール済みの環境向けに小さくしたい場合は、以下を使う。

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1 -FrameworkDependent
```

## インストール

1. `TaskSchedulerPlus-Installer-win-x64.zip` を任意のフォルダへ展開する。
2. PowerShell を管理者として起動する。
3. 展開したフォルダで以下を実行する。

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

既定のインストール先:

```text
C:\Program Files\TaskSchedulerPlus
```

既定の Web URL:

```text
http://localhost:7255
```

インストール先を変更する場合:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -InstallRoot "D:\Apps\TaskSchedulerPlus"
```

サービスを登録するが起動しない場合:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoStart
```

## 登録されるサービス

| サービス名 | 表示名 | 役割 |
| --- | --- | --- |
| `TaskSchedulerPlusWeb` | Task Scheduler Plus Web | Blazor Web 管理画面 |
| `TaskSchedulerPlusWorker` | Task Scheduler Plus Worker | スケジュール監視とタスク実行 |

## 共有フォルダ

インストール先には以下のフォルダを作成する。

```text
TaskSchedulerPlus/
├─ Web/
│  └─ logs/
├─ Worker/
│  └─ logs/
├─ Parameter/
└─ App_Data/
```

`Parameter` と `App_Data` は Web / Worker で共有する。
ログは Web / Worker それぞれの実行直下に出力する。

## 注意点

Windows Service として実行する場合、既定ではサービスは `LocalSystem` で動作する。

そのため、タスクで実行する exe や bat は以下を考慮する。

- デスクトップ画面を表示する GUI アプリは通常のユーザー画面には表示されない
- ネットワーク共有や外部フォルダへアクセスする場合はサービス実行アカウントの権限が必要
- 必要に応じて Windows のサービス設定から実行アカウントを変更する

## アンインストール

PowerShell を管理者として起動し、以下を実行する。

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\TaskSchedulerPlus\uninstall.ps1"
```

既定ではサービスだけを削除し、設定ファイルやログは残す。

設定ファイルやログも含めて削除する場合:

```powershell
powershell -ExecutionPolicy Bypass -File "C:\Program Files\TaskSchedulerPlus\uninstall.ps1" -RemoveData
```
