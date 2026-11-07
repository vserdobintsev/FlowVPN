using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    void LayoutProfileSettings()
    {
        var scroll = C<ScrollViewer>("ProfileSettingsScroll");
        double available = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
        if (available <= 0)
            return;
        double width = Math.Min(960, available);
        var content = C<StackPanel>("ProfileSettingsContent");
        if (Math.Abs(content.Width - width) > .5)
            content.Width = width;
        var routing = C<Grid>("ProfileRoutingGrid");
        bool twoColumns = width >= 720;
        routing.ColumnDefinitions[1].Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        routing.ColumnSpacing = twoColumns ? 16 : 0;
        routing.RowSpacing = twoColumns ? 0 : 16;
        Grid.SetColumn(C<Border>("ProfileRoutingCard"), twoColumns ? 1 : 0);
        Grid.SetRow(C<Border>("ProfileRoutingCard"), twoColumns ? 0 : 1);
        var links = C<Grid>("ProfileLinksGrid");
        links.ColumnDefinitions[1].Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        links.ColumnSpacing = twoColumns ? 16 : 0;
        links.RowSpacing = twoColumns ? 0 : 16;
        Grid.SetColumn(C<Button>("ProfileSitesButton"), twoColumns ? 1 : 0);
        Grid.SetRow(C<Button>("ProfileSitesButton"), twoColumns ? 0 : 1);
        var diagnostics = C<Grid>("ProfileDiagnosticsGrid");
        diagnostics.ColumnSpacing = twoColumns ? 16 : 0;
        diagnostics.RowSpacing = twoColumns ? 0 : 12;
        Grid.SetColumn(C<Button>("ValidateButton"), twoColumns ? 1 : 0);
        Grid.SetRow(C<Button>("ValidateButton"), twoColumns ? 0 : 1);
        C<StackPanel>("ProfileFooterActions").Orientation = width >= 420 ? Orientation.Horizontal : Orientation.Vertical;
    }
}
