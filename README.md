# Task Scheduler Plus

Task Scheduler Plus は、Windows タスクスケジューラーを少し拡張したような使い方を想定した、ワークフロー型のタスク実行管理ツールです。

Web 画面でワークフロー、タスク、スケジュール、実行履歴を管理し、Worker が指定時刻に外部プログラムを実行します。

Language: [日本語](#日本語) / [English](#english)

## Screenshots

![Login](docs/images/readme/login.png)

![Dashboard](docs/images/readme/dashboard.png)

![Workflows](docs/images/readme/workflows.png)

![Execution History](docs/images/readme/run-history.png)

## 日本語

### 主な機能

- ワークフローとタスクの登録、変更、削除
- `.exe`、`.bat`、`.cmd` の実行
- 終了コードによる成功、失敗判定
- 正常終了時に次のタスクへ進むかどうかの制御
- タスクスケジューラーのような詳細スケジュール設定
- Worker によるバックグラウンド実行
- 1日単位のガントチャート風の実行履歴
- ASP.NET Core Identity によるローカルログイン
- 管理者、一般ユーザー、参照者の権限管理
- ワークフロー定義の JSON インポート、エクスポート
- NLog によるログ出力

### 技術スタック

| 分類 | 使用技術 |
| --- | --- |
| 言語 | C# |
| フレームワーク | ASP.NET Core / Blazor Server |
| ランタイム | .NET 9 |
| データベース | SQL Server / SQL Server LocalDB |
| ORM | Entity Framework Core |
| 認証 | ASP.NET Core Identity |
| ログ | NLog |
| UI | Bootstrap 5 |
| 実行エンジン | .NET Worker Service |

### 必要環境

- Windows
- .NET 9 SDK
- SQL Server または SQL Server LocalDB

### 起動方法

依存関係を復元します。

```powershell
dotnet restore .\src\TaskSchedulerPlus.sln
```

ソリューションをビルドします。

```powershell
dotnet build .\src\TaskSchedulerPlus.sln
```

Worker を起動します。

```powershell
dotnet run --project .\src\TaskSchedulerPlus.Worker\TaskSchedulerPlus.Worker.csproj
```

Web アプリを起動します。

```powershell
dotnet run --project .\src\TaskSchedulerPlus.Web\TaskSchedulerPlus.Web.csproj
```

初回起動時は初期設定画面で、SQL Server の接続先と初期管理者ユーザーを登録します。

### 使い方

1. 初期設定でデータベース接続先と管理者ユーザーを作成します。
2. ログイン後、ワークフローを作成します。
3. ワークフローにタスクを登録します。
4. スケジュールを設定します。
5. Worker がスケジュールを監視し、指定時刻にタスクを実行します。
6. 実行結果は実行履歴画面で確認できます。

### インストーラー

Windows Service として利用するためのインストーラー作成スクリプトがあります。

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

詳細は [docs/INSTALLER.md](docs/INSTALLER.md) を参照してください。

### ドキュメント

- [詳細設計書](docs/DETAILED_DESIGN.md)
- [インストーラー手順](docs/INSTALLER.md)

### ライセンス

このプロジェクトは MIT License です。
詳細は [LICENSE](LICENSE) を参照してください。

---

## English

Task Scheduler Plus is a workflow-based task execution management tool designed as a small extension of Windows Task Scheduler.

The Web application manages workflows, tasks, schedules, and execution history.
The Worker monitors schedules and runs external programs at the specified time.

### Features

- Create, edit, and delete workflows and tasks
- Execute `.exe`, `.bat`, and `.cmd` files
- Determine success or failure by exit code
- Control whether the next task runs after success
- Detailed schedule settings
- Background execution by Worker
- Daily Gantt-style execution history
- Local authentication with ASP.NET Core Identity
- Role management for administrators, general users, and viewers
- Workflow JSON import and export
- Logging with NLog

### Tech Stack

| Category | Technology |
| --- | --- |
| Language | C# |
| Framework | ASP.NET Core / Blazor Server |
| Runtime | .NET 9 |
| Database | SQL Server / SQL Server LocalDB |
| ORM | Entity Framework Core |
| Authentication | ASP.NET Core Identity |
| Logging | NLog |
| UI | Bootstrap 5 |
| Execution Engine | .NET Worker Service |

### Requirements

- Windows
- .NET 9 SDK
- SQL Server or SQL Server LocalDB

### Getting Started

Restore dependencies.

```powershell
dotnet restore .\src\TaskSchedulerPlus.sln
```

Build the solution.

```powershell
dotnet build .\src\TaskSchedulerPlus.sln
```

Run the Worker.

```powershell
dotnet run --project .\src\TaskSchedulerPlus.Worker\TaskSchedulerPlus.Worker.csproj
```

Run the Web application.

```powershell
dotnet run --project .\src\TaskSchedulerPlus.Web\TaskSchedulerPlus.Web.csproj
```

On first launch, use the setup screen to register the SQL Server connection and initial administrator account.

### Basic Usage

1. Configure the database connection and create the administrator account.
2. Log in and create a workflow.
3. Register tasks in the workflow.
4. Configure the schedule.
5. The Worker monitors schedules and executes tasks at the specified time.
6. Check execution results in the execution history screen.

### Installer

An installer build script is available for using the application as Windows Services.

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

See [docs/INSTALLER.md](docs/INSTALLER.md) for details.

### Documentation

- [Detailed Design](docs/DETAILED_DESIGN.md)
- [Installer Guide](docs/INSTALLER.md)

### License

This project is licensed under the MIT License.
See [LICENSE](LICENSE) for details.
