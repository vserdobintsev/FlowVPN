namespace FlowVPN;

// A boundary for read-only discovery, not a replacement for a leased handle when executing a file.
internal static class LocalPathPolicy
{
    public static bool TryNormalize(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            return false;
        if (path.AsSpan(2).IndexOfAny(':', '\0', '*') >= 0 || path.IndexOfAny(['?', '"', '<', '>', '|']) >= 0)
            return false;
        try
        {
            string normalized = Path.GetFullPath(path);
            foreach (string segment in normalized[3..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment.EndsWith(' ') || segment.EndsWith('.') || ReservedDevice(segment))
                    return false;
            }
            if (!IsLocalDriveType(new DriveInfo(Path.GetPathRoot(normalized)!).DriveType))
                return false;
            fullPath = normalized;
            return true;
        }
        catch (Exception error) when (PathError(error)) { return false; }
    }

    internal static bool IsLocalDriveType(DriveType type) => type is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram;

    public static bool TryFile(string? path, out string fullPath, bool allowLocalLinks = true) => TryExisting(path, false, allowLocalLinks, out fullPath);
    public static bool TryDirectory(string? path, out string fullPath, bool allowLocalLinks = true) => TryExisting(path, true, allowLocalLinks, out fullPath);

    static bool TryExisting(string? path, bool directory, bool allowLocalLinks, out string fullPath)
    {
        fullPath = "";
        if (!TryNormalize(path, out string normalized))
            return false;
        try
        {
            for (int links = 0; links <= 16; links++)
            {
                string current = Path.GetPathRoot(normalized)!;
                var segments = normalized[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                FileAttributes attributes = File.GetAttributes(current);
                bool redirected = false;
                for (int index = 0; index < segments.Length; index++)
                {
                    current = Path.Combine(current, segments[index]);
                    attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                        continue;
                    if (!allowLocalLinks || links == 16)
                        return false;
                    FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
                    // Read this link only. ResolveLinkTarget(true) can follow a remote second hop before validation.
                    string? target = entry.LinkTarget;
                    if (string.IsNullOrWhiteSpace(target))
                        return false;
                    if (!Path.IsPathRooted(target))
                        target = Path.Combine(Path.GetDirectoryName(current)!, target);
                    if (!TryNormalize(target, out string localTarget))
                        return false;
                    string suffix = string.Join(Path.DirectorySeparatorChar, segments.Skip(index + 1));
                    if (!TryNormalize(Path.Combine(localTarget, suffix), out normalized))
                        return false;
                    redirected = true;
                    break;
                }
                if (redirected)
                    continue;
                if (((attributes & FileAttributes.Directory) != 0) != directory)
                    return false;
                fullPath = current;
                return true;
            }
        }
        catch (Exception error) when (PathError(error)) { }
        return false;
    }

    static bool ReservedDevice(string segment)
    {
        string name = segment.Split('.')[0].TrimEnd(' ');
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;
        return name.Length == 4 && name[3] is (>= '1' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3'
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    static bool PathError(Exception error) => error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;
}
