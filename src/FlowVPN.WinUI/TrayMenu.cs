using WinForms = System.Windows.Forms;
namespace FlowVPN;

internal sealed partial class FluentWindow
{
    void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip { ImageScalingSize = new System.Drawing.Size(20, 20) };
        var images = new List<System.Drawing.Image>();
        System.Drawing.Image Icon(string glyph, int color = -1)
        {
            var image = TrayArt.Glyph(glyph, color < 0 ? System.Drawing.Color.FromArgb(0, 128, 116)
                : System.Drawing.Color.FromArgb(Palette[color].R, Palette[color].G, Palette[color].B));
            images.Add(image);
            return image;
        }
        WinForms.ToolStripMenuItem Add(WinForms.ToolStripItemCollection parent, string label, string glyph, Func<Task> action)
        {
            var item = new WinForms.ToolStripMenuItem(label, Icon(glyph));
            item.Click += (_, _) => DispatcherQueue.TryEnqueue(async () => { try { await action(); } catch (Exception e) { Restore(); await Message(e.Message); } });
            parent.Add(item);
            return item;
        }
        Task OpenProfile(VpnProfile profile, int destination)
        {
            Restore();
            SelectProfile(profile, destination);
            return Task.CompletedTask;
        }
        Add(menu.Items, "Открыть FlowVPN", "\uE8A7", () => { Restore(); return Task.CompletedTask; });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        trayToggle = Add(menu.Items, "Подключить выбранный профиль", "\uE768", async () => { if (engine.Busy) return; if (engine.Running) await Disconnect(); else await Connect(); });
        trayConnectImage = trayToggle.Image;
        trayDisconnectImage = Icon("\uE71A");
        trayStatus = new WinForms.ToolStripMenuItem { Enabled = false };
        menu.Items.Add(trayStatus);
        var profiles = new WinForms.ToolStripMenuItem("Профили", Icon("\uE8FD"));
        trayProfiles.Clear();
        foreach (var profile in settings.Profiles)
        {
            var item = new WinForms.ToolStripMenuItem(profile.Name, Icon(Icons.First(i => i.Key == IconKey(profile)).Glyph, ProfileColor(profile)))
            {
                Checked = profile.Id == settings.SelectedProfileId,
                ToolTipText = "Галочка — выбранный профиль. Выбор не прерывает текущее соединение."
            };
            Add(item.DropDownItems, "Выбрать профиль", "\uE73E", () => { SelectProfile(profile, 0); return Task.CompletedTask; });
            Add(item.DropDownItems, "Журнал", "\uE8FD", () => OpenProfile(profile, 3));
            Add(item.DropDownItems, "Настройки профиля", "\uE713", () => OpenProfile(profile, 4));
            trayProfiles.Add((profile, item));
            profiles.DropDownItems.Add(item);
        }
        menu.Items.Add(profiles);
        var general = new WinForms.ToolStripMenuItem("Настройки приложения", Icon("\uE713"));
        Add(general.DropDownItems, "База блокировок", "\uE73E", () => { Restore(); SelectPage(1); return Task.CompletedTask; });
        Add(general.DropDownItems, "Подключения", "\uE705", () => { Restore(); SelectPage(2); return Task.CompletedTask; });
        Add(general.DropDownItems, "Обновления", "\uE895", () => { Restore(); SelectPage(5); return Task.CompletedTask; });
        Add(general.DropDownItems, "Запуск с Windows", "\uE7F4", () => { Restore(); SelectPage(6); return Task.CompletedTask; });
        menu.Items.Add(general);
        Add(menu.Items, "Сменить тему", "\uE771", () => { Theme(); return Task.CompletedTask; });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        Add(menu.Items, "Завершить работу", "\uE8BB", async () => { await Disconnect(); closing = true; Close(); Microsoft.UI.Xaml.Application.Current.Exit(); });
        var old = tray.ContextMenuStrip;
        tray.ContextMenuStrip = menu;
        old?.Dispose();
        foreach (var image in trayImages)
            image.Dispose();
        trayImages.Clear();
        trayImages.AddRange(images);
        menu.Opening += (_, _) => SetTrayState();
        SetTrayState();
    }
}
