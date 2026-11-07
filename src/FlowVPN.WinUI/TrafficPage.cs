using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Windowing;
using System.Diagnostics;
using Windows.Foundation;
using Windows.System;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    readonly TrafficHistory trafficHistory = new();
    readonly DispatcherTimer trafficTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Dictionary<string, TrafficGroupRow> trafficRows = new(StringComparer.OrdinalIgnoreCase);
    TrafficPoint[] trafficPlotPoints = [], pausedTrafficPoints = [];
    CoreTrafficSnapshot? pausedTraffic, observedTraffic;
    long observedTrafficAt;
    DateTimeOffset trafficClockUtc = DateTimeOffset.UtcNow;
    long trafficClockTick = Stopwatch.GetTimestamp();
    CoreSnapshot? pausedConnections, renderedConnections, observedConnections;
    long observedConnectionsAt, observedConnectionsAge, pausedConnectionsAge;
    string pausedControlError = "";
    DateTimeOffset trafficPlotStart, trafficPlotEnd, pausedTrafficEnd;
    int trafficQueued, trafficHoverIndex = -1;
    bool trafficHooked, trafficPaused, trafficClockAnchored;
    bool? trafficPauseRendered;
    readonly record struct TrafficChartKey(string Session, bool Paused, TrafficPoint[]? FrozenPoints, int Minutes, double Width, double Height, long EndSecond);
    TrafficChartKey? renderedTrafficChart;
    DateTimeOffset TrafficNow => trafficClockUtc + Stopwatch.GetElapsedTime(trafficClockTick);
    bool TrafficVisible => trafficHooked && !windowClosed && page == 10 && AppWindow.IsVisible &&
        AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    sealed class TrafficGroupRow
    {
        public required TrafficConnectionGroup Group { get; set; }
        public readonly Expander Control = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        public readonly TextBlock Name = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        public readonly TextBlock Summary = new() { FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap };
        public readonly StackPanel Connections = new() { Spacing = 10 };
    }

    void HookTraffic()
    {
        trafficHooked = true;
        engine.TrafficChanged += TrafficChanged;
        trafficTimer.Tick += (_, _) =>
        {
            if (windowClosed) return;
            CaptureTraffic();
            trafficHistory.Prune(TrafficNow.AddMinutes(-15));
            UpdateTrafficPage();
        };
        trafficTimer.Start();
        C<ComboBox>("TrafficRange").SelectionChanged += (_, _) => RenderTrafficChart();
        C<Button>("TrafficPause").Click += (_, _) =>
        {
            if (trafficPaused) ResumeTraffic();
            else if (engine.Traffic is { } snapshot && engine.ActiveProfileId == Profile?.Id)
            {
                pausedTraffic = snapshot with { SampleAgeMilliseconds = TrafficAge(snapshot), TotalsAgeMilliseconds = TrafficAge(snapshot, totals: true) };
                pausedConnections = engine.Snapshot;
                pausedConnectionsAge = ConnectionAge(pausedConnections);
                pausedControlError = engine.ControlError;
                renderedConnections = null;
                pausedTrafficPoints = trafficHistory.Copy();
                pausedTrafficEnd = TrafficNow;
                trafficPaused = true;
            }
            UpdateTrafficPage();
        };
        C<Button>("TrafficActiveProfileButton").Click += (_, _) =>
        {
            var list = C<ListView>("ProfileList");
            var item = list.Items.OfType<ListViewItem>().FirstOrDefault(i => i.Tag is VpnProfile p && p.Id == engine.ActiveProfileId);
            if (item != null) { list.SelectedItem = item; SelectPage(10); }
        };
        C<Expander>("TrafficDetails").Expanding += (_, _) => UpdateTrafficConnections(force: true);
        C<ComboBox>("TrafficGrouping").SelectionChanged += (_, _) => UpdateTrafficConnections(force: true);
        C<ComboBox>("TrafficSort").SelectionChanged += (_, _) => UpdateTrafficConnections(force: true);
        C<TextBox>("TrafficSearch").TextChanged += (_, _) => UpdateTrafficConnections(force: true);
        C<Grid>("TrafficPlot").SizeChanged += (_, _) => RenderTrafficChart();
        C<Grid>("TrafficDetailsControls").SizeChanged += (_, _) => LayoutTrafficControls();
        var chart = C<ContentControl>("TrafficChart");
        chart.PointerMoved += TrafficPointerMoved;
        chart.PointerExited += (_, _) => HideTrafficCursor();
        chart.LostFocus += (_, _) => HideTrafficCursor();
        chart.KeyDown += TrafficChartKeyDown;
        UpdateTrafficPage();
    }

    void DisposeTraffic()
    {
        engine.TrafficChanged -= TrafficChanged;
        trafficTimer.Stop();
        trafficHooked = false;
        trafficRows.Clear();
        trafficHistory.Clear();
        trafficPlotPoints = pausedTrafficPoints = [];
        pausedTraffic = observedTraffic = null;
        pausedConnections = renderedConnections = observedConnections = null;
        renderedTrafficChart = null;
    }

    void TrafficChanged()
    {
        if (Interlocked.Exchange(ref trafficQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref trafficQueued, 0);
            if (windowClosed || !trafficHooked) return;
            CaptureTraffic();
            if (trafficHistory.Count <= 1 || engine.Traffic == null) UpdateTrafficPage();
        })) Interlocked.Exchange(ref trafficQueued, 0);
    }

    void CaptureTraffic()
    {
        if (!engine.Running)
        {
            if (trafficHistory.SessionId.Length > 0 || observedTraffic != null || trafficPaused)
            {
                ResumeTraffic();
                trafficHistory.Clear();
                observedTraffic = null;
                observedConnections = renderedConnections = null;
                trafficRows.Clear();
                C<StackPanel>("TrafficGroups").Children.Clear();
                trafficPlotPoints = [];
            }
            return;
        }
        if (engine.ActiveProfileId is not { } profile || engine.Traffic is not { } snapshot) return;
        if (!ReferenceEquals(snapshot, observedTraffic))
        {
            observedTraffic = snapshot;
            observedTrafficAt = Stopwatch.GetTimestamp();
        }
        if (trafficHistory.Begin(snapshot.SessionId, profile))
        {
            trafficClockUtc = DateTimeOffset.UtcNow;
            trafficClockTick = Stopwatch.GetTimestamp();
            trafficClockAnchored = false;
            ResumeTraffic();
        }
        if (FreshTraffic(snapshot))
        {
            if (!trafficClockAnchored)
            {
                trafficClockUtc = snapshot.SampledAt.AddMilliseconds(TrafficAge(snapshot));
                trafficClockTick = Stopwatch.GetTimestamp();
                trafficClockAnchored = true;
            }
            trafficHistory.Add(snapshot.Sequence, snapshot.SampledAt, snapshot.UploadedPerSecond, snapshot.DownloadedPerSecond);
        }
        else trafficHistory.Gap(TrafficNow);
    }

    long TrafficAge(CoreTrafficSnapshot snapshot, bool totals = false)
    {
        long age = totals ? snapshot.TotalsAgeMilliseconds : snapshot.SampleAgeMilliseconds;
        if (age < 0) return long.MaxValue;
        if (!ReferenceEquals(snapshot, observedTraffic)) return age;
        return TrafficConnectionGroups.Sum(age, (long)Stopwatch.GetElapsedTime(observedTrafficAt).TotalMilliseconds);
    }

    bool FreshTraffic(CoreTrafficSnapshot snapshot) => snapshot.HasSample && TrafficAge(snapshot) <= 3000 &&
        double.IsFinite(snapshot.UploadedPerSecond) && double.IsFinite(snapshot.DownloadedPerSecond) && snapshot.UploadedPerSecond >= 0 && snapshot.DownloadedPerSecond >= 0;

    void ResumeTraffic()
    {
        trafficPaused = false;
        pausedTraffic = null;
        pausedConnections = null;
        renderedConnections = null;
        pausedTrafficPoints = [];
        renderedTrafficChart = null;
        HideTrafficCursor();
    }

    void UpdateTrafficPage()
    {
        if (!TrafficVisible) return;
        CaptureTraffic();
        bool sameProfile = engine.Running && engine.ActiveProfileId == Profile?.Id;
        C<Border>("TrafficEmpty").Visibility = sameProfile ? Visibility.Collapsed : Visibility.Visible;
        C<StackPanel>("TrafficContent").Visibility = sameProfile ? Visibility.Visible : Visibility.Collapsed;
        C<Button>("TrafficActiveProfileButton").Visibility = engine.Running && !sameProfile ? Visibility.Visible : Visibility.Collapsed;
        if (!sameProfile)
        {
            C<TextBlock>("TrafficEmptyTitle").Text = engine.Running ? "Сейчас подключён другой профиль" : "Трафик появится после подключения";
            C<TextBlock>("TrafficEmptyHint").Text = engine.Running ? $"Откройте профиль «{engine.ActiveProfile}», чтобы увидеть его сеанс." :
                "Подключите этот профиль. График хранит последние 15 минут только в памяти и очищается при новом сеансе.";
            if (!engine.Running)
            {
                ResumeTraffic();
                trafficRows.Clear();
                C<StackPanel>("TrafficGroups").Children.Clear();
                renderedConnections = null;
            }
            return;
        }
        var snapshot = trafficPaused ? pausedTraffic : engine.Traffic;
        bool fresh = snapshot != null && FreshTraffic(snapshot);
        C<TextBlock>("TrafficDownloadSpeed").Text = fresh ? TrafficBytes(snapshot!.DownloadedPerSecond) + "/с" : "—";
        C<TextBlock>("TrafficUploadSpeed").Text = fresh ? TrafficBytes(snapshot!.UploadedPerSecond) + "/с" : "—";
        bool totals = snapshot?.HasTotals == true;
        C<TextBlock>("TrafficDownloadTotal").Text = totals ? TrafficBytes(snapshot!.DownloadedTotal) + " за сеанс" : "Объём сеанса пока недоступен";
        C<TextBlock>("TrafficUploadTotal").Text = totals ? TrafficBytes(snapshot!.UploadedTotal) + " за сеанс" : "Объём сеанса пока недоступен";
        var connections = trafficPaused ? pausedConnections : engine.Snapshot;
        DateTimeOffset now = trafficPaused ? pausedTrafficEnd : TrafficNow;
        TimeSpan elapsed = snapshot != null && now >= snapshot.StartedAt ? now - snapshot.StartedAt : TimeSpan.Zero;
        string duration = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        C<TextBlock>("TrafficSessionInfo").Text = snapshot == null ? "Ожидаем данные движка…" :
            $"Сеанс {duration} · Открытых соединений: {(connections == null ? "—" : (connections.TotalConnectionCount ?? connections.Connections.Count).ToString())}" +
            (totals && TrafficAge(snapshot, totals: true) > 10_000 ? " · Объём ожидает обновления" : "") +
            (connections != null && ConnectionAge(connections) > 10_000 ? " · Список соединений ожидает обновления" : "");
        if (trafficPauseRendered != trafficPaused)
        {
            C<Button>("TrafficPause").Content = CommandContent(trafficPaused ? "\uE768" : "\uE769", trafficPaused ? "Продолжить" : "Пауза просмотра");
            trafficPauseRendered = trafficPaused;
        }
        C<Button>("TrafficPause").IsEnabled = snapshot != null;
        C<TextBlock>("TrafficViewStatus").Text = trafficPaused ? $"Просмотр на {pausedTrafficEnd.ToLocalTime():HH:mm:ss}. Сбор данных продолжается." :
            !fresh ? "Нет свежих измерений. Пропуски остаются разрывами на графике." : "В реальном времени · История только в памяти, до 15 минут.";
        RenderTrafficChart();
        UpdateTrafficConnections();
    }

    static string TrafficBytes(double bytes)
    {
        if (!double.IsFinite(bytes) || bytes < 0) return "—";
        string[] units = ["Б", "КиБ", "МиБ", "ГиБ", "ТиБ", "ПиБ", "ЭиБ"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "N0" : bytes >= 100 ? "N0" : "N1") + " " + units[unit];
    }

    void RenderTrafficChart()
    {
        if (!TrafficVisible || !engine.Running || engine.ActiveProfileId != Profile?.Id) return;
        var canvas = C<Canvas>("TrafficCanvas");
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (width <= 0 || height <= 0) return;
        int minutes = C<ComboBox>("TrafficRange").SelectedIndex switch { 0 => 1, 2 => 15, _ => 5 };
        var end = trafficPaused ? pausedTrafficEnd : TrafficNow;
        var key = new TrafficChartKey(trafficHistory.SessionId, trafficPaused, trafficPaused ? pausedTrafficPoints : null,
            minutes, width, height, end.UtcTicks / TimeSpan.TicksPerSecond);
        if (renderedTrafficChart == key) return;
        renderedTrafficChart = key;
        trafficPlotEnd = end;
        trafficPlotStart = trafficPlotEnd.AddMinutes(-minutes);
        DateTimeOffset? hovered = trafficHoverIndex >= 0 && trafficHoverIndex < trafficPlotPoints.Length ? trafficPlotPoints[trafficHoverIndex].At : null;
        trafficPlotPoints = (trafficPaused ? pausedTrafficPoints : trafficHistory.Copy()).Where(p => p.At >= trafficPlotStart && p.At <= trafficPlotEnd).ToArray();
        double maximum = 1024;
        foreach (var point in trafficPlotPoints)
            if (point.Available) maximum = Math.Max(maximum, Math.Max(point.Uploaded!.Value, point.Downloaded!.Value));
        double scale = maximum <= double.MaxValue / 1.1 ? maximum * 1.1 : maximum;
        C<TextBlock>("TrafficScale").Text = TrafficBytes(scale) + "/с";
        C<TextBlock>("TrafficAxisStart").Text = trafficPlotStart.ToLocalTime().ToString("HH:mm:ss");
        C<TextBlock>("TrafficAxisEnd").Text = trafficPaused ? trafficPlotEnd.ToLocalTime().ToString("HH:mm:ss") : "Сейчас";
        bool hasValues = trafficPlotPoints.Any(p => p.Available);
        C<TextBlock>("TrafficChartHint").Visibility = hasValues ? Visibility.Collapsed : Visibility.Visible;
        C<TextBlock>("TrafficChartHint").Text = trafficHistory.Count == 0 ? "Ожидаем измерения…" : "В этом интервале нет измерений";
        (PathGeometry down, PathGeometry downFill) = TrafficGeometry(trafficPlotPoints, false, width, height, scale);
        (PathGeometry up, PathGeometry upFill) = TrafficGeometry(trafficPlotPoints, true, width, height, scale);
        C<ShapePath>("TrafficDownloadLine").Data = down;
        C<ShapePath>("TrafficDownloadArea").Data = downFill;
        C<ShapePath>("TrafficUploadLine").Data = up;
        C<ShapePath>("TrafficUploadArea").Data = upFill;
        int keep = hovered.HasValue ? Array.FindIndex(trafficPlotPoints, p => p.At == hovered.Value) : -1;
        if (keep >= 0) ShowTrafficCursor(keep); else HideTrafficCursor();
    }

    (PathGeometry Line, PathGeometry Area) TrafficGeometry(TrafficPoint[] points, bool upload, double width, double height, double scale)
    {
        var line = new PathGeometry();
        var area = new PathGeometry();
        var run = new List<Point>();
        TrafficPoint? previous = null;
        void Finish()
        {
            if (run.Count == 0) return;
            var segment = new PolyLineSegment();
            foreach (var point in run.Skip(1)) segment.Points.Add(point);
            var figure = new PathFigure { StartPoint = run[0], IsFilled = false };
            figure.Segments.Add(segment);
            line.Figures.Add(figure);
            var fill = new PathFigure { StartPoint = new(run[0].X, height), IsClosed = true, IsFilled = true };
            var fillSegment = new PolyLineSegment();
            foreach (var point in run) fillSegment.Points.Add(point);
            fillSegment.Points.Add(new(run[^1].X, height));
            fill.Segments.Add(fillSegment);
            area.Figures.Add(fill);
            run.Clear();
        }
        double seconds = (trafficPlotEnd - trafficPlotStart).TotalSeconds;
        foreach (var point in points)
        {
            if (!point.Available) { Finish(); previous = null; continue; }
            if (previous is { } before && !TrafficHistory.Continuous(before, point)) Finish();
            double x = (point.At - trafficPlotStart).TotalSeconds / seconds * width;
            double value = (upload ? point.Uploaded : point.Downloaded)!.Value;
            run.Add(new(x, Math.Clamp(height - value / scale * height, 0, height)));
            previous = point;
        }
        Finish();
        return (line, area);
    }

    void TrafficPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (trafficPlotPoints.Length == 0) return;
        double width = C<Canvas>("TrafficCanvas").ActualWidth;
        if (width <= 0) return;
        double x = args.GetCurrentPoint(C<Grid>("TrafficPlot")).Position.X;
        DateTimeOffset target = trafficPlotStart + TimeSpan.FromTicks((long)((trafficPlotEnd - trafficPlotStart).Ticks * Math.Clamp(x / width, 0, 1)));
        int nearest = 0;
        for (int index = 1; index < trafficPlotPoints.Length; index++)
            if ((trafficPlotPoints[index].At - target).Duration() < (trafficPlotPoints[nearest].At - target).Duration()) nearest = index;
        if ((trafficPlotPoints[nearest].At - target).Duration() > TimeSpan.FromSeconds(3)) { HideTrafficCursor(); return; }
        ShowTrafficCursor(nearest);
    }

    void TrafficChartKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (trafficPlotPoints.Length == 0) return;
        int index = trafficHoverIndex < 0 ? trafficPlotPoints.Length - 1 : trafficHoverIndex;
        index = args.Key switch { VirtualKey.Left => Math.Max(0, index - 1), VirtualKey.Right => Math.Min(trafficPlotPoints.Length - 1, index + 1), VirtualKey.Home => 0, VirtualKey.End => trafficPlotPoints.Length - 1, _ => -1 };
        if (index < 0) return;
        args.Handled = true;
        ShowTrafficCursor(index);
    }

    void ShowTrafficCursor(int index)
    {
        trafficHoverIndex = index;
        var point = trafficPlotPoints[index];
        var canvas = C<Canvas>("TrafficCanvas");
        double x = (point.At - trafficPlotStart).TotalSeconds / (trafficPlotEnd - trafficPlotStart).TotalSeconds * canvas.ActualWidth;
        var cursor = C<Line>("TrafficCursor");
        cursor.X1 = cursor.X2 = x;
        cursor.Y1 = 0;
        cursor.Y2 = canvas.ActualHeight;
        cursor.Visibility = Visibility.Visible;
        string text = point.At.ToLocalTime().ToString("HH:mm:ss") + (point.Available ? $"\n↓ {TrafficBytes(point.Downloaded!.Value)}/с · ↑ {TrafficBytes(point.Uploaded!.Value)}/с" : "\nНет измерения");
        C<TextBlock>("TrafficHoverText").Text = text;
        C<Border>("TrafficHover").Visibility = Visibility.Visible;
        AutomationProperties.SetName(C<ContentControl>("TrafficChart"), "График трафика. " + text.Replace('\n', ' '));
    }

    void HideTrafficCursor()
    {
        trafficHoverIndex = -1;
        C<Line>("TrafficCursor").Visibility = Visibility.Collapsed;
        C<Border>("TrafficHover").Visibility = Visibility.Collapsed;
        AutomationProperties.SetName(C<ContentControl>("TrafficChart"), "График приёма и отправки. Стрелки влево и вправо — значения по секундам; Home и End — края диапазона.");
    }

    void LayoutTrafficControls()
    {
        var grid = C<Grid>("TrafficDetailsControls");
        bool narrow = grid.ActualWidth < 460;
        if (grid.RowDefinitions.Count == 2) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetRow(C<ComboBox>("TrafficSort"), narrow ? 1 : 0);
        Grid.SetColumn(C<ComboBox>("TrafficSort"), narrow ? 0 : 1);
        Grid.SetColumnSpan(C<ComboBox>("TrafficGrouping"), narrow ? 2 : 1);
        Grid.SetColumnSpan(C<ComboBox>("TrafficSort"), narrow ? 2 : 1);
        Grid.SetRow(C<TextBox>("TrafficSearch"), narrow ? 2 : 1);
    }

    void UpdateTrafficConnections(bool force = false)
    {
        if (!TrafficVisible || !engine.Running || engine.ActiveProfileId != Profile?.Id || (!force && !C<Expander>("TrafficDetails").IsExpanded)) return;
        var snapshot = trafficPaused ? pausedConnections : engine.Snapshot;
        bool stale = snapshot != null && ConnectionAge(snapshot) > 10_000;
        bool controlError = (trafficPaused ? pausedControlError : engine.ControlError).Length > 0;
        var coverage = C<InfoBar>("TrafficConnectionsCoverage");
        int total = snapshot?.TotalConnectionCount ?? snapshot?.Connections.Count ?? 0;
        bool truncated = snapshot != null && total > snapshot.Connections.Count;
        coverage.IsOpen = truncated || stale || controlError;
        coverage.Message = string.Join(" ", new[] {
            truncated ? $"Показаны {snapshot!.Connections.Count} из {total} активных соединений. Суммы групп относятся только к показанным." : "",
            stale || controlError ? "Список ожидает обновления. Время последнего снимка указано ниже." : "" }.Where(s => s.Length > 0));
        if (!force && ReferenceEquals(snapshot, renderedConnections)) return;
        renderedConnections = snapshot;
        var grouping = (TrafficGrouping)Math.Max(0, C<ComboBox>("TrafficGrouping").SelectedIndex);
        var sort = (TrafficSort)Math.Max(0, C<ComboBox>("TrafficSort").SelectedIndex);
        var groups = TrafficConnectionGroups.Build(snapshot?.Connections ?? [], grouping, sort, C<TextBox>("TrafficSearch").Text.Trim());
        C<TextBlock>("TrafficDetailsHint").Text = trafficPaused ? $"Соединения на {pausedTrafficEnd.ToLocalTime():HH:mm:ss}. Просмотр приостановлен; это не история закрытых соединений." :
            "Только открытые соединения, обновление примерно раз в 4 секунды. Их объём не равен полному объёму сеанса.";
        C<TextBlock>("TrafficConnectionsSummary").Text = snapshot == null ? "Ожидаем список соединений…" :
            $"Показано соединений: {groups.Sum(g => g.Connections.Count)} из {snapshot.Connections.Count} · Снимок {snapshot.CapturedAt.ToLocalTime():HH:mm:ss}";
        C<TextBlock>("TrafficConnectionsEmpty").Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        C<TextBlock>("TrafficConnectionsEmpty").Text = C<TextBox>("TrafficSearch").Text.Length > 0 ? "Совпадений не найдено." : "Сейчас нет активных соединений.";
        var panel = C<StackPanel>("TrafficGroups");
        var keys = groups.Select(g => grouping + ":" + g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string key in trafficRows.Keys.Where(k => !keys.Contains(k)).ToArray())
        {
            panel.Children.Remove(trafficRows[key].Control);
            trafficRows.Remove(key);
        }
        for (int index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            string key = grouping + ":" + group.Key;
            if (!trafficRows.TryGetValue(key, out var row))
            {
                row = new() { Group = group };
                var header = new StackPanel { Spacing = 4 };
                header.Children.Add(row.Name);
                header.Children.Add(row.Summary);
                row.Control.Header = header;
                row.Control.Content = row.Connections;
                var captured = row;
                row.Control.Expanding += (_, _) => FillTrafficGroup(captured, grouping);
                trafficRows.Add(key, row);
            }
            row.Group = group;
            row.Name.Text = group.Name;
            row.Summary.Text = $"Соединений: {group.Connections.Count} · ↓ {TrafficBytes(group.Downloaded)} · ↑ {TrafficBytes(group.Uploaded)}";
            ToolTipService.SetToolTip(row.Control, group.Detail.Length > 0 ? group.Detail : group.Name);
            if (row.Control.IsExpanded) FillTrafficGroup(row, grouping);
            if (index >= panel.Children.Count || !ReferenceEquals(panel.Children[index], row.Control))
            {
                panel.Children.Remove(row.Control);
                panel.Children.Insert(index, row.Control);
            }
        }
    }

    long ConnectionAge(CoreSnapshot? snapshot)
    {
        if (snapshot == null) return long.MaxValue;
        if (trafficPaused && ReferenceEquals(snapshot, pausedConnections)) return pausedConnectionsAge;
        if (!ReferenceEquals(snapshot, observedConnections))
        {
            observedConnections = snapshot;
            observedConnectionsAt = Stopwatch.GetTimestamp();
            observedConnectionsAge = Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.CapturedAt).TotalMilliseconds);
        }
        return TrafficConnectionGroups.Sum(observedConnectionsAge, (long)Stopwatch.GetElapsedTime(observedConnectionsAt).TotalMilliseconds);
    }

    static void FillTrafficGroup(TrafficGroupRow row, TrafficGrouping grouping)
    {
        row.Connections.Children.Clear();
        foreach (var connection in row.Group.Connections)
        {
            var item = new StackPanel { Spacing = 3 };
            string name = grouping == TrafficGrouping.Application ? TrafficConnectionGroups.Address(connection) : TrafficConnectionGroups.Application(connection);
            item.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap });
            string route = connection.Route switch { "vpn" => "VPN", "direct" => "Напрямую", _ => connection.Route };
            string ip = connection.DestinationIp.Length > 0 && connection.Host.Length > 0 ? connection.DestinationIp + " · " : "";
            item.Children.Add(new TextBlock { Text = $"{ip}{route} · ↓ {TrafficBytes(connection.Downloaded)} · ↑ {TrafficBytes(connection.Uploaded)}", FontSize = 12, Opacity = .75, TextWrapping = TextWrapping.Wrap });
            ToolTipService.SetToolTip(item, connection.ProcessPath + "\n" + TrafficConnectionGroups.Address(connection) + "\n" + connection.DestinationIp);
            row.Connections.Children.Add(item);
        }
    }
}
