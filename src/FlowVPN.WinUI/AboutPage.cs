using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace FlowVPN;

internal static class DeveloperInfo
{
    public const string Name = "Владислав Сердобинцев";
    public const string GitHub = "https://github.com/vserdobintsev";
    public const string Email = "vladislav.serdobintsev@gmail.com";
}

internal sealed partial class FluentWindow
{
    void HookAbout()
    {
        C<TextBlock>("AboutVersion").Text = "Предварительная версия · " + updateIdentity.Version.ToString(3);
        C<TextBlock>("DeveloperName").Text = DeveloperInfo.Name;
        C<TextBlock>("DeveloperEmail").Text = DeveloperInfo.Email;
        C<HyperlinkButton>("DeveloperGitHub").NavigateUri = new(DeveloperInfo.GitHub);
        C<HyperlinkButton>("DeveloperMail").NavigateUri = new("mailto:" + DeveloperInfo.Email);
        // Initials are already in XAML; local cache and public refresh never delay the first frame.
        _ = LoadDeveloperAvatar();
        Hook("CopyDeveloperEmail", () =>
        {
            var content = new DataPackage();
            content.SetText(DeveloperInfo.Email);
            Clipboard.SetContent(content);
            C<TextBlock>("ContactFeedback").Text = "Адрес скопирован";
            Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(C<TextBlock>("ContactFeedback"))?
                .RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
            return Task.CompletedTask;
        });
    }

    async Task LoadDeveloperAvatar()
    {
        var token = windowLifetime.Token;
        try
        {
            using var client = DeveloperAvatarCache.CreateClient();
            var cache = new DeveloperAvatarCache(Path.Combine(Store.Data, "appearance"), client);
            var cached = await Task.Run(() => cache.ReadAsync(token), token);
            if (cached.Image != null) await ShowDeveloperAvatar(cached.Image, token);
            // Automated UI fixtures must remain deterministic and must not contact GitHub.
            if (Entry.VisualTest || Entry.ReleaseUiTest) return;
            var updated = await cache.RefreshAsync(cached, token);
            if (updated != null) await ShowDeveloperAvatar(updated, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            // Offline/invalid images keep the cached picture or initials; no modal and no retry loop.
        }
    }

    async Task ShowDeveloperAvatar(byte[] bytes, CancellationToken token)
    {
        if (windowClosed || token.IsCancellationRequested) return;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(token);
        }
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = 128, DecodePixelHeight = 128 };
        await bitmap.SetSourceAsync(stream).AsTask(token);
        if (!windowClosed && !token.IsCancellationRequested) C<PersonPicture>("DeveloperAvatar").ProfilePicture = bitmap;
    }
}
