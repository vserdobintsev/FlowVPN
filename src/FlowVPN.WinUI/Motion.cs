using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace FlowVPN;

// Controls retain their native templates (dialog show/hide, hover, pressed, disabled, expanded).
// Only app-level navigation adds motion; it never blocks interaction or animates layout dimensions.
internal sealed class Motion : IDisposable
{
    readonly UISettings settings = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueue dispatcher;
    readonly Dictionary<FrameworkElement, Storyboard> active = [];
    readonly bool supportsChangeEvent = Windows.Foundation.Metadata.ApiInformation.IsEventPresent("Windows.UI.ViewManagement.UISettings", "AnimationsEnabledChanged");
    public bool Enabled => settings.AnimationsEnabled;
    public bool? TestEnabled
    {
        get; set;
    }
    public Motion(Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        this.dispatcher = dispatcher;
        if (supportsChangeEvent)
            settings.AnimationsEnabledChanged += Changed;
    }
    void Changed(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args) => dispatcher.TryEnqueue(() => { if (!Enabled) StopAll(); });
    public void Enter(FrameworkElement target, bool backwards = false, bool horizontal = false)
    {
        Stop(target);
        if (!(TestEnabled ?? Enabled))
            return;
        var transform = new TranslateTransform();
        target.RenderTransform = transform;
        var storyboard = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(160) };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        var slide = new DoubleAnimation
        {
            From = (backwards ? -1 : 1) * (horizontal ? 16 : 8),
            To = 0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, horizontal ? "X" : "Y");
        storyboard.Children.Add(slide);
        active[target] = storyboard;
        storyboard.Completed += (_, _) => { if (active.TryGetValue(target, out var current) && current == storyboard) Stop(target); };
        storyboard.Begin();
    }
    public void Stop(FrameworkElement target)
    {
        if (active.Remove(target, out var previous))
            previous.Stop();
        target.Opacity = 1;
        if (target.RenderTransform is TranslateTransform transform)
        {
            transform.X = 0;
            transform.Y = 0;
        }
    }
    void StopAll()
    {
        foreach (var target in active.Keys.ToArray())
            Stop(target);
    }
    public void Dispose()
    {
        if (supportsChangeEvent)
            settings.AnimationsEnabledChanged -= Changed;
        StopAll();
    }
}
