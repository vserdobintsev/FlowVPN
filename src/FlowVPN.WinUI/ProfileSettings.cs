using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace FlowVPN;

internal sealed partial class FluentWindow
{
    void AttachProfileActions(ListViewItem item, VpnProfile profile, Grid host)
    {
        var gear = new Button
        {
            Content = new FontIcon { Glyph = "\uE713", FontSize = 14 },
            Width = 28,
            Height = 30,
            MinWidth = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        ToolTipService.SetToolTip(gear, "Название и оформление профиля");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gear, "Изменить профиль «" + profile.Name + "»");
        bool pointerOver = false;
        void UpdateVisibility()
        {
            bool visible = !compact && (pointerOver || item.FocusState == FocusState.Keyboard || gear.FocusState == FocusState.Keyboard);
            gear.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            // Reserve the action slot so hovering never changes text truncation.
            if (host.Children[0] is FrameworkElement normal)
                normal.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 32, 0);
        }
        item.PointerEntered += (_, _) => { pointerOver = true; UpdateVisibility(); };
        item.PointerExited += (_, _) => { pointerOver = false; UpdateVisibility(); };
        item.PointerCanceled += (_, _) => { pointerOver = false; UpdateVisibility(); };
        // Wait for both focus properties to settle when focus moves from the row to its button.
        // Property callbacks also cover a change of input mode on an already focused row.
        item.RegisterPropertyChangedCallback(UIElement.FocusStateProperty, (_, _) => DispatcherQueue.TryEnqueue(UpdateVisibility));
        gear.RegisterPropertyChangedCallback(UIElement.FocusStateProperty, (_, _) => DispatcherQueue.TryEnqueue(UpdateVisibility));
        gear.Click += async (_, _) => { try { await EditProfileIdentity(profile); } catch (Exception e) { await Message(e.Message); } };
        host.SizeChanged += (_, _) => UpdateVisibility();
        host.Children.Add(gear);
        UpdateVisibility();
        item.ContextFlyout = ProfileActions(profile);
    }
    void SelectProfile(VpnProfile profile, int destination = 0)
    {
        bool previous = binding;
        binding = true;
        settings.SelectedProfileId = profile.Id;
        C<ListView>("ProfileList").SelectedItem = C<ListView>("ProfileList").Items.OfType<ListViewItem>().FirstOrDefault(i => ((VpnProfile)i.Tag).Id == profile.Id);
        binding = previous;
        Store.Save(settings);
        BindProfile();
        SelectPage(destination);
        UpdateTray();
    }
    MenuFlyout ProfileActions(VpnProfile profile) => Menu(
        ("Название и оформление…", () => EditProfileIdentity(profile)),
        ("Настройки профиля", () => { SelectProfile(profile, 4); return Task.CompletedTask; }
    ),
        ("Создать копию", () => { DuplicateProfile(profile); return Task.CompletedTask; }
    ),
        ("Удалить профиль", () => DeleteProfile(profile)));
    void DuplicateProfile(VpnProfile profile)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<VpnProfile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += " · копия";
        settings.Profiles.Add(copy);
        settings.SelectedProfileId = copy.Id;
        Save();
        BindAll();
        SelectPage(4);
    }
    async Task DeleteProfile(VpnProfile profile)
    {
        if (engine.Busy)
        {
            await Message("Дождитесь завершения подключения или отключения.");
            return;
        }
        if (engine.ActiveProfileId == profile.Id)
        {
            await Message("Сначала отключите этот профиль.");
            return;
        }
        if (await Dialog("Удалить профиль?", new TextBlock { Text = "«" + profile.Name + "» будет удалён. Общие подключения и база блокировок сохранятся.", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 }, "Удалить") != ContentDialogResult.Primary)
            return;
        if (engine.Busy || engine.ActiveProfileId == profile.Id)
        {
            await Message("Сначала отключите этот профиль.");
            return;
        }
        settings.Profiles.Remove(profile);
        if (settings.SelectedProfileId == profile.Id)
            settings.SelectedProfileId = settings.Profiles.FirstOrDefault()?.Id ?? "";
        Save();
        BindAll();
        SelectPage(0);
    }
    void HookProfileSettings()
    {
        Hook("EditIdentityButton", EditProfileIcon);
        Hook("ProfileSitesButton", EditDomains);
        Hook("ProfileReconnectButton", async () => { await Disconnect(); await Connect(); });
        Hook("ProfileAppsButton", () => { SelectPage(0); return Task.CompletedTask; });
        Hook("DuplicateProfileButton", () => { if (Profile != null) DuplicateProfile(Profile); return Task.CompletedTask; });
        Hook("DeleteProfileButton", () => Profile == null ? Task.CompletedTask : DeleteProfile(Profile));
        C<ComboBox>("ProfileSourceCombo").SelectionChanged += (_, _) =>
        {
            if (binding || Profile == null)
                return;
            Profile.SourceId = (C<ComboBox>("ProfileSourceCombo").SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            Save();
            BindAll();
            SelectPage(4);
        };
        C<ComboBox>("ProfileModeCombo").SelectionChanged += (_, _) =>
        {
            if (binding || Profile == null)
                return;
            Profile.Mode = new[] { "include", "exclude", "all" }[Math.Max(0, C<ComboBox>("ProfileModeCombo").SelectedIndex)];
            Save();
            BindProfile();
        };
    }
    void BindProfileSettings(VpnProfile? profile)
    {
        C<TextBlock>("IdentityName").Text = profile?.Name ?? "Создайте профиль";
        C<Border>("IdentityGlyph").Child = profile == null ? null : ProfileGlyph(profile, false);
        var source = C<ComboBox>("ProfileSourceCombo");
        source.Items.Clear();
        foreach (var item in settings.Sources)
            source.Items.Add(new ComboBoxItem { Content = item.Name + " · " + item.Kind, Tag = item.Id });
        source.SelectedItem = source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == profile?.SourceId);
        C<ComboBox>("ProfileModeCombo").SelectedIndex = profile?.Mode == "all" ? 2 : profile?.Mode == "exclude" ? 1 : 0;
        foreach (string name in new[] { "EditIdentityButton", "ProfileSitesButton", "ProfileAppsButton", "DuplicateProfileButton", "DeleteProfileButton" })
            C<Button>(name).IsEnabled = profile != null;
        C<Button>("ProfileSitesButton").Content = CommandContent("\uE774", "Сайты через VPN" + (profile?.Domains.Count > 0 ? " · " + profile.Domains.Count : ""));
    }
}
