using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace maccy.Views;

public partial class UpdateProgressWindow : Window
{
    private ProgressBar? _progress;
    private TextBlock? _detailText;

    public UpdateProgressWindow()
    {
        InitializeComponent();

        _progress = this.FindControl<ProgressBar>("Progress");
        _detailText = this.FindControl<TextBlock>("DetailText");
    }

    public void UpdateProgress(long readBytes, long? totalBytes)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (totalBytes is not null && totalBytes.Value > 0)
            {
                var percent = Math.Clamp((double)readBytes / totalBytes.Value * 100.0, 0, 100);
                if (_progress is not null)
                    _progress.Value = percent;

                if (_detailText is not null)
                    _detailText.Text = FormatBytes(readBytes) + " / " + FormatBytes(totalBytes.Value);
            }
            else
            {
                if (_progress is not null)
                    _progress.IsIndeterminate = true;

                if (_detailText is not null)
                    _detailText.Text = FormatBytes(readBytes);
            }
        });
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024;
        const double MB = KB * 1024;
        const double GB = MB * 1024;

        if (bytes >= GB)
            return (bytes / GB).ToString("0.00") + " GB";
        if (bytes >= MB)
            return (bytes / MB).ToString("0.00") + " MB";
        if (bytes >= KB)
            return (bytes / KB).ToString("0.00") + " KB";
        return bytes + " B";
    }
}
