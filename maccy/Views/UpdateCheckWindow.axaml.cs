using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace maccy.Views;

public partial class UpdateCheckWindow : Window
{
    private TextBlock? _statusText;

    public UpdateCheckWindow()
    {
        InitializeComponent();
        _statusText = this.FindControl<TextBlock>("StatusText");
    }

    public void SetStatus(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_statusText is not null)
                _statusText.Text = text;
        });
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
