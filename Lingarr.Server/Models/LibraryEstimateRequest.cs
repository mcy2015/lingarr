using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;

namespace Lingarr.Server.Models;

public class LibraryEstimateRequest
{
    public required List<EstimateItem> Items { get; init; }
    public required string TargetLanguage { get; init; }
}
