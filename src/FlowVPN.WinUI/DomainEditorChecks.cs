using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    void CheckDomainEditorLayout(FrameworkElement panel, TextBox input, IEnumerable<(Border Card, TextBox Input, FontIcon Indicator, Button Remove)> rows)
    {
        var reports = new List<object>();
        var inputBounds = VisualBounds(input, panel);
        foreach (var row in rows)
        {
            var remove = row.Remove;
            var previous = row.Indicator.Visibility;
            try
            {
                foreach (bool marked in new[] { false, true })
                {
                    row.Indicator.Visibility = marked ? Visibility.Visible : Visibility.Collapsed;
                    panel.UpdateLayout();
                    var editBounds = VisualBounds(row.Input, panel);
                    var removeBounds = VisualBounds(remove, panel);
                    var cardBounds = VisualBounds(row.Card, panel);
                    if (Math.Abs(editBounds.Left - inputBounds.Left) > 1 ||
                        row.Input.Padding.Left != input.Padding.Left || row.Input.BorderThickness.Left != input.BorderThickness.Left)
                        throw new InvalidOperationException("Domain rows do not align with the add-address field");
                    if (removeBounds.Width < 32 || removeBounds.Right > cardBounds.Right + 1 ||
                        remove.Visibility != Visibility.Visible || !remove.IsEnabled)
                        throw new InvalidOperationException("Domain removal is clipped or unavailable");
                    if (!marked && Math.Abs(removeBounds.Left - editBounds.Right - 8) > 1)
                        throw new InvalidOperationException("A normal domain row reserves unused indicator space");
                    if (marked)
                    {
                        var indicatorBounds = VisualBounds(row.Indicator, panel);
                        if (indicatorBounds.Width <= 0 || indicatorBounds.Left < editBounds.Right || indicatorBounds.Right > removeBounds.Left)
                            throw new InvalidOperationException("Domain warning overlaps the field or removal action");
                    }
                    reports.Add(new { Marked = marked, InputLeft = inputBounds.Left, Row = editBounds, Remove = removeBounds });
                }
            }
            finally { row.Indicator.Visibility = previous; panel.UpdateLayout(); }
        }
        if (reports.Count == 0)
            throw new InvalidOperationException("Domain layout fixture is empty");
        File.WriteAllText(Path.Combine(Store.Root, "design-preview", "domain-layout-checks.json"), JsonSerializer.Serialize(reports, Store.JsonOptions));
    }
}
