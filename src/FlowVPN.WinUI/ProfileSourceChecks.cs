using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    async Task CheckProfileSources()
    {
        var profile = Profile ?? throw new InvalidOperationException("Source selection check requires a profile fixture");
        var originalSources = settings.Sources.ToList();
        string originalSourceId = profile.SourceId;
        int originalPage = page;
        bool originalBinding = binding;
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Profile source selection: " + name);
            checks.Add(name);
        }
        var first = new VpnSource { Id = Guid.NewGuid().ToString("N"), Name = "Проверка выбора · OpenVPN", Kind = "OpenVPN", ConnectionJson = "{}" };
        var second = new VpnSource { Id = Guid.NewGuid().ToString("N"), Name = "Проверка выбора · Outline", Kind = "Outline", ConnectionJson = "{}" };
        var transaction = new VpnProfile { SourceId = first.Id };
        bool failed = false;
        try { CommitProfileSource(transaction, second.Id, () => throw new IOException("Synthetic save failure")); }
        catch (IOException) { failed = true; }
        Check(failed && transaction.SourceId == first.Id, "A failed save restores the previous source");
        int writes = 0;
        CommitProfileSource(transaction, second.Id, () => writes++);
        Check(transaction.SourceId == second.Id && writes == 1, "A successful transaction persists exactly once");

        try
        {
            settings.Sources.Add(first);
            settings.Sources.Add(second);
            profile.SourceId = first.Id;
            binding = false;
            BindAll();
            Store.Save(settings);
            var main = C<ComboBox>("SourceCombo");
            var details = C<ComboBox>("ProfileSourceCombo");
            var mainItems = main.Items.Cast<object>().ToArray();
            var detailItems = details.Items.Cast<object>().ToArray();
            var profileItem = C<ListView>("ProfileList").Items.OfType<ListViewItem>().Single(item => ReferenceEquals(item.Tag, profile));
            foreach (var target in new[] { (Control: details, Page: 4, Name: "settings"), (Control: main, Page: 0, Name: "main") })
            {
                SelectPage(target.Page);
                target.Control.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await Task.Delay(150);
                root.UpdateLayout();
                for (int step = 0; step < 4; step++)
                {
                    var next = step % 2 == 0 ? second : first;
                    target.Control.IsDropDownOpen = true;
                    await Task.Delay(100);
                    Check(target.Control.IsDropDownOpen, "The real dropdown is open: " + target.Name + "/" + step);
                    var selected = target.Control.Items.OfType<ComboBoxItem>().Single(item => item.Tag is string id && id == next.Id);
                    target.Control.SelectedItem = selected;
                    await Task.Delay(100);
                    Check(ReferenceEquals(target.Control.SelectedItem, selected) && profile.SourceId == next.Id, "Selection commits while the dropdown owns its containers: " + target.Name + "/" + step);
                    Check(main.Items.Cast<object>().SequenceEqual(mainItems, ReferenceEqualityComparer.Instance)
                        && details.Items.Cast<object>().SequenceEqual(detailItems, ReferenceEqualityComparer.Instance), "Both selectors retain their item instances: " + target.Name + "/" + step);
                    Check((main.SelectedItem as ComboBoxItem)?.Tag as string == next.Id && (details.SelectedItem as ComboBoxItem)?.Tag as string == next.Id,
                        "Both selectors reflect the saved source: " + target.Name + "/" + step);
                    Check(page == target.Page && ReferenceEquals(C<ListView>("ProfileList").SelectedItem, profileItem)
                        && profileSourceLabels[profile.Id].Text == next.Kind, "Navigation stays intact and its subtitle updates: " + target.Name + "/" + step);
                    Check(Store.Load().Profiles.Single(item => item.Id == profile.Id).SourceId == next.Id, "The source survives a settings reload: " + target.Name + "/" + step);
                    target.Control.IsDropDownOpen = false;
                    await Task.Delay(80);
                }
            }
            first.Name += " · переименовано";
            BindAll();
            Check(main.Items.Cast<object>().SequenceEqual(mainItems, ReferenceEqualityComparer.Instance)
                && details.Items.Cast<object>().SequenceEqual(detailItems, ReferenceEqualityComparer.Instance), "Renaming a connection updates labels without replacing containers");
            Check(main.Items.OfType<ComboBoxItem>().Single(item => item.Tag is string id && id == first.Id).Content is string label && label.Contains(first.Name, StringComparison.Ordinal),
                "The existing item displays the updated connection name");
            File.WriteAllText(Path.Combine(Store.Root, "design-preview", "profile-source-checks.json"), JsonSerializer.Serialize(checks, Store.JsonOptions));
        }
        finally
        {
            try
            {
                C<ComboBox>("SourceCombo").IsDropDownOpen = false;
                C<ComboBox>("ProfileSourceCombo").IsDropDownOpen = false;
                binding = true;
                settings.Sources.Clear();
                settings.Sources.AddRange(originalSources);
                profile.SourceId = originalSourceId;
                BindAll();
                SelectPage(originalPage);
                Store.Save(settings);
            }
            finally { binding = originalBinding; }
        }
    }
}
