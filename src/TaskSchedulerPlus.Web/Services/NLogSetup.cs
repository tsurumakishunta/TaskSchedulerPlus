using System.Text;
using NLog;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using NLogLevel = NLog.LogLevel;

namespace TaskSchedulerPlus.Web.Services;

// Web と Worker で共通利用する NLog 設定をコードで組み立てます。
public static class NLogSetup
{
    public static void Configure(string contentRootPath)
    {
        // ログは各プログラムの実行直下に出力します。
        // 開発時は各プロジェクト直下、インストール後は Web / Worker の publish フォルダ直下になります。
        var logDirectory = Path.Combine(contentRootPath, "logs");

        Directory.CreateDirectory(logDirectory);

        InternalLogger.LogLevel = NLogLevel.Warn;
        InternalLogger.LogFile = Path.Combine(logDirectory, "nlog-internal.log");

        // 時刻、レベル、プロセスID、スレッドID、ロガー名、本文、構造化プロパティ、例外を同じ形式で出力します。
        // どの処理がどの入力で動き、どこで分岐または失敗したかを後から追いやすくするためです。
        var configuration = new LoggingConfiguration();
        var commonLayout =
            "${longdate}|${uppercase:${level}}|pid=${processid}|tid=${threadid}|${logger}|${message}|${all-event-properties:separator=|}${onexception:inner=|${exception:format=tostring}}";

        var consoleTarget = new ColoredConsoleTarget("console")
        {
            Layout = commonLayout
        };

        // application.log は Info 以上、error.log は Warn 以上を出力します。
        var applicationFileTarget = CreateFileTarget(
            "applicationFile",
            Path.Combine(logDirectory, "application.log"),
            commonLayout);

        var errorFileTarget = CreateFileTarget(
            "errorFile",
            Path.Combine(logDirectory, "error.log"),
            commonLayout);

        configuration.AddRule(NLogLevel.Info, NLogLevel.Fatal, consoleTarget);
        configuration.AddRule(NLogLevel.Info, NLogLevel.Fatal, applicationFileTarget);
        configuration.AddRule(NLogLevel.Warn, NLogLevel.Fatal, errorFileTarget);

        LogManager.Configuration = configuration;
    }

    private static FileTarget CreateFileTarget(string name, string fileName, string layout) => new(name)
    {
        // 日次でローテーションし、古いログは最大30世代まで保持します。
        FileName = fileName,
        ArchiveEvery = FileArchivePeriod.Day,
        ArchiveSuffixFormat = "_{1:yyyyMMdd}_{0:00}",
        MaxArchiveFiles = 30,
        KeepFileOpen = false,
        Encoding = Encoding.UTF8,
        Layout = layout
    };
}
