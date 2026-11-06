using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    bool switchingCategory;
    void BindBaseCategories()
    {
        var combo = C<ComboBox>("DomainCategoryCombo");
        var categories = DomainCategories.Read().ToList();
        if (categories.All(c => c.Id != settings.BaseDomainCategory))
            categories.Add(DomainCategories.Describe(settings.BaseDomainCategory));
        string query = C<TextBox>("DomainCategorySearch").Text.Trim();
        categories = categories.Where(c => c.Id == settings.BaseDomainCategory || (c.Id + " " + c.Name).Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        combo.ItemsSource = categories;
        combo.DisplayMemberPath = nameof(DomainCategory.Name);
        combo.SelectedItem = categories.First(c => c.Id == settings.BaseDomainCategory);
        C<TextBlock>("DomainCategoryHint").Text = DomainCategories.Describe(settings.BaseDomainCategory).Description;
    }
    void HookBaseCategories()
    {
        C<TextBox>("DomainCategorySearch").TextChanged += (_, _) =>
        {
            bool previous = binding;
            binding = true;
            try
            {
                BindBaseCategories();
            }
            finally { binding = previous; }
        };
        Hook("DomainRepositoryLink", () => { Process.Start(new ProcessStartInfo(DomainRepository.Repository) { UseShellExecute = true }); return Task.CompletedTask; });
        C<ComboBox>("DomainCategoryCombo").SelectionChanged += async (_, _) =>
        {
            if (binding || switchingCategory || C<ComboBox>("DomainCategoryCombo").SelectedItem is not DomainCategory chosen || chosen.Id == settings.BaseDomainCategory)
                return;
            if (updating)
            {
                UpdateBase();
                return;
            }
            switchingCategory = true;
            C<ComboBox>("DomainCategoryCombo").IsEnabled = false;
            C<Button>("UpdateRulesButton").IsEnabled = false;
            C<TextBlock>("DomainCategoryHint").Text = "Загружаем и проверяем выбранную категорию…";
            try
            {
                using var client = engine.Running ? engine.Client() : BoundedHttp.Client(TimeSpan.FromSeconds(45));
                await DomainRepository.Update(client, chosen.Id);
                if (windowClosed)
                    return;
                settings.BaseDomainCategory = chosen.Id;
                Save();
                UpdateBase();
            }
            catch (Exception e) { if (!windowClosed) { UpdateBase(); await Message("Предыдущая категория сохранена.\n" + e.Message, "Категория не переключена"); } }
            finally { switchingCategory = false; if (!windowClosed) { C<ComboBox>("DomainCategoryCombo").IsEnabled = true; C<Button>("UpdateRulesButton").IsEnabled = true; } }
        };
    }
}
