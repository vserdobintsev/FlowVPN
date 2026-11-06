using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
namespace FlowVPN;

internal sealed partial class FluentWindow
{
    readonly record struct PageInfo(int Id, string Element, bool General = false);
    static readonly PageInfo[] Pages = [new(0, "AppsPage"), new(1, "BasePage", true), new(2, "SourcesPage", true), new(3, "LogsPage"), new(4, "ProfileSettingsPage"), new(5, "UpdatesPage", true), new(6, "StartupPage", true), new(7, "AboutPage", true), new(8, "ProfileSetsPage", true)];
    static PageInfo Page(int id) => Pages.First(p => p.Id == id);
    bool navigating;
    void SelectPage(int index)
    {
        if (navigating)
            return;
        navigating = true;
        try
        {
            int previousPage = page;
            if (previousPage != index)
                C<InfoBar>("ProfileSetupFeedback").IsOpen = false;
            page = index;
            var destination = Page(index);
            bool general = destination.General;
            bool catalog = index == 8;
            bool empty = !general && Profile == null;
            C<FrameworkElement>("ConnectionCard").Visibility = general || index == 4 || empty ? Visibility.Collapsed : Visibility.Visible;
            C<FrameworkElement>("SectionNavigation").Visibility = general || empty ? Visibility.Collapsed : Visibility.Visible;
            C<FrameworkElement>("SettingsPage").Visibility = general ? Visibility.Visible : Visibility.Collapsed;
            C<FrameworkElement>("BaseBadge").Visibility = general ? Visibility.Collapsed : Visibility.Visible;
            C<FrameworkElement>("SettingsNavigation").Visibility = catalog ? Visibility.Collapsed : Visibility.Visible;
            C<Button>("SettingsButton").Background = Color(general && !catalog ? (byte)0 : (byte)37, general && !catalog ? (byte)105 : (byte)57, general && !catalog ? (byte)98 : (byte)77);
            C<Button>("ProfileSuggestionsButton").Background = catalog ? Color(0, 105, 98) : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            bool previous = binding;
            binding = true;
            C<ListView>("ProfileList").SelectedItem = general ? null : C<ListView>("ProfileList").Items.OfType<ListViewItem>()
                .FirstOrDefault(i => ((VpnProfile)i.Tag).Id == settings.SelectedProfileId);
            binding = previous;
            if (!general)
                BindProfile();
            foreach (var item in Pages)
                C<FrameworkElement>(item.Element).Visibility = index == item.Id && !empty ? Visibility.Visible : Visibility.Collapsed;
            C<FrameworkElement>("EmptyProfileState").Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            var navigation = C<NavigationView>(general ? "SettingsNavigation" : "SectionNavigation");
            navigation.SelectedItem = catalog ? null : navigation.MenuItems.OfType<NavigationViewItem>().First(item => int.TryParse(item.Tag?.ToString(), out int tag) && tag == index);
            UpdateHeading();
            if (index == 5)
                RefreshUpdates();
            if (index == 6)
                _ = RefreshStartup();
            if (index == 3)
                UpdateLog();
            if (catalog)
                ShowProfileSetsPage();
            if (previousPage != index)
            {
                var target = C<FrameworkElement>(empty ? "EmptyProfileState" : destination.Element);
                motion.Enter(target);
            }
        }
        finally { navigating = false; }
    }
    string ProfileLogFolder()
    {
        string folder = Profile == null ? Path.Combine(Store.Data, "logs") : Store.ProfileLogs(Profile.Id);
        Directory.CreateDirectory(folder);
        return folder;
    }
    void UpdateLog()
    {
        C<TextBlock>("LogScope").Text = "Последние сообщения профиля «" + (Profile?.Name ?? "—") + "».";
        if (Profile == null)
        {
            C<TextBox>("LogText").Text = "Выберите профиль.";
            return;
        }
        string folder = Store.ProfileLogs(Profile.Id), main = Path.Combine(folder, "sing-box.log"), console = Path.Combine(folder, "console.log");
        C<TextBox>("LogText").Text = !File.Exists(main) && !File.Exists(console)
            ? "У этого профиля пока нет записей. Они появятся после подключения."
            : string.Join("\n", Store.Tail(main, 160)) + "\n\nСистемные сообщения:\n" + string.Join("\n", Store.Tail(console, 30));
    }
}
