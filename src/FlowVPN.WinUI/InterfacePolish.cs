using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinForms = System.Windows.Forms;
namespace FlowVPN;

internal sealed partial class FluentWindow
{
    WinForms.ToolStripMenuItem? trayToggle, trayStatus;
    System.Drawing.Image? trayConnectImage, trayDisconnectImage;
    readonly List<(VpnProfile Profile, WinForms.ToolStripMenuItem Item)> trayProfiles = [];
    readonly List<System.Drawing.Image> trayImages = [];
    VpnHealth previousHealth = VpnHealth.Stopped;
    static string ActionGlyph(string text) => text switch
    {
        var t when t.Contains("оформление") || t.Contains("цвет") => "\uE771",
        var t when t.Contains("Настройки") => "\uE713",
        var t when t.Contains("Переимен") => "\uE70F",
        var t when t.Contains("копию") => "\uE8C8",
        var t when t.Contains("Удал") || t.Contains("Убрать") => "\uE74D",
        var t when t.Contains("Папк") || t.Contains("папк") => "\uE8B7",
        var t when t.Contains("процесс") => "\uE756",
        var t when t.Contains("импорт", StringComparison.OrdinalIgnoreCase) => "\uE8B5",
        var t when t.Contains("Обнов") => "\uE72C",
        var t when t.Contains("домен", StringComparison.OrdinalIgnoreCase) => "\uE774",
        var t when t.Contains("конфиг") => "\uE73E",
        var t when t.Contains("Outline") => "\uE968",
        _ => "\uE71D"
    };
    void DecorateButtons()
    {
        foreach (var pair in new[] { ("CheckAppUpdateButton", "\uE72C"), ("PrepareUpdateButton", "\uE896"), ("EditIdentityButton", "\uE70F"), ("DuplicateProfileButton", "\uE8C8"), ("DeleteProfileButton", "\uE74D"), ("ProfileAppsButton", "\uE71D"), ("AddApp", "\uE710"), ("DomainsButton", "\uE774"), ("ApplyButton", "\uE72C"), ("UpdateRulesButton", "\uE72C"), ("ImportClientsButton", "\uE8B5"), ("AddConnectionButton", "\uE710"), ("RenameSourceButton", "\uE70F"), ("DeleteSourceButton", "\uE74D"), ("RefreshLogButton", "\uE72C"), ("OpenLogsButton", "\uE8B7"), ("ValidateButton", "\uE73E") })
        {
            var button = C<Button>(pair.Item1);
            if (button.Content is not string label)
                continue;
            label = label.TrimStart('＋', '+', ' ');
            button.Content = CommandContent(pair.Item2, label);
            AutomationProperties.SetName(button, label);
        }
        foreach (var pair in new[] { ("NewProfile", "Новый профиль"), ("ThemeButton", "Сменить тему"), ("SourceCombo", "Подключение выбранного профиля"), ("ModeCombo", "Режим маршрутизации"), ("ProfileList", "Профили"), ("AppList", "Приложения профиля"), ("SourceList", "Источники подключения") })
        {
            AutomationProperties.SetName(C<DependencyObject>(pair.Item1), pair.Item2);
            ToolTipService.SetToolTip(C<DependencyObject>(pair.Item1), pair.Item2);
        }
    }
    static StackPanel CommandContent(string glyph, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = label });
        return panel;
    }
    void UpdateHeading()
    {
        bool general = Page(page).General;
        C<TextBlock>("ScopeCaption").Text = general ? "ОБЩИЕ ДЛЯ ВСЕХ ПРОФИЛЕЙ" : "ВЫБРАННЫЙ ПРОФИЛЬ";
        C<TextBlock>("ProfileHeading").Text = general ? "Настройки приложения" : Profile?.Name ?? "Создайте профиль";
    }
    void SetTrayState()
    {
        var command = TaskbarState.From(Entry.Preview, engine.Running, engine.Busy, Profile is { } profile && settings.Sources.Any(s => s.Id == profile.SourceId));
        taskbar.Update(command);
        string state = Entry.Preview ? "preview" : engine.Busy ? "busy" : engine.Running ? (engine.Healthy ? "connected" : "warning") : "disconnected";
        tray.Icon = TrayArt.Status(state);
        string text = Entry.Preview ? "FlowVPN · предпросмотр" : engine.Busy ? "FlowVPN · подключение" : engine.Running ? "FlowVPN · " + (engine.Healthy ? engine.ActiveProfile : "Нет связи · " + engine.ActiveProfile) : "FlowVPN · отключено";
        tray.Text = text.Length > 63 ? text[..63] : text;
        if (trayToggle != null)
        {
            trayToggle.Enabled = command.Enabled;
            trayToggle.Text = command.Tooltip;
            trayToggle.Image = engine.Running ? trayDisconnectImage : trayConnectImage;
            trayToggle.ToolTipText = engine.Running ? "Активный профиль: " + engine.ActiveProfile : "Выбран: " + (Profile?.Name ?? "нет профиля");
        }
        if (trayStatus != null)
            trayStatus.Text = Entry.Preview ? "Предпросмотр · VPN не запускается" : engine.Running ? (engine.Healthy ? "Подключено: " : "Нет связи: ") + engine.ActiveProfile : "Выбран: " + (Profile?.Name ?? "нет профиля");
        foreach (var (profileItem, item) in trayProfiles)
        {
            item.Checked = profileItem.Id == settings.SelectedProfileId;
            item.Text = profileItem.Name + (engine.ActiveProfileId == profileItem.Id ? " · подключён" : "");
            item.DropDownItems[0].Enabled = !engine.Busy && profileItem.Id != settings.SelectedProfileId;
        }
        if (!Entry.Preview && !engine.Busy && previousHealth != engine.Health)
        {
            if (previousHealth == VpnHealth.Online && engine.Status != "Отключено")
                tray.ShowBalloonTip(4000, "FlowVPN · связь потеряна", engine.Status, WinForms.ToolTipIcon.Warning);
            else if (previousHealth is VpnHealth.Unreachable or VpnHealth.NoNetwork or VpnHealth.NetworkChanged && engine.Healthy)
                tray.ShowBalloonTip(3000, "FlowVPN · связь восстановлена", "Туннель снова проходит проверку.", WinForms.ToolTipIcon.Info);
        }
        if (!engine.Busy)
            previousHealth = engine.Health;
    }
}
internal static class TrayArt
{
    static readonly Dictionary<string, System.Drawing.Icon> states = [];
    static readonly string FontName = ChooseFont();
    static string ChooseFont()
    {
        using var candidate = new System.Drawing.Font("Segoe Fluent Icons", 12);
        return candidate.Name == "Segoe Fluent Icons" ? candidate.Name : "Segoe MDL2 Assets";
    }
    public static System.Drawing.Bitmap Glyph(string glyph, System.Drawing.Color color, int size = 32)
    {
        var bitmap = new System.Drawing.Bitmap(size, size);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Transparent);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new System.Drawing.Font(FontName, size * 22f / 32, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        using var brush = new System.Drawing.SolidBrush(color);
        using var format = new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center };
        graphics.DrawString(glyph, font, brush, new System.Drawing.RectangleF(0, 0, size, size), format);
        return bitmap;
    }
    public static System.Drawing.Icon Status(string state)
    {
        if (states.TryGetValue(state, out var cached))
            return cached;
        using var bitmap = new System.Drawing.Bitmap(64, 64);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0, 128, 116));
            g.FillEllipse(bg, 2, 2, 56, 56);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, 5) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            g.DrawLine(pen, 22, 43, 22, 18);
            g.DrawLine(pen, 22, 18, 40, 18);
            g.DrawLine(pen, 22, 30, 36, 30);
            using var ring = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            g.FillEllipse(ring, 38, 38, 25, 25);
            using var dot = new System.Drawing.SolidBrush(state == "connected" ? System.Drawing.Color.FromArgb(0, 150, 103) : state is "busy" or "warning" ? System.Drawing.Color.FromArgb(220, 145, 24) : System.Drawing.Color.FromArgb(100, 119, 137));
            g.FillEllipse(dot, 40, 40, 21, 21);
            using var mark = new System.Drawing.Pen(System.Drawing.Color.White, 2.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            if (state == "connected")
            {
                g.DrawLine(mark, 45, 50, 49, 54);
                g.DrawLine(mark, 49, 54, 56, 46);
            }
            else if (state == "busy")
                g.DrawArc(mark, 45, 45, 11, 11, 0, 260);
            else if (state == "warning")
            {
                g.DrawLine(mark, 50, 45, 50, 51);
                g.DrawLine(mark, 50, 56, 50, 56.5f);
            }
            else
                g.DrawLine(mark, 45, 51, 56, 51);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
            states[state] = icon;
            return icon;
        }
        finally { Native.DestroyIcon(handle); }
    }
}
