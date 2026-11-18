using System.Threading;
using System.Windows;

namespace LanChat;

public partial class App : Application
{
    private static int _crashRecorded;

    public App()
    {
        AppLog.Write("程序启动。");

        DispatcherUnhandledException += (_, e) =>
        {
            AppLog.Write($"未处理的 UI 异常：{e.Exception.GetType().Name}: {e.Exception.Message}");
            ReportCrash(e.Exception, "UI 线程未处理异常");
            // 记录完成后再按默认行为退出，避免程序停在不可用状态。
            e.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            AppLog.Write($"未处理的运行时异常：{exception?.Message ?? e.ExceptionObject?.ToString() ?? "未知"}");
            ReportCrash(exception, $"非 UI 线程未处理异常（IsTerminating={e.IsTerminating}）");
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.SaveRuntimeLog();

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"未观察到的后台任务异常：{e.Exception}");
            ReportCrash(e.Exception, "未观察到的后台任务异常");
            e.SetObserved();
        };
    }

    /// <summary>
    /// UI 线程异常会再触发一次 AppDomain 处理器，这里只记录第一次，
    /// 避免同一崩溃生成两份报告并把来源标错。
    /// </summary>
    private static void ReportCrash(Exception? exception, string source)
    {
        if (Interlocked.Exchange(ref _crashRecorded, 1) == 0) AppLog.SaveCrashLog(exception, source);
        else AppLog.Write($"崩溃处理补充：{source}");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.SaveRuntimeLog();
        base.OnExit(e);
    }
}
