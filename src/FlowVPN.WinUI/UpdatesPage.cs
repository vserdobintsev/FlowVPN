using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace FlowVPN;

internal sealed partial class FluentWindow
{
    UpdateChannels updateChannels = new();
    readonly UpdateIdentity updateIdentity = UpdateProtocol.Identity();
    UpdateRelease? offeredRelease;
    bool checkingUpdate, stagingUpdate;
    CancellationTokenSource? updateCancellation;
    DateTimeOffset nextAppUpdateCheck = DateTimeOffset.MinValue;
    string updateStatus = "Канал обновлений ещё не настроен.", installedCore = "Проверяем версию…";
    UpdateChannel? Channel => C<ComboBox>("UpdateChannelCombo").SelectedItem as UpdateChannel;
    void HookUpdates()
    {
        settings.Updates ??= new();
        try
        {
            updateChannels = UpdateChannels.Load();
        }
        catch (Exception e) { updateStatus = e.Message; }
        if (Version.TryParse(settings.Updates.PendingVersion, out var pending) && pending <= updateIdentity.Version)
            settings.Updates.PendingVersion = "";
        C<ComboBox>("UpdateChannelCombo").ItemsSource = updateChannels.Channels;
        C<ComboBox>("UpdateChannelCombo").SelectedItem = updateChannels.Channels.FirstOrDefault(c => c.Id == settings.Updates.Channel) ?? updateChannels.Channels.FirstOrDefault();
        C<ToggleSwitch>("CheckUpdatesSwitch").IsOn = settings.Updates.CheckWhileOpen;
        C<ComboBox>("UpdateChannelCombo").SelectionChanged += (_, _) =>
        {
            if (Channel == null)
                return;
            settings.Updates.Channel = Channel.Id;
            offeredRelease = null;
            nextAppUpdateCheck = DateTimeOffset.MinValue;
            updateStatus = Channel.FeedUrl.Length == 0 ? "Канал обновлений ещё не настроен." : "Можно проверить наличие обновлений.";
            Store.Save(settings);
            RefreshUpdates();
        };
        C<ToggleSwitch>("CheckUpdatesSwitch").Toggled += (_, _) => { settings.Updates.CheckWhileOpen = C<ToggleSwitch>("CheckUpdatesSwitch").IsOn; Store.Save(settings); };
        Hook("CheckAppUpdateButton", () => CheckAppUpdate(true));
        Hook("PrepareUpdateButton", PrepareUpdate);
        Hook("CancelAppUpdateButton", () => { updateCancellation?.Cancel(); return Task.CompletedTask; });
        Hook("FinishUpdateButton", FinishUpdate);
        RefreshUpdates();
        _ = ReadInstalledCore();
    }
    async Task ReadInstalledCore()
    {
        try
        {
            using var verifiedCore = await Task.Run(CoreIntegrity.Acquire, windowLifetime.Token);
            var start = new ProcessStartInfo(Store.Bin) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("version");
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                string output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                await stderr;
                installedCore = Regex.Match(output, @"sing-box version ([^\s]+)").Groups[1].Value;
                if (installedCore.Length == 0)
                    installedCore = "Не удалось определить";
            }
            catch { if (!process.HasExited) process.Kill(); throw; }
        }
        catch { installedCore = "Движок недоступен"; }
        if (windowClosed)
            return;
        RefreshUpdates();
        C<TextBlock>("EngineVersionLabel").Text = "sing-box " + installedCore + " · WinUI 3";
    }
    void RefreshUpdates()
    {
        if (windowClosed || root.FindName("UpdateStatus") == null)
            return;
        bool busy = checkingUpdate || stagingUpdate, configured = Channel?.FeedUrl.Length > 0;
        C<TextBlock>("InstalledAppVersion").Text = updateIdentity.Version + (UpdateProtocol.IsPackaged ? " · MSIX" : " · переносимая сборка");
        string expectedCore = UpdateProtocol.ExpectedCoreVersion();
        C<TextBlock>("InstalledCoreVersion").Text = installedCore == expectedCore
            ? installedCore + " · совместимость проверена"
            : installedCore + " · для этой сборки проверена " + expectedCore;
        C<TextBlock>("UpdateStatus").Text = updateStatus;
        C<TextBlock>("LastUpdateCheck").Text = settings.Updates.LastCheckedUtc is DateTimeOffset at
            ? "Последняя успешная проверка: " + at.ToLocalTime().ToString("g") + " · " + (settings.Updates.LastCheckedChannel == "stable" ? "стабильный" : "предварительный") : "Проверок ещё не было";
        bool differentIdentity = Channel != null && Channel.PackageName != updateIdentity.Name;
        C<TextBlock>("UpdateSourceText").Text = differentIdentity ? "Этот канал устанавливается отдельно: у него другой пакет и свои настройки." : configured ? new Uri(Channel!.FeedUrl).Host : "Адрес публикации пока не задан";
        C<TextBlock>("UpdateReleaseNotes").Text = offeredRelease?.Notes ?? "Новые версии FlowVPN поставляются вместе с проверенным sing-box. База блокировок обновляется отдельно.";
        C<TextBlock>("AvailableVersion").Text = offeredRelease == null ? "" : $"FlowVPN {offeredRelease.Version} · sing-box {offeredRelease.CoreVersion}";
        C<ProgressRing>("AppUpdateProgress").IsActive = busy;
        C<ProgressRing>("AppUpdateProgress").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        C<ComboBox>("UpdateChannelCombo").IsEnabled = !busy;
        C<Button>("CheckAppUpdateButton").IsEnabled = configured && !busy && !differentIdentity;
        C<Button>("CancelAppUpdateButton").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        bool canStage = UpdateProtocol.CanStage(Entry.Preview, UpdateProtocol.IsPackaged, engine.Busy || busy, updateIdentity, offeredRelease);
        C<Button>("PrepareUpdateButton").IsEnabled = canStage;
        C<Button>("PrepareUpdateButton").Visibility = offeredRelease != null && UpdateProtocol.Version4(offeredRelease.Version) > updateIdentity.Version ? Visibility.Visible : Visibility.Collapsed;
        bool pending = Version.TryParse(settings.Updates.PendingVersion, out var version) && version > updateIdentity.Version;
        C<Button>("FinishUpdateButton").Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        C<Button>("FinishUpdateButton").IsEnabled = !Entry.Preview && !engine.Busy && !busy;
        C<Button>("FinishUpdateButton").Content = CommandContent("\uE8BB", engine.Running ? "Отключить VPN и завершить" : "Завершить для обновления");
        C<InfoBar>("UpdateModeInfo").Message = Entry.Preview ? "В предпросмотре можно проверять версии. Установка обновлений отключена."
            : !UpdateProtocol.IsPackaged ? "Установку обновлений через Windows поддерживает версия MSIX. Эта сборка переносимая."
            : "Обновление подготовится без закрытия FlowVPN. Применение — при следующем запуске после завершения приложения.";
    }
    async Task CheckAppUpdate(bool manual)
    {
        var channel = Channel;
        if (checkingUpdate || stagingUpdate || channel == null || channel.FeedUrl.Length == 0 || channel.PackageName != updateIdentity.Name)
            return;
        checkingUpdate = true;
        offeredRelease = null;
        updateStatus = "Проверяем обновления…";
        RefreshUpdates();
        updateCancellation = new();
        nextAppUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(30);
        try
        {
            using var client = UpdateTransport.Client(engine.Running ? engine.Proxy : null);
            string json = await UpdateTransport.Feed(client, channel.FeedUrl, updateCancellation.Token);
            var release = UpdateProtocol.Parse(json, channel, updateIdentity, Environment.OSVersion.Version.Build);
            offeredRelease = release;
            settings.Updates.LastCheckedUtc = DateTimeOffset.UtcNow;
            settings.Updates.LastCheckedChannel = channel.Id;
            nextAppUpdateCheck = DateTimeOffset.UtcNow.AddHours(8);
            Store.Save(settings);
            updateStatus = UpdateProtocol.Version4(release.Version) > updateIdentity.Version ? "Доступна новая версия." : "У вас актуальная версия для этого канала.";
        }
        catch (OperationCanceledException) { updateStatus = "Проверка отменена или сервер не ответил вовремя."; }
        catch (Exception e) { updateStatus = "Не удалось проверить обновления. " + e.Message; }
        finally { checkingUpdate = false; updateCancellation.Dispose(); updateCancellation = null; RefreshUpdates(); }
    }
    async Task BackgroundAppUpdate()
    {
        if (Entry.VisualTest || !settings.Updates.CheckWhileOpen || DateTimeOffset.UtcNow < nextAppUpdateCheck || engine.Busy)
            return;
        if (settings.Updates.LastCheckedUtc is DateTimeOffset at && settings.Updates.LastCheckedChannel == Channel?.Id && DateTimeOffset.UtcNow - at < TimeSpan.FromHours(8))
            return;
        await CheckAppUpdate(false);
    }
    async Task PrepareUpdate()
    {
        var release = offeredRelease;
        if (!UpdateProtocol.CanStage(Entry.Preview, UpdateProtocol.IsPackaged, engine.Busy || checkingUpdate || stagingUpdate, updateIdentity, release))
            return;
        stagingUpdate = true;
        updateCancellation = new();
        RefreshUpdates();
        try
        {
            using var client = UpdateTransport.Client(engine.Running ? engine.Proxy : null);
            bool downloading = true;
            var progress = new Progress<int>(n => { if (downloading && stagingUpdate) { updateStatus = $"Загрузка обновления: {n}%"; RefreshUpdates(); } });
            string package = await UpdateTransport.Download(client, release!, progress, updateCancellation.Token);
            downloading = false;
            updateCancellation.Token.ThrowIfCancellationRequested();
            if (engine.Busy)
                throw new InvalidOperationException("Дождитесь завершения операции с VPN и повторите подготовку обновления.");
            UpdateTransport.BackupSettings(updateIdentity.Version.ToString());
            updateStatus = "Windows проверяет подпись и готовит обновление…";
            RefreshUpdates();
            var options = new Windows.Management.Deployment.AddPackageOptions
            {
                DeferRegistrationWhenPackagesAreInUse = true,
                ForceAppShutdown = false,
                ForceTargetAppShutdown = false,
                ForceUpdateFromAnyVersion = false,
                AllowUnsigned = false
            };
            var result = await new Windows.Management.Deployment.PackageManager().AddPackageByUriAsync(new Uri(package), options).AsTask(updateCancellation.Token);
            if (result.ExtendedErrorCode != null && result.ExtendedErrorCode.HResult < 0)
                throw new InvalidOperationException(result.ErrorText);
            settings.Updates.PendingVersion = release!.Version;
            Store.Save(settings);
            updateStatus = "Обновление подготовлено. Завершите FlowVPN и откройте его снова из меню «Пуск».";
        }
        catch (OperationCanceledException) { updateStatus = "Подготовка отменена. Текущая версия продолжает работать."; }
        catch (Exception e) { updateStatus = "Не удалось подготовить обновление. " + e.Message; }
        finally { stagingUpdate = false; updateCancellation.Dispose(); updateCancellation = null; RefreshUpdates(); }
    }
    async Task FinishUpdate()
    {
        if (Entry.Preview || !UpdateProtocol.IsPackaged || stagingUpdate || checkingUpdate || engine.Busy || !Version.TryParse(settings.Updates.PendingVersion, out var version) || version <= updateIdentity.Version)
            return;
        await Disconnect();
        closing = true;
        Close();
        Application.Current.Exit();
    }
}
