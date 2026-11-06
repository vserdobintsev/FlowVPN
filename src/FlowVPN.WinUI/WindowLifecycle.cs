using Microsoft.UI.Xaml;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    readonly CancellationTokenSource windowLifetime = new();
    bool windowClosed, ticking;

    async void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        Responsive();
        if (Entry.VisualTest || Entry.ReleaseUiTest)
        {
            try
            {
                if (Entry.ReleaseUiTest)
                    await ReleaseUiChecks();
                else
                    await VisualChecks();
            }
            catch (Exception e)
            {
                Entry.VerificationFailed = true;
                File.WriteAllText(Path.Combine(Store.Data, Entry.ReleaseUiTest ? "release-ui-error.txt" : "visual-error.txt"), e.ToString());
            }
            finally { closing = true; Close(); Application.Current.Exit(); }
        }
        else if (settings.Sources.Count == 0)
        {
            SelectPage(2);
            if (!Entry.Startup)
                C<Microsoft.UI.Xaml.Controls.Button>("AddConnectionButton").Focus(FocusState.Programmatic);
        }
    }

    void EngineChanged() => DispatcherQueue.TryEnqueue(() => { if (!windowClosed) UpdateState(); });

    void CloseResources(object sender, WindowEventArgs args)
    {
        windowClosed = true;
        timer.Stop();
        engine.Changed -= EngineChanged;
        engine.Dispose();
        windowLifetime.Cancel();
        motion.Dispose();
        taskbar.Dispose();
        sizing.Dispose();
        updateCancellation?.Cancel();
        groupIcons?.Cancel();
        groupIcons?.Dispose();
        groupIcons = null;
        tray.Visible = false;
        tray.Dispose();
        foreach (var image in trayImages)
            image.Dispose();
        trayImages.Clear();
    }

    async void BackgroundTick(object? sender, object args)
    {
        if (ticking || windowClosed)
            return;
        ticking = true;
        try
        {
            UpdateState();
            await BackgroundAppUpdate();
            if (windowClosed)
                return;
            if (page == 3)
                UpdateLog();
            if (!Entry.Preview && settings.AutoUpdateRules && engine.Running && !engine.Busy && !updating
                && DateTime.Now > nextUpdate && (RuleRepository.Due() || DomainRepository.Due(settings.BaseDomainCategory)))
            {
                nextUpdate = DateTime.Now.AddMinutes(30);
                await UpdateRules(false);
            }
        }
        catch (OperationCanceledException) when (windowClosed) { }
        catch (Exception e)
        {
            // An optional background refresh must not terminate the message loop.
            try
            {
                File.AppendAllText(Path.Combine(Store.Data, "background-errors.log"), DateTimeOffset.Now.ToString("O") + " " + e + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        finally { ticking = false; }
    }
}
