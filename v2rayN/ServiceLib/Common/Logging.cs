using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace ServiceLib.Common;

public class Logging
{
    private static readonly Logger _logger1 = LogManager.GetLogger("Log1");
    private static readonly Logger _logger2 = LogManager.GetLogger("Log2");

    private static readonly Logger _connections = LogManager.GetLogger("Connections");

    public static void SaveConnectionLog(string line)
    {
        if (LogManager.IsLoggingEnabled()) _connections.Info(line);
    }

    public static void Setup()
    {
        LoggingConfiguration config = new();
        FileTarget fileTarget = new();
        config.AddTarget("file", fileTarget);
        fileTarget.Layout = "${longdate}-${level:uppercase=true} ${message}";
        fileTarget.FileName = Utils.GetLogPath("${shortdate}.txt");
        var connectionFile = new FileTarget("connectionFile")
        {
            FileName = Utils.GetLogPath("connections.txt"),
            Layout = "${longdate} ${message}",
            ArchiveAboveSize = 10 * 1024 * 1024,
            MaxArchiveFiles = 3,
            ArchiveSuffixFormat = ".{0:000}"
        };
        var connectionTarget = new AsyncTargetWrapper(connectionFile, 2048, AsyncTargetWrapperOverflowAction.Discard) { Name = "connectionsAsync" };
        config.AddTarget(connectionTarget);
        config.LoggingRules.Add(new LoggingRule("Connections", LogLevel.Info, connectionTarget) { Final = true });
        config.LoggingRules.Add(new LoggingRule("*", LogLevel.Debug, fileTarget));
        LogManager.Configuration = config;
    }

    public static void LoggingEnabled(bool enable)
    {
        if (!enable)
        {
            LogManager.SuspendLogging();
        }
    }

    public static void SaveLog(string strContent)
    {
        if (!LogManager.IsLoggingEnabled())
        {
            return;
        }

        _logger1.Info(strContent);
    }

    public static void SaveLog(string strTitle, Exception ex)
    {
        if (!LogManager.IsLoggingEnabled())
        {
            return;
        }

        _logger2.Debug($"{strTitle},{ex.Message}");
        _logger2.Debug(ex.StackTrace);
        if (ex?.InnerException != null)
        {
            _logger2.Error(ex.InnerException);
        }
    }
}
