using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    static readonly string[] CappedSettingsPages = ["Updates", "Startup", "About"];

    void HookSettingsContentLayout()
    {
        foreach (string name in CappedSettingsPages)
            C<ScrollViewer>(name + "Scroll").SizeChanged += (_, _) => LayoutSettingsContent();
        C<StackPanel>("UpdatesContent").SizeChanged += (_, _) => LayoutUpdateVersions();
        C<Border>("ConnectionCard").SizeChanged += (_, _) => LayoutSettingsContent();
        var apply = C<Button>("ApplyButton");
        apply.SizeChanged += (_, _) => LayoutSettingsContent();
        apply.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => LayoutSettingsContent());
    }

    void LayoutSettingsContent()
    {
        foreach (string name in CappedSettingsPages)
        {
            var scroll = C<ScrollViewer>(name + "Scroll");
            double available = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
            var content = C<StackPanel>(name + "Content");
            if (available > 0 && Math.Abs(content.Width - Math.Min(960, available)) > .5)
                content.Width = Math.Min(960, available);
        }
        LayoutUpdateVersions();
        var card = C<Border>("ConnectionCard");
        double sourceAvailable = card.ActualWidth - card.Padding.Left - card.Padding.Right - card.BorderThickness.Left - card.BorderThickness.Right;
        if (sourceAvailable <= 0)
            return;
        var source = C<Grid>("SourceGrid");
        var apply = C<Button>("ApplyButton");
        source.ColumnSpacing = apply.Visibility == Visibility.Visible ? 12 : 0;
        double width = Math.Min(sourceAvailable, 560 + (apply.Visibility == Visibility.Visible ? apply.ActualWidth + source.ColumnSpacing : 0));
        if (double.IsNaN(source.Width) || Math.Abs(source.Width - width) > .5)
            source.Width = width;
    }
}
