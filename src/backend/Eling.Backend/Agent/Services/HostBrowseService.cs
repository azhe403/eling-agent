using Eling.Backend.Dtos;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Agent.Services;

public sealed class HostBrowseService(ILogger<HostBrowseService> logger)
{
    private const int ListCap = 200;

    public IReadOnlyList<string> ListDrives()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady)
                    .Select(d => d.RootDirectory.FullName)
                    .ToList();
            }

            return ["/"];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list host drives");
            throw;
        }
    }

    public HostBrowseResponse Browse(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = new DirectoryInfo(full);
            if (!dir.Exists)
            {
                throw new FileNotFoundException($"Directory not found: {path}");
            }

            var entries = new List<FileListEntry>();
            foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new FileListEntry(sub.FullName, true));
                if (entries.Count >= ListCap)
                {
                    break;
                }
            }

            if (entries.Count < ListCap)
            {
                foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(new FileListEntry(file.FullName, false));
                    if (entries.Count >= ListCap)
                    {
                        break;
                    }
                }
            }

            var parent = dir.Parent?.FullName;
            return new HostBrowseResponse(full, parent, entries);
        }
        catch (Exception ex) when (ex is FileNotFoundException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to browse host path {Path}", path);
            throw;
        }
    }
}
