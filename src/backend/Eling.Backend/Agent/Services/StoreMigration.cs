using Microsoft.Extensions.Logging;

namespace Eling.Backend.Agent.Services;

public static class StoreMigration
{
    public static void MoveFileIfNeeded(string globalPath, string legacyPath, ILogger logger, string label)
    {
        try
        {
            if (SamePath(globalPath, legacyPath) || File.Exists(globalPath))
            {
                return;
            }

            if (!File.Exists(legacyPath))
            {
                return;
            }

            var dir = Path.GetDirectoryName(globalPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.Copy(legacyPath, globalPath);
            File.Delete(legacyPath);
            logger.LogInformation("Migrated {Label} from project scope to global scope", label);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to migrate {Label} to global scope", label);
        }
    }

    public static void MoveDirectoryIfNeeded(string globalDir, string legacyDir, ILogger logger, string label)
    {
        try
        {
            if (SamePath(globalDir, legacyDir))
            {
                return;
            }

            if (!Directory.Exists(legacyDir))
            {
                return;
            }

            if (Directory.Exists(globalDir))
            {
                foreach (var file in Directory.EnumerateFiles(legacyDir))
                {
                    var dest = Path.Combine(globalDir, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        File.Move(file, dest);
                    }
                }
                try { Directory.Delete(legacyDir, true); } catch { }
                return;
            }

            var parent = Path.GetDirectoryName(globalDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            Directory.Move(legacyDir, globalDir);
            logger.LogInformation("Migrated {Label} from project scope to global scope", label);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to migrate {Label} to global scope", label);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
