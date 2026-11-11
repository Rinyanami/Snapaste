using System.Windows.Threading;
namespace Snapaste.Core;

public static class RenderWorker
{
    public static Task Run(Action action) => Run(() =>
    {
        action();
        return true;
    });

    public static Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                T result;
                try { result = action(); }
                finally
                {
                    try { Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown(); }
                    catch (Exception error) when (error is not OutOfMemoryException) { Log.Error("Render thread cleanup", error); }
                }
                completion.SetResult(result);
            }
            catch (Exception e) { completion.SetException(e); }
        }) { IsBackground = true, Name = "Snapaste image render" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
