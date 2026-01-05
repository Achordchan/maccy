using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace maccy.Services;

public sealed class ToastService
{
    public static ToastService Instance { get; } = new();

    private int _token;

    public event Action<string?>? ToastChanged;

    private ToastService()
    {
    }

    public void Show(string message, int durationMs = 1100)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var token = Interlocked.Increment(ref _token);

        try
        {
            Dispatcher.UIThread.Post(() => ToastChanged?.Invoke(message));
        }
        catch
        {
        }

        _ = ClearLaterAsync(token, durationMs);
    }

    private async Task ClearLaterAsync(int token, int durationMs)
    {
        try
        {
            await Task.Delay(Math.Max(200, durationMs));

            Dispatcher.UIThread.Post(() =>
            {
                if (token == _token)
                    ToastChanged?.Invoke(null);
            });
        }
        catch
        {
        }
    }
}
