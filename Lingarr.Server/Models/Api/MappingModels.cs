using System.ComponentModel.DataAnnotations;

namespace Lingarr.Server.Models.Api;

/// <summary>
/// GET /api/mapping/status — ground-truth reachability check over the media
/// directories the sync pipeline already stored (post-mapping paths). A path
/// that is not on disk is exactly the condition under which translations get
/// silently skipped later.
/// </summary>
public class MappingStatusDto
{
    /// <summary>Number of distinct media directories checked.</summary>
    public int Checked { get; set; }

    /// <summary>Distinct directories that do not exist on this filesystem.</summary>
    public List<UnreachablePathDto> Unreachable { get; set; } = [];
}

public class UnreachablePathDto
{
    /// <summary>"movie" or "show" — the mapping domain the directory belongs to.</summary>
    public string MediaType { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

/// <summary>Body of POST /api/mapping/test — an *arr-style path to try against the saved mappings.</summary>
public class TestMappingRequest
{
    [Required]
    public string Path { get; set; } = string.Empty;

    /// <summary>"movie" or "show".</summary>
    [Required]
    public string MediaType { get; set; } = string.Empty;
}

/// <summary>
/// Verdict of POST /api/mapping/test — ok=false carries the reason instead of a
/// 5xx, mirroring the translate test-connection endpoint.
/// </summary>
public class TestMappingResult
{
    /// <summary>True only when a mapping hit and the mapped location exists.</summary>
    public bool Ok { get; set; }

    /// <summary>Whether any saved mapping applied to the input.</summary>
    public bool Matched { get; set; }

    /// <summary>The path after normalization and mapping.</summary>
    public string MappedPath { get; set; } = string.Empty;

    /// <summary>Whether the mapped location is visible to Lingarr (file or directory).</summary>
    public bool Exists { get; set; }
}
