using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Filter that intercepts season and episode requests for dynamic TV series
/// and returns virtual seasons/episodes from the cache.
/// </summary>
public class SeasonEpisodeFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly ILogger<SeasonEpisodeFilter> _logger;

    public SeasonEpisodeFilter(
        DynamicItemCache itemCache,
        ILogger<SeasonEpisodeFilter> logger)
    {
        _itemCache = itemCache;
        _logger = logger;
    }

    // Run before other filters
    public int Order => 0;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

        // Debug logging to see all requests
        if (controllerName == "Items")
        {
            _logger.LogDebug("[DynamicLibrary] SeasonEpisodeFilter: Items controller, Action={Action}, Args={Args}",
                actionName, string.Join(", ", context.ActionArguments.Select(kv => $"{kv.Key}={kv.Value}")));
        }

        // Handle TvShows controller
        if (controllerName == "TvShows")
        {
            // Handle GetSeasons
            if (actionName == "GetSeasons")
            {
                await HandleGetSeasons(context, next);
                return;
            }

            // Handle GetEpisodes
            if (actionName == "GetEpisodes")
            {
                await HandleGetEpisodes(context, next);
                return;
            }
        }

        // Handle Items controller - Android TV uses this endpoint with parentId
        if (controllerName == "Items" && actionName == "GetItems")
        {
            await HandleGetItems(context, next);
            return;
        }

        await next();
    }

    private async Task HandleGetSeasons(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Get series ID from action arguments
        if (!context.ActionArguments.TryGetValue("seriesId", out var seriesIdObj) || seriesIdObj is not Guid seriesId)
        {
            await next();
            return;
        }

        // Check if this is a dynamic series
        if (!_itemCache.HasItem(seriesId))
        {
            await next();
            return;
        }

        // Get cached seasons
        var seasons = _itemCache.GetSeasonsForSeries(seriesId);
        if (seasons == null || seasons.Count == 0)
        {
            _logger.LogDebug("[DynamicLibrary] No cached seasons for dynamic series {Id}", seriesId);
            // Return empty result rather than letting Jellyfin try to look it up
            context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
            {
                Items = Array.Empty<BaseItemDto>(),
                TotalRecordCount = 0
            });
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Returning {Count} cached seasons for dynamic series {Id}",
            seasons.Count, seriesId);

        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = seasons.ToArray(),
            TotalRecordCount = seasons.Count
        });
    }

    private async Task HandleGetEpisodes(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Get series ID from action arguments
        if (!context.ActionArguments.TryGetValue("seriesId", out var seriesIdObj) || seriesIdObj is not Guid seriesId)
        {
            await next();
            return;
        }

        // Check if this is a dynamic series
        if (!_itemCache.HasItem(seriesId))
        {
            await next();
            return;
        }

        // Get season filter - can be seasonId (Guid) or season (int)
        int? seasonNumber = null;

        // First check for seasonId (Guid) - this is what Jellyfin passes when clicking a season
        if (context.ActionArguments.TryGetValue("seasonId", out var seasonIdObj) && seasonIdObj is Guid seasonId)
        {
            // Look up the season to get its number
            var seasonDto = _itemCache.GetItem(seasonId);
            if (seasonDto?.IndexNumber != null)
            {
                seasonNumber = seasonDto.IndexNumber;
            }
        }
        // Also check for season number directly
        else if (context.ActionArguments.TryGetValue("season", out var seasonObj) && seasonObj is int sn)
        {
            seasonNumber = sn;
        }

        // Get cached episodes
        List<BaseItemDto>? episodes;
        if (seasonNumber.HasValue)
        {
            episodes = _itemCache.GetEpisodesForSeason(seriesId, seasonNumber.Value);
        }
        else
        {
            episodes = _itemCache.GetEpisodesForSeries(seriesId);
        }

        if (episodes == null || episodes.Count == 0)
        {
            _logger.LogDebug("[DynamicLibrary] No cached episodes for dynamic series {Id} (season: {Season})",
                seriesId, seasonNumber?.ToString() ?? "all");
            // Return empty result
            context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
            {
                Items = Array.Empty<BaseItemDto>(),
                TotalRecordCount = 0
            });
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Returning {Count} cached episodes for dynamic series {Id} (season: {Season})",
            episodes.Count, seriesId, seasonNumber?.ToString() ?? "all");

        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = episodes.ToArray(),
            TotalRecordCount = episodes.Count
        });
    }

    private async Task HandleGetItems(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Check for parentId parameter - Android TV uses this to get children of items
        // parentId can be Guid or Guid? (nullable) - when boxed, Guid? becomes Guid or null
        if (!context.ActionArguments.TryGetValue("parentId", out var parentIdObj) ||
            parentIdObj == null ||
            parentIdObj is not Guid parentId)
        {
            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] HandleGetItems: Checking parentId {ParentId}", parentId);

        // Check if parent is a dynamic item
        var parentItem = _itemCache.GetItem(parentId);
        if (parentItem == null)
        {
            await next();
            return;
        }

        // If parent is a series, return seasons
        if (parentItem.Type == BaseItemKind.Series)
        {
            var seasons = _itemCache.GetSeasonsForSeries(parentId);
            if (seasons != null && seasons.Count > 0)
            {
                _logger.LogDebug("[DynamicLibrary] Items/GetItems: Returning {Count} seasons for dynamic series {Id}",
                    seasons.Count, parentId);

                context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                {
                    Items = seasons.ToArray(),
                    TotalRecordCount = seasons.Count
                });
                return;
            }
        }

        // If parent is a season, return episodes
        if (parentItem.Type == BaseItemKind.Season)
        {
            // Get the series ID and season number
            var seriesId = parentItem.SeriesId ?? Guid.Empty;
            var seasonNumber = parentItem.IndexNumber;

            if (seriesId != Guid.Empty && seasonNumber.HasValue)
            {
                var episodes = _itemCache.GetEpisodesForSeason(seriesId, seasonNumber.Value);
                if (episodes != null && episodes.Count > 0)
                {
                    _logger.LogDebug("[DynamicLibrary] Items/GetItems: Returning {Count} episodes for dynamic season {SeasonNum} of series {SeriesId}",
                        episodes.Count, seasonNumber.Value, seriesId);

                    context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                    {
                        Items = episodes.ToArray(),
                        TotalRecordCount = episodes.Count
                    });
                    return;
                }
            }
        }

        // Return empty result for dynamic items with no children
        _logger.LogDebug("[DynamicLibrary] Items/GetItems: No children found for dynamic item {Id}", parentId);
        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = Array.Empty<BaseItemDto>(),
            TotalRecordCount = 0
        });
    }
}
