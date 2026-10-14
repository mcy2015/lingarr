using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Models.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Extensions;

namespace Lingarr.Server.Modules.Media;

public class MappingService : IMappingService
{
    private readonly LingarrDbContext _context;

    public MappingService(LingarrDbContext context)
    {
        _context = context;
    }
    
    /// <inheritdoc />
    public async Task<List<PathMappingDto>> GetMapping()
    {
        var mappings = await _context.PathMappings.ToListAsync();
        return mappings.Select(pathMapping => new PathMappingDto
        {
            SourcePath = pathMapping.SourcePath,
            DestinationPath = pathMapping.DestinationPath,
            MediaType = pathMapping.MediaType.GetDisplayName()
        }).ToList();
    }
    
    /// <inheritdoc />
    public async Task SetMapping(List<PathMapping> mappings)
    {
        _context.PathMappings.RemoveRange(_context.PathMappings);
        await _context.PathMappings.AddRangeAsync(mappings);

        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task<MappingStatusDto> GetMappingStatus()
    {
        // Movie.Path/Episode.Path are stored post-mapping at directory level by
        // the sync pipeline, so a Directory.Exists probe per distinct value is
        // exactly the "would subtitles land" ground truth.
        var movieDirs = await _context.Movies
            .Where(m => m.Path != null && m.Path != string.Empty)
            .Select(m => m.Path!)
            .ToListAsync();
        var episodeDirs = await _context.Episodes
            .Where(e => e.Path != null && e.Path != string.Empty)
            .Select(e => e.Path!)
            .ToListAsync();

        var status = new MappingStatusDto();
        foreach (var (dirs, mediaType) in new[] { (movieDirs, "movie"), (episodeDirs, "show") })
        {
            foreach (var dir in dirs.Distinct(StringComparer.Ordinal))
            {
                status.Checked++;
                if (!Directory.Exists(dir))
                {
                    status.Unreachable.Add(new UnreachablePathDto { MediaType = mediaType, Path = dir });
                }
            }
        }

        return status;
    }
}