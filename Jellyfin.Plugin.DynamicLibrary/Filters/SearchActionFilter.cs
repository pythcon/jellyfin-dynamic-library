using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Extensions;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicLibraryService _dynamicLibraryService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SearchActionFilter> _logger;

    public SearchActionFilter(
        DynamicLibraryService dynamicLibraryService,
        ILibraryManager libraryManager,
        ILogger<SearchActionFilter> logger)
    {
        _dynamicLibraryService = dynamicLibraryService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public int Order => 1;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Check if plugin is enabled
        if (!_dynamicLibraryService.IsEnabled)
        {
            await next();
            return;
        }

        // Check if this is a search action
        if (!context.IsApiSearchAction())
        {
            await next();
            return;
        }

        // Check for searchTerm
        if (!context.TryGetActionArgument<string>("searchTerm", out var searchTerm)
            || string.IsNullOrWhiteSpace(searchTerm))
        {
            await next();
            return;
        }

        // Get requested item types
        var requestedTypes = context.GetRequestedItemTypes();
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        // Get pagination parameters
        context.TryGetActionArgument("startIndex", out var startIndex, 0);
        context.TryGetActionArgument("limit", out var limit, 25);

        // Let the original action execute first to get real results
        var resultContext = await next();

        // If the original action failed, don't modify
        // Note: Jellyfin returns ObjectResult, not OkObjectResult
        if (resultContext.Result is not ObjectResult objectResult)
        {
            return;
        }

        // Get the original results
        var originalResults = objectResult.Value as QueryResult<BaseItemDto>;
        if (originalResults is null)
        {
            return;
        }

        try
        {
            // Search our APIs
            var dynamicResults = await _dynamicLibraryService.SearchAsync(
                searchTerm,
                requestedTypes,
                context.HttpContext.RequestAborted);

            if (dynamicResults.Count == 0)
            {
                return;
            }

            // Get IDs of existing items from search results to avoid duplicates
            var existingTmdbIds = originalResults.Items
                .Where(i => i.ProviderIds?.ContainsKey("Tmdb") == true)
                .Select(i => i.ProviderIds["Tmdb"])
                .ToHashSet();

            var existingTvdbIds = originalResults.Items
                .Where(i => i.ProviderIds?.ContainsKey("Tvdb") == true)
                .Select(i => i.ProviderIds["Tvdb"])
                .ToHashSet();

            var existingImdbIds = originalResults.Items
                .Where(i => i.ProviderIds?.ContainsKey("Imdb") == true)
                .Select(i => i.ProviderIds["Imdb"])
                .ToHashSet();

            // Also check the Jellyfin library for items that might exist there
            // but weren't in the search results (e.g., persisted items)
            foreach (var dynamicResult in dynamicResults)
            {
                if (dynamicResult.ProviderIds == null) continue;

                // Check IMDB
                if (dynamicResult.ProviderIds.TryGetValue("Imdb", out var imdbId) &&
                    !string.IsNullOrEmpty(imdbId) &&
                    !existingImdbIds.Contains(imdbId))
                {
                    if (ItemExistsInLibrary("Imdb", imdbId))
                    {
                        existingImdbIds.Add(imdbId);
                    }
                }

                // Check TMDB
                if (dynamicResult.ProviderIds.TryGetValue("Tmdb", out var tmdbId) &&
                    !string.IsNullOrEmpty(tmdbId) &&
                    !existingTmdbIds.Contains(tmdbId))
                {
                    if (ItemExistsInLibrary("Tmdb", tmdbId))
                    {
                        existingTmdbIds.Add(tmdbId);
                    }
                }

                // Check TVDB
                if (dynamicResult.ProviderIds.TryGetValue("Tvdb", out var tvdbId) &&
                    !string.IsNullOrEmpty(tvdbId) &&
                    !existingTvdbIds.Contains(tvdbId))
                {
                    if (ItemExistsInLibrary("Tvdb", tvdbId))
                    {
                        existingTvdbIds.Add(tvdbId);
                    }
                }
            }

            // Filter out duplicates from dynamic results
            var filteredDynamicResults = dynamicResults.Where(d =>
            {
                if (d.ProviderIds == null) return true;

                if (d.ProviderIds.TryGetValue("Imdb", out var imdbId) && existingImdbIds.Contains(imdbId))
                    return false;

                if (d.ProviderIds.TryGetValue("Tmdb", out var tmdbId) && existingTmdbIds.Contains(tmdbId))
                    return false;

                if (d.ProviderIds.TryGetValue("Tvdb", out var tvdbId) && existingTvdbIds.Contains(tvdbId))
                    return false;

                return true;
            }).ToList();

            if (filteredDynamicResults.Count == 0)
            {
                _logger.LogDebug("All dynamic results were duplicates of existing items");
                return;
            }

            // Merge results: original items first, then dynamic items
            var mergedItems = originalResults.Items.ToList();
            mergedItems.AddRange(filteredDynamicResults);

            // Apply pagination to merged results
            var totalCount = mergedItems.Count;
            var pagedItems = mergedItems.Skip(startIndex).Take(limit).ToArray();

            _logger.LogInformation(
                "Search for '{Query}' merged {OriginalCount} original + {DynamicCount} dynamic results (total: {Total})",
                searchTerm,
                originalResults.Items.Count,
                filteredDynamicResults.Count,
                totalCount);

            // Replace the result
            resultContext.Result = new OkObjectResult(new QueryResult<BaseItemDto>
            {
                Items = pagedItems,
                TotalRecordCount = totalCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error merging dynamic search results for '{Query}'", searchTerm);
            // Keep original results on error
        }
    }

    /// <summary>
    /// Check if an item with the given provider ID exists in Jellyfin's library.
    /// </summary>
    private bool ItemExistsInLibrary(string providerName, string providerId)
    {
        try
        {
            var query = new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string> { { providerName, providerId } },
                Recursive = true,
                Limit = 1 // We only need to know if at least one exists
            };

            var result = _libraryManager.GetItemsResult(query);
            return result.TotalRecordCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking library for {Provider}:{Id}", providerName, providerId);
            return false;
        }
    }
}
