using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace maccy.Views;

public partial class MessageWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    private TextBlock? _titleText;
    private TextBlock? _messageText;
    private Button? _okButton;

    public MessageWindow()
    {
        InitializeComponent();

        _titleText = this.FindControl<TextBlock>("TitleText");
        _messageText = this.FindControl<TextBlock>("MessageText");
        _okButton = this.FindControl<Button>("OkButton");

        if (_okButton is not null)
            _okButton.Click += (_, _) => CloseWithResult();

        Closed += (_, _) =>
        {
            if (!_tcs.Task.IsCompleted)
                _tcs.TrySetResult(true);
        };
    }

    public MessageWindow(string title, string message)
        : this()
    {
        Title = title;
        if (_titleText is not null)
            _titleText.Text = title;
        if (_messageText is not null)
            _messageText.Text = message;
    }

    public async Task ShowDialogAsync(Window owner)
    {
        await ShowDialog(owner);
        await _tcs.Task;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void CloseWithResult()
    {
        _tcs.TrySetResult(true);
        Close();
    }
}
