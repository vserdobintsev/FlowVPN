using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    static void CheckProfileIdentityDialog(VpnProfile initial, bool creating, ContentDialog dialog, TextBox name, GridView icons, GridView colors)
    {
        if (name.Text != initial.Name || icons.SelectedItem is not GridViewItem icon || (string)icon.Tag != initial.Icon ||
            colors.SelectedItem is not GridViewItem color || (int)color.Tag != initial.IconColor)
            throw new InvalidOperationException("Profile identity fields were not initialized from the draft");
        if (!dialog.IsPrimaryButtonEnabled || dialog.PrimaryButtonText != (creating ? "Создать профиль" : "Сохранить"))
            throw new InvalidOperationException("Profile identity dialog has the wrong primary action");
    }

    async Task CheckProfileIdentity()
    {
        var profiles = settings.Profiles.ToArray();
        string selected = settings.SelectedProfileId;
        bool completed = settings.ProfileOnboardingCompleted;
        var identities = profiles.Select(p => (p.Name, p.SourceId, p.Icon, p.IconColor)).ToArray();
        string? previousDialogName = visualDialogName;
        try
        {
            // The existing visual harness captures the real creation dialog and closes it without Primary.
            visualDialogName = "profile-create";
            await NewProfile();
            if (!settings.Profiles.SequenceEqual(profiles) || settings.SelectedProfileId != selected ||
                settings.ProfileOnboardingCompleted != completed ||
                !settings.Profiles.Select(p => (p.Name, p.SourceId, p.Icon, p.IconColor)).SequenceEqual(identities))
                throw new InvalidOperationException("Canceling profile creation changed saved profile state");
            if (visualDialogError != null) throw new InvalidOperationException("Profile creation dialog check failed", visualDialogError);
            File.WriteAllText(Path.Combine(Store.Root, "design-preview", "profile-identity-checks.json"),
                JsonSerializer.Serialize(new { PrefilledIdentity = true, SharedDialog = true, CancelPreservesProfiles = true,
                    CancelPreservesSelection = true, CancelPreservesOnboarding = true }, Store.JsonOptions));
        }
        finally { visualDialogName = previousDialogName; }
    }
}
