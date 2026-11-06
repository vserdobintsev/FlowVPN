using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

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
}
