using Eling.Backend.Dtos;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Agent.Services;

public sealed class BackendFileTools(ILogger<BackendFileTools> logger)
{
    private const int ListCap = 200;
    private const int ReadCapBytes = 64 * 1024;
    private const int WriteCapBytes = 128 * 1024;

    public IReadOnlyList<FileListEntry> ListDir(string dirFullPath)
    {
        try
        {
            var dir = new DirectoryInfo(dirFullPath);
            if (!dir.Exists)
            {
                throw new FileNotFoundException($"Directory not found: {dirFullPath}");
            }

            var entries = new List<FileListEntry>();
            foreach (var sub in dir.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new FileListEntry(sub.Name, true));
                if (entries.Count >= ListCap)
                {
                    return entries;
                }
            }

            foreach (var file in dir.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(new FileListEntry(file.Name, false));
                if (entries.Count >= ListCap)
                {
                    break;
                }
            }

            return entries;
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            logger.LogWarning(ex, "Failed to list directory {Path}", dirFullPath);
            throw;
        }
    }

    public (string Content, bool Truncated) ReadText(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {fullPath}");
            }

            using var stream = File.OpenRead(fullPath);
            var buffer = new byte[Math.Min(ReadCapBytes + 1, stream.Length > 0 ? stream.Length : ReadCapBytes + 1)];
            var read = stream.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    throw new InvalidOperationException("binary");
                }
            }

            var truncated = stream.Length > ReadCapBytes;
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(read, ReadCapBytes));
            return (text, truncated);
        }
        catch (Exception ex) when (ex is FileNotFoundException || (ex is InvalidOperationException ioe && ioe.Message == "binary"))
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read file {Path}", fullPath);
            throw;
        }
    }

    public void WriteText(string fullPath, string content)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
            if (bytes > WriteCapBytes)
            {
                throw new ArgumentException($"Content exceeds {WriteCapBytes} bytes.", nameof(content));
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = fullPath + $".tmp-{Guid.NewGuid():N}";
            File.WriteAllText(temp, content);
            File.Move(temp, fullPath, overwrite: true);
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write file {Path}", fullPath);
            throw;
        }
    }
}
