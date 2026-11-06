namespace FlowVPN;

internal static class UniqueNames
{
    public static string Reserve(string name, HashSet<string> names, int maximumLength = 180)
    {
        string stem = name.Length <= maximumLength ? name : name[..maximumLength].TrimEnd();
        if (names.Add(stem)) return stem;
        for (int suffix = 2; ; suffix++)
        {
            string ending = " (" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            string candidate = stem[..Math.Min(stem.Length, maximumLength - ending.Length)].TrimEnd() + ending;
            if (names.Add(candidate)) return candidate;
        }
    }
}
