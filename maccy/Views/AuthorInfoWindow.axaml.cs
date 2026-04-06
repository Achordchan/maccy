using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using System.Diagnostics;

namespace maccy.Views;

public partial class AuthorInfoWindow : Window
{
    private static readonly Uri AvatarUri = new("https://avatars.githubusercontent.com/u/179492542?v=4");
    private const string EmailAddress = "achordchan@gmail.com";
    private const string ProjectUrl = "https://gitee.com/Achordchan/maccy";
    private const string PrivacyUrl = "https://gitee.com/Achordchan/maccy/blob/master/docs/privacy.md";
    private const string LicenseUrl = "https://gitee.com/Achordchan/maccy/blob/master/LICENSE";
    private const string SponsorUrl = "https://gitee.com/Achordchan/maccy/blob/master/docs/sponsor.md";

    public AuthorInfoWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await TryLoadAvatarAsync();
        Deactivated += (_, _) => Close();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async Task TryLoadAvatarAsync()
    {
        try
        {
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(AvatarUri);
            await using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            var img = this.FindControl<Image>("AvatarImage");
            if (img is not null)
            {
                img.Source = bmp;
                img.Opacity = 1;
            }
        }
        catch
        {
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void OnProjectClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl(ProjectUrl);
    }

    private void OnEmailClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl($"mailto:{EmailAddress}");
    }

    private void OnPrivacyClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl(PrivacyUrl);
    }

    private void OnLicenseClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl(LicenseUrl);
    }

    private void OnSponsorClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl(SponsorUrl);
    }
}
