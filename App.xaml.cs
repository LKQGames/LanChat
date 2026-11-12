using System.Windows;

namespace LanChat;

public partial class App : Application
{
    public App()
    {
        AppLog.Write("程序启动。");
        DispatcherUnhandledException += (_, e) => AppLog.Write($"未处理的 UI 异常：{e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Write($"未处理的运行时异常：{e.ExceptionObject}");
            AppLog.SaveRuntimeLog();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"未观察到的后台任务异常：{e.Exception}");
            e.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.SaveRuntimeLog();
        base.OnExit(e);
    }
}
