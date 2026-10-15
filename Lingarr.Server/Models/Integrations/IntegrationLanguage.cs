using System.Text.Json.Serialization;

namespace Lingarr.Server.Models.Integrations;

/// <summary>
/// A *arr Language resource (<c>{id, name}</c>) as carried by the
/// movieFile/episodeFile "languages" aggregate. It is a guess — *arr falls
/// back to the original language when it cannot detect anything — so it ranks
/// below mediaInfo.audioLanguages in the PRD card A trust chain.
/// </summary>
public class IntegrationLanguage
{
    [JsonPropertyName("id")]
    public required int Id { get; set; }

    [JsonPropertyName("name")]
    public required string Name { get; set; }
}
