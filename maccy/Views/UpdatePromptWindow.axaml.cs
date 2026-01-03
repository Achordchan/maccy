using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using maccy.Services;

namespace maccy.Views;

public enum UpdatePromptResult
{
    UpdateNow,
    Later,
    Exit,
}

public partial class UpdatePromptWindow : Window
{
    private readonly TaskCompletionSource<UpdatePromptResult> _tcs = new();

    private TextBlock? _titleText;
    private TextBlock? _versionText;
    private TextBlock? _notesText;
    private Button? _laterButton;
    private Button? _exitButton;
    private Button? _updateButton;

    public UpdatePromptWindow()
    {
        InitializeComponent();

        _titleText = this.FindControl<TextBlock>("TitleText");
        _versionText = this.FindControl<TextBlock>("VersionText");
        _notesText = this.FindControl<TextBlock>("NotesText");
        _laterButton = this.FindControl<Button>("LaterButton");
        _exitButton = this.FindControl<Button>("ExitButton");
        _updateButton = this.FindControl<Button>("UpdateButton");

        Closed += (_, _) =>
        {
            if (!_tcs.Task.IsCompleted)
                _tcs.TrySetResult(UpdatePromptResult.Exit);
        };
    }

    public UpdatePromptWindow(UpdateInfo info, bool mandatory)
        : this()
    {
        if (_titleText is not null)
            _titleText.Text = mandatory ? "需要更新" : "发现新版本";

        if (_versionText is not null)
            _versionText.Text = "版本：" + info.Version;

        if (_notesText is not null)
            _notesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "（无）" : info.Notes;

        if (_laterButton is not null)
        {
            _laterButton.IsVisible = !mandatory;
            _laterButton.Click += (_, _) => CloseWith(UpdatePromptResult.Later);
        }

        if (_exitButton is not null)
        {
            _exitButton.Content = mandatory ? "退出" : "取消";
            _exitButton.Click += (_, _) => CloseWith(UpdatePromptResult.Exit);
        }

        if (_updateButton is not null)
            _updateButton.Click += (_, _) => CloseWith(UpdatePromptResult.UpdateNow);
    }

    public async Task<UpdatePromptResult> ShowDialogAsync(Window owner)
    {
        await ShowDialog(owner);
        return await _tcs.Task;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void CloseWith(UpdatePromptResult result)
    {
        _tcs.TrySetResult(result);
        Close();
    }
}
