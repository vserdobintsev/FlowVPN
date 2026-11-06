using System.Text;

namespace FlowVPN;

internal static class OpenVpnSyntax
{
    // OpenVPN configuration escaping is not shell execution. Comments start between arguments.
    internal static string[] Tokens(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
                i++;
            if (i == line.Length || line[i] is '#' or ';')
                break;
            char quote = line[i] is '\'' or '"' ? line[i++] : '\0';
            var value = new StringBuilder();
            bool closed = quote == '\0';
            while (i < line.Length)
            {
                char c = line[i++];
                if (quote != '\0' && c == quote)
                {
                    closed = true;
                    break;
                }
                if (quote == '\0' && char.IsWhiteSpace(c))
                    break;
                if (c == '\\' && quote != '\'')
                {
                    if (i == line.Length || (line[i] != '\\' && line[i] != '"' && !char.IsWhiteSpace(line[i])))
                        throw new InvalidDataException("Некорректное экранирование в OpenVPN-профиле.");
                    c = line[i++];
                }
                value.Append(c);
            }
            if (!closed || (quote != '\0' && i < line.Length && !char.IsWhiteSpace(line[i])))
                throw new InvalidDataException("Некорректные кавычки в OpenVPN-профиле.");
            tokens.Add(value.ToString());
        }
        return tokens.ToArray();
    }
    internal static string Network(string value) => value switch
    {
        "udp" or "udp4" or "udp6" or "tcp" or "tcp4" or "tcp6" => value,
        "tcp-client" => "tcp",
        "tcp4-client" => "tcp4",
        "tcp6-client" => "tcp6",
        _ => throw new InvalidDataException("Не поддерживается клиентский протокол OpenVPN: " + value)
    };
    internal static int Port(string value) => int.TryParse(value, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535
        ? port : throw new InvalidDataException("Некорректный порт OpenVPN.");
}
