using Microsoft.UI.Xaml.Controls;

namespace FlowVPN;

internal sealed partial class FluentWindow
{
    bool validatingProfile;

    async Task Validate()
    {
        if (validatingProfile || windowClosed || Profile is not { } selected) return;
        validatingProfile = true;
        var button = C<Button>("ValidateButton");
        button.IsEnabled = false;
        var token = windowLifetime.Token;
        try
        {
            var snapshot = ProfileValidator.Capture(settings, selected.Id);
            var configuration = await ProfileValidator.BuildConfiguration(snapshot, ConfigBuilder.Detect(), token);
            await CoreCli.Check(configuration, token);
            if (!windowClosed)
                await Message($"Профиль «{snapshot.Profile.Name}»: сохранённые настройки и загруженная общая база совместимы с sing-box. VPN и транспортный модуль не запускались. Доступность сервера проверяется при подключении.", "Совместимость настроек подтверждена");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!windowClosed) await Message(error.Message, "Не удалось проверить настройки");
        }
        finally
        {
            validatingProfile = false;
            if (!windowClosed) button.IsEnabled = Profile != null;
        }
    }
}
