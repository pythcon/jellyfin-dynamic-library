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
    /// Search AniList for an anime by title and year, returning the best match.
    /// Returns multiple results and picks the best match based on year.
    /// </summary>
    /// <param name="title">The anime title to search for.</param>
    /// <param name="year">The production year to match against (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The AniList ID if found, null otherwise.</returns>
    public async Task<int?> SearchByTitleAndYearAsync(string title, int? year, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        const string query = @"
            query ($search: String) {
                Page(page: 1, perPage: 10) {
                    media(search: $search, type: ANIME, sort: [SEARCH_MATCH]) {
                        id
                        idMal
                        seasonYear
                        title {
                            romaji
                            english
                            native
                        }
                    }
                }
            }";

        var variables = new { search = title };

        try
        {
            var response = await ExecuteQueryAsync<AniListPageResponse>(query, variables, cancellationToken);
            var results = response?.Data?.Page?.Media;

            if (results == null || results.Count == 0)
            {
                _logger.LogDebug("[DynamicLibrary] AniList: No results found for '{Title}' (year: {Year})", title, year);
                return null;
            }

            // If we have a year, find best match
            if (year.HasValue)
            {
                // Exact year match
                var exactMatch = results.FirstOrDefault(r => r.SeasonYear == year.Value);
                if (exactMatch != null)
                {
                    _logger.LogDebug("[DynamicLibrary] AniList: Found exact year match for '{Title}' ({Year}) -> ID {Id}",
                        title, year.Value, exactMatch.Id);
                    return exactMatch.Id;
                }

                // ±1 year tolerance (anime may air across year boundaries)
                var closeMatch = results.FirstOrDefault(r =>
                    r.SeasonYear.HasValue && Math.Abs(r.SeasonYear.Value - year.Value) <= 1);
                if (closeMatch != null)
                {
                    _logger.LogDebug("[DynamicLibrary] AniList: Found close year match for '{Title}' ({Year} vs {MatchYear}) -> ID {Id}",
                        title, year.Value, closeMatch.SeasonYear, closeMatch.Id);
                    return closeMatch.Id;
                }

                _logger.LogDebug("[DynamicLibrary] AniList: No year match found for '{Title}' ({Year}), using best search match",
                    title, year.Value);
            }

            // Fall back to first result (best search match)
            var firstResult = results.First();
            _logger.LogDebug("[DynamicLibrary] AniList: Using best search match for '{Title}' -> '{MatchTitle}' (ID {Id}, Year {Year})",
                title, firstResult.Title?.English ?? firstResult.Title?.Romaji ?? "Unknown", firstResult.Id, firstResult.SeasonYear);
            return firstResult.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DynamicLibrary] AniList: Error searching for '{Title}' with year {Year}", title, year);
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
