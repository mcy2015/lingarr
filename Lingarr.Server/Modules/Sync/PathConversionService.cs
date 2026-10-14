using Lingarr.Core.Data;
using Lingarr.Core.Enum;

namespace Lingarr.Server.Modules.Sync;

/// <summary>Outcome of a path conversion: the rewritten path and whether any mapping applied.</summary>
public record PathConversionResult(string MappedPath, bool Matched);

public class PathConversionService
{
    private readonly LingarrDbContext _context;

    public PathConversionService(LingarrDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Converts and maps a source path based on the specified media type, applying configured path mappings
    /// and normalizing directory separators.
    /// </summary>
    /// <param name="sourcePath">The original path to be converted.</param>
    /// <param name="mediaType">The type of media associated with the path.</param>
    public string ConvertAndMapPath(string sourcePath, MediaType mediaType)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return sourcePath;
        }

        var normalizedPath = NormalizePath(sourcePath);
        return PathReplace(normalizedPath, mediaType).MappedPath;
    }

    /// <summary>
    /// Same conversion as <see cref="ConvertAndMapPath"/> but reports whether any
    /// mapping matched — the mapping-test endpoint uses it so the verdict and the
    /// sync pipeline share one code path.
    /// </summary>
    public PathConversionResult TryConvertAndMapPath(string sourcePath, MediaType mediaType)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            return new PathConversionResult(sourcePath, false);
        }

        var normalizedPath = NormalizePath(sourcePath);
        return PathReplace(normalizedPath, mediaType);
    }

    /// <summary>
    /// Normalizes a file system path by converting Windows-style backslashes to forward slashes
    /// and removing Windows drive letters (e.g., "C:" or "D:").
    /// </summary>
    /// <param name="path">The path to normalize. Can be either Windows or Linux style path.</param>
    public string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        path = path.Replace('\\', '/').TrimStart();

        if (path.Length >= 2 && path[1] == ':')
        {
            path = path.Substring(2);
        }

        return path;
    }

    /// <summary>
    /// Applies configured path mappings from the database to the source path for the specified media type.
    /// </summary>
    /// <param name="sourcePath">The original path to apply mappings to.</param>
    /// <param name="mediaType">The type of media to filter relevant path mappings.</param>
    /// <returns>The rewritten path and whether any mapping applied.</returns>
    private PathConversionResult PathReplace(string sourcePath, MediaType mediaType)
    {
        var mappings = _context.PathMappings.Where(m => m.MediaType == mediaType).ToList();
        var matched = false;
        foreach (var mapping in mappings)
        {
            if (sourcePath.Contains(mapping.SourcePath))
            {
                sourcePath = sourcePath.Replace(mapping.SourcePath, mapping.DestinationPath);
                matched = true;
            }
        }

        return new PathConversionResult(sourcePath, matched);
    }
}