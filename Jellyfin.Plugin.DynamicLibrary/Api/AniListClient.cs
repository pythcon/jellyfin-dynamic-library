using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

/// <summary>
/// Client for AniList GraphQL API.
/// No API key required - public API.
/// </summary>
public class AniListClient
{
    private const string GraphQlEndpoint = "https://graphql.anilist.co";

    private readonly HttpClient _httpClient;
    private readonly ILogger<AniListClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniListClient"/> class.
    /// </summary>
    public AniListClient(IHttpClientFactory httpClientFactory, ILogger<AniListClient> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        _logger = logger;
    }

    /// <summary>
    /// Search AniList for an anime by title and return its AniList ID.
    /// </summary>
    /// <param name="title">The anime title to search for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The AniList ID if found, null otherwise.</returns>
    public async Task<int?> SearchByTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        const string query = @"
            query ($search: String) {
                Media(search: $search, type: ANIME) {
                    id
                    idMal
                    title {
                        romaji
                        english
                        native
                    }
                }
            }";

        var variables = new { search = title };

        try
        {
            var response = await ExecuteQueryAsync<AniListMediaResponse>(query, variables, cancellationToken);
            var media = response?.Data?.Media;

            if (media != null)
            {
                _logger.LogDebug("[DynamicLibrary] AniList: Found anime '{Title}' with ID {Id}",
                    media.Title?.English ?? media.Title?.Romaji ?? title, media.Id);
                return media.Id;
            }

            _logger.LogDebug("[DynamicLibrary] AniList: No results found for '{Title}'", title);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DynamicLibrary] AniList: Error searching for '{Title}'", title);
            return null;
        }
    }

    /// <summary>
    /// Search AniList for an anime by MAL ID and return its AniList ID.
    /// </summary>
    /// <param name="malId">The MyAnimeList ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The AniList ID if found, null otherwise.</returns>
    public async Task<int?> SearchByMalIdAsync(int malId, CancellationToken cancellationToken = default)
    {
        const string query = @"
            query ($idMal: Int) {
                Media(idMal: $idMal, type: ANIME) {
                    id
                    idMal
                    title {
                        romaji
                        english
                        native
                    }
                }
            }";

        var variables = new { idMal = malId };

        try
        {
            var response = await ExecuteQueryAsync<AniListMediaResponse>(query, variables, cancellationToken);
            var media = response?.Data?.Media;

            if (media != null)
            {
                _logger.LogDebug("[DynamicLibrary] AniList: Found anime by MAL ID {MalId} -> AniList ID {Id}",
                    malId, media.Id);
                return media.Id;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DynamicLibrary] AniList: Error searching by MAL ID {MalId}", malId);
            return null;
        }
    }

    /// <summary>
    /// Execute a GraphQL query against AniList API.
    /// </summary>
    private async Task<T?> ExecuteQueryAsync<T>(string query, object variables, CancellationToken cancellationToken)
        where T : class
    {
        var requestBody = new
        {
            query,
            variables
        };

        var jsonContent = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(GraphQlEndpoint, content, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("[DynamicLibrary] AniList API returned {StatusCode}", response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
    }
}
