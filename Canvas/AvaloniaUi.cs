using Avalonia.Threading;

namespace Galimapia.Canvas;

static class AvaloniaUi
{
    public static async Task RunAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }
}
