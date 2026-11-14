namespace WindowsAxon.Windows;

/// <summary>
/// New chats load personal skills without approval, so a tool that writes there could plant instructions for later chats.
/// Only the Skills dialog changes that folder.
/// </summary>
public static class SkillsFolderGuard
{
    /// <summary>The full path, or an error when the path is the skills folder, inside it, or a folder above it.</summary>
    public static string Check(string path, string? skillsFolder)
    {
        path = Path.GetFullPath(path);
        if (skillsFolder is null)
        {
            return path;
        }

        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skillsFolder));
        var target = Path.TrimEndingDirectorySeparator(path);
        if (IsSameOrInside(target, folder) || IsSameOrInside(folder, target))
        {
            throw new UnauthorizedAccessException("Tools cannot change the personal skills folder. Use the Skills dialog in WindowsAxon.");
        }

        return path;
    }

    private static bool IsSameOrInside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
