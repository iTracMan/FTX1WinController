using System.Windows;
using System.Windows.Threading;

namespace FTX1WinController;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // None of these can save a truly fatal (e.g. native-level) crash,
        // but they turn an ordinary unhandled .NET exception — on the UI
        // thread, a background thread (NAudio's playback/capture threads),
        // or an unawaited Task — from a silent process death with no trace
        // into a logged one, and keep the app alive where it's safe to.
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLogger.Log("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashLogger.Log("AppDomain.UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLogger.Log("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }
}
