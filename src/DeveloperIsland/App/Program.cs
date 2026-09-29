using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeveloperIsland;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);

        // One island per user session. A second launch (Start menu, installer "Launch") shows the
        // running island instead. Demo mode uses its own instance name so it can run side by side.
        var name = options.IsDemo ? "DeveloperIsland.Demo" : "DeveloperIsland";
        using var mutex = new Mutex(true, $@"Local\{name}.Instance", out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        if (!isFirst)
        {
            showSignal.Set();
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App(options);
            ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => dispatcher.TryEnqueue(() => app.OnSecondInstance()), null, Timeout.Infinite, executeOnlyOnce: false);
        });

        GC.KeepAlive(mutex);
        return 0;
    }
}
