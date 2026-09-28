using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using ExplorerTabUtility.App.Services;
using ExplorerTabUtility.Helpers;
using ExplorerTabUtility.Models;

namespace ExplorerTabUtility.App.Pages;

/// <summary>
/// About page. Static content plus the supporter list, which is fetched from the sponsors SVG.
/// <para>
/// The WPF build's ~500 lines of looping animations are gone in favour of official text styles,
/// official buttons and a plain image.
/// </para>
/// </summary>
public sealed partial class AboutPage : Page
{
    private const string RepositoryUrl = "https://github.com/saillill/ExplorerTabUtility-WinUI3";

    public AboutPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Localize();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            AppIcon.Source = new BitmapImage(new Uri(App.IconPath));
        }
        catch
        {
            // Icon is decorative; a missing file must not break the page.
        }

        // The WPF build fetched supporters here; the WinUI port never did, so the list stayed empty
        // and the page always fell back to the "be the first" placeholder.
        _ = LoadSupportersAsync();

        // Same fetch-and-decode path as the supporters: handed to BitmapImage.UriSource, a github
        // avatar URL hangs forever in this app (no ImageOpened, no ImageFailed), so it never showed.
        _ = LoadDeveloperAvatarAsync();
    }

    private void Localize()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(3, 0, 0);

        AppTitleText.Text = LocalizationService.Get("AppTitle");
        VersionText.Text = $"{LocalizationService.Get("Version")} {version.ToString(3)}";
        DescriptionText.Text = LocalizationService.Get("AppDescription");

        GitHubButtonText.Text = LocalizationService.Get("StarOnGitHub");
        SupportTitle.Text = LocalizationService.Get("SupportProject");
        SupportDescriptionText.Text = LocalizationService.Get("SupportDescription");
        DeveloperTitle.Text = LocalizationService.Get("Developer");
        SupportersTitle.Text = LocalizationService.Get("CurrentSupporters");
        SupportersDescriptionText.Text = LocalizationService.Get("SupportersThanks");
        SupportersEmptyText.Text = LocalizationService.Get("BeFirst");

        LnkGitHubSponsors.Content = "GitHub Sponsors";
    }

    private void OnGitHubClick(object sender, RoutedEventArgs e) => OpenUrl(RepositoryUrl);

    private void OnSponsorsClick(object sender, RoutedEventArgs e)
        => OpenUrl("https://github.com/sponsors/w4po");

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // No default browser / blocked by policy — nothing useful to do.
        }
    }

    private async Task LoadSupportersAsync()
    {
        var supporters = await Helper.GetSupporters();

        var items = supporters
            .Select(s => new SupporterView(s.Name, s.ProfileUrl, s.ImageUrl))
            .ToList();

        SupportersList.ItemsSource = items;

        // Only show the placeholder when there really is nobody.
        SupportersEmptyText.Visibility =
            items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        await Task.WhenAll(items.Select(LoadAvatarAsync));
        StartupLog.Step($"About: supporters={items.Count} avatars={items.Count(i => i.Avatar != null)}");
    }

    /// <summary>
    /// Fills in the avatar, trying the most reliably decodable source first.
    /// <para>
    /// The sponsors SVG embeds avatars as <c>data:image/webp;base64,…</c>. WinUI's
    /// <c>BitmapImage</c> rejects the <c>data:</c> scheme, and even decoded to a stream the bytes
    /// are WebP, which needs a WIC codec that is often absent — hence the failures.
    /// GitHub serves a plain JPEG/PNG avatar per user, which always decodes, so it is tried first.
    /// </para>
    /// <para>
    /// When nothing decodes, <c>Avatar</c> stays null and <c>PersonPicture</c> renders the name's
    /// initials instead of an empty box.
    /// </para>
    /// </summary>
    private async Task LoadDeveloperAvatarAsync()
        => DeveloperAvatar.Source = await TryLoadImageAsync(
            "https://avatars.githubusercontent.com/u/6666887?s=96");

    private static async Task LoadAvatarAsync(SupporterView view)
    {
        if (TryGitHubAvatar(view.ProfileUrl, out var github))
        {
            var loaded = await TryLoadImageAsync(github);
            if (loaded != null)
            {
                view.Avatar = loaded;
                return;
            }
        }

        // Fall back to the picture embedded in the SVG. It is WebP, so this only helps when the
        // machine has a WebP codec AND the decode yields real pixels.
        if (!string.IsNullOrWhiteSpace(view.ImageUrl))
            view.Avatar = await TryLoadImageAsync(view.ImageUrl);
    }

    /// <summary>Derives the GitHub avatar URL (a decodable JPEG/PNG) from a github.com profile link.</summary>
    private static bool TryGitHubAvatar(string? profileUrl, out string url)
    {
        url = string.Empty;

        if (string.IsNullOrWhiteSpace(profileUrl)) return false;
        if (!Uri.TryCreate(profileUrl, UriKind.Absolute, out var uri)) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;

        var user = uri.AbsolutePath.Trim('/');
        if (user.Length == 0 || user.Contains('/')) return false;

        url = $"https://github.com/{user}.png";
        return true;
    }

    /// <summary>
    /// Decodes an avatar from bytes, never from a remote URI handed to <c>BitmapImage</c>.
    /// <para>
    /// <c>BitmapImage.UriSource</c> downloads through the platform's image stack, which in this app
    /// never resolves for github.com — it neither opens nor raises <c>ImageFailed</c>, it just hangs.
    /// Fetching with <see cref="HttpClient"/> works (that is how the sponsors SVG is read), so the
    /// bytes are downloaded first and decoded via <see cref="BitmapImage.SetSourceAsync(IRandomAccessStream)"/>.
    /// </para>
    /// </summary>
    private static async Task<ImageSource?> TryLoadImageAsync(string url)
    {
        try
        {
            byte[] bytes;

            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                if (comma < 0) return null;

                bytes = Convert.FromBase64String(url[(comma + 1)..]);
            }
            else
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                bytes = await client.GetByteArrayAsync(url);
            }

            var bitmap = new BitmapImage();

            using var stream = new InMemoryRandomAccessStream();

            // DetachStream is essential: disposing a DataWriter also disposes the stream it wraps,
            // which then makes Seek/SetSourceAsync throw ObjectDisposedException.
            var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();

            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);

            // SetSourceAsync can complete without throwing yet yield an empty bitmap — which is what
            // the embedded WebP did here. PersonPicture then painted its generic placeholder, so the
            // entry looked like a missing avatar. Treat "no pixels" as a failure and fall back.
            return bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 ? bitmap : null;
        }
        catch (Exception ex)
        {
            // Unreachable host, or a format no codec can read (WebP without the codec).
            if (!_avatarErrorLogged)
            {
                _avatarErrorLogged = true;
                StartupLog.Step($"About: avatar error [{url[..Math.Min(48, url.Length)]}] {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }
    }

    private static bool _avatarErrorLogged;

    /// <summary>
    /// One supporter row. Raises property changes because the avatar arrives after the list is
    /// already bound.
    /// </summary>
    private sealed class SupporterView : INotifyPropertyChanged
    {
        private ImageSource? _avatar;

        public SupporterView(string name, string? profileUrl, string? imageUrl)
        {
            Name = name;
            ProfileUrl = profileUrl;
            ImageUrl = imageUrl;
        }

        public string Name { get; }
        public string? ProfileUrl { get; }
        public string? ImageUrl { get; }

        public ImageSource? Avatar
        {
            get => _avatar;
            set
            {
                _avatar = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
