using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task ShowCoreConnections()
    {
        if (dialogOpen)
            return;
        string? profileId = engine.ActiveProfileId;
        var status = new TextBlock { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        var search = new TextBox { PlaceholderText = "Сайт, приложение или маршрут" };
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = Math.Clamp(root.ActualHeight - 370, 160, 340) };
        var close = new Button { Content = CommandContent("\uE711", "Закрыть соединение"), IsEnabled = false };
        var error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Width = Math.Min(480, Math.Max(320, root.ActualWidth - 120)), Spacing = 12 };
        panel.Children.Add(status);
        panel.Children.Add(search);
        panel.Children.Add(list);
        panel.Children.Add(close);
        panel.Children.Add(error);
        bool operating = false, closed = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        string Bytes(long n) => n < 1024 ? n + " Б" : n < 1024 * 1024 ? (n / 1024d).ToString("N1") + " КБ" : (n / (1024d * 1024)).ToString("N1") + " МБ";
        void Fill()
        {
            if (closed)
                return;
            var snapshot = Entry.VisualTest ? new CoreSnapshot("sing-box 1.14.4", 148000, 2340000,
                [new(Guid.Empty.ToString(), "example.com", @"C:\Apps\Browser\browser.exe", "vpn", 1024, 45000)], DateTimeOffset.Now) : engine.Snapshot;
            if (!Entry.VisualTest && profileId != engine.ActiveProfileId)
                snapshot = null;
            string? selected = ((list.SelectedItem as ListViewItem)?.Tag as CoreConnection)?.Id;
            list.Items.Clear();
            status.Text = snapshot == null ? engine.ControlError.Length > 0 ? engine.ControlError : "Ожидаем ответ движка…"
                : $"Соединений: {snapshot.Connections.Count} · ↑ {Bytes(snapshot.Uploaded)} · ↓ {Bytes(snapshot.Downloaded)}\nСтатистика всего трафика, обработанного sing-box в этом сеансе.";
            foreach (var connection in snapshot?.Connections.Where(c => (c.Host + " " + c.ProcessPath + " " + c.Route).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).Take(300) ?? [])
            {
                var item = Card(connection.Host.Length > 0 ? connection.Host : "Соединение по IP", Path.GetFileName(connection.ProcessPath) + " · " + connection.Route + $" · ↓ {Bytes(connection.Downloaded)}", "\uE774", connection);
                ToolTipService.SetToolTip(item, connection.ProcessPath + "\n" + connection.Route);
                list.Items.Add(item);
                if (connection.Id == selected)
                    list.SelectedItem = item;
            }
            close.IsEnabled = !Entry.Preview && !operating && list.SelectedItem != null;
        }
        list.SelectionChanged += (_, _) => close.IsEnabled = !Entry.Preview && !operating && list.SelectedItem != null;
        search.TextChanged += (_, _) => Fill();
        timer.Tick += (_, _) => { if (!operating) Fill(); };
        close.Click += async (_, _) =>
        {
            if ((list.SelectedItem as ListViewItem)?.Tag is not CoreConnection connection || operating || engine.ActiveProfileId != profileId)
                return;
            operating = true;
            close.IsEnabled = false;
            try
            {
                await engine.CloseConnection(connection.Id, lifetime.Token);
                if (!closed)
                    error.Text = "Соединение закрыто. Приложение может открыть его заново.";
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception e) { if (!closed) error.Text = e.Message; }
            finally { operating = false; Fill(); }
        };
        Fill();
        timer.Start();
        try
        {
            await Dialog("Соединения · " + (engine.ActiveProfile ?? "предпросмотр"), panel, "Готово", false);
        }
        finally { closed = true; timer.Stop(); lifetime.Cancel(); }
    }
}
