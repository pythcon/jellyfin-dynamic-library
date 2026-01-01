using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Models;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using ImageType = MediaBrowser.Model.Entities.ImageType;

namespace Jellyfin.Plugin.DynamicLibrary.Services;

public class SearchResultFactory
{
    private readonly ITmdbClient _tmdbClient;
    private readonly ITvdbClient _tvdbClient;
    private readonly DynamicItemCache _itemCache;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly ILogger<SearchResultFactory> _logger;

    private const string DynamicLibraryProviderId = "DynamicLibrary";

    public SearchResultFactory(
        ITmdbClient tmdbClient,
        ITvdbClient tvdbClient,
        DynamicItemCache itemCache,
        IServerApplicationHost serverApplicationHost,
        ILogger<SearchResultFactory> logger)
    {
        _tmdbClient = tmdbClient;
        _tvdbClient = tvdbClient;
        _itemCache = itemCache;
        _serverApplicationHost = serverApplicationHost;
        _logger = logger;
    }

    /// <summary>
    /// Convert a TMDB movie search result to a basic Jellyfin BaseItemDto.
    /// Full details are fetched later when the user clicks on the item.
    /// </summary>
    public async Task<BaseItemDto> CreateMovieDtoAsync(TmdbMovieResult movie, CancellationToken cancellationToken = default)
    {
        var dynamicUri = DynamicUri.FromTmdb(movie.Id);
        var imageBaseUrl = await _tmdbClient.GetImageBaseUrlAsync(cancellationToken);

        var dto = new BaseItemDto
        {
            Id = dynamicUri.ToGuid(),
            ServerId = _serverApplicationHost.SystemId,
            Name = movie.Title,
            OriginalTitle = movie.OriginalTitle,
            Overview = movie.Overview,
            Type = BaseItemKind.Movie,
            MediaType = Jellyfin.Data.Enums.MediaType.Video,
            IsFolder = false,
            ProductionYear = movie.Year,
            CommunityRating = (float)movie.VoteAverage,
            ProviderIds = new Dictionary<string, string>
            {
                { "Tmdb", movie.Id.ToString() },
                { DynamicLibraryProviderId, dynamicUri.ToProviderIdValue() }
            },
            UserData = new UserItemDataDto
            {
                Key = dynamicUri.ToString(),
                PlaybackPositionTicks = 0,
                PlayCount = 0,
                IsFavorite = false,
                Played = false
            }
        };

        // Build image URL and set ImageTags so client knows to request images
        string? imageUrl = null;
        if (!string.IsNullOrEmpty(movie.PosterPath))
        {
            imageUrl = $"{imageBaseUrl}w500{movie.PosterPath}";
            dto.ImageTags = new Dictionary<ImageType, string>
            {
                { ImageType.Primary, "dynamic" }
            };
            dto.PrimaryImageAspectRatio = 0.667; // Standard movie poster ratio (2:3)
        }

        // Store in cache for later retrieval (will be enriched when user clicks)
        _itemCache.StoreItem(dto, imageUrl);

        return dto;
    }

    /// <summary>
    /// Enrich a movie DTO with full details from TMDB (called when user views item details).
    /// </summary>
    public async Task<BaseItemDto> EnrichMovieDtoAsync(BaseItemDto dto, CancellationToken cancellationToken = default)
    {
        // Get TMDB ID from provider IDs
        if (dto.ProviderIds?.TryGetValue("Tmdb", out var tmdbIdStr) != true ||
            !int.TryParse(tmdbIdStr, out var tmdbId))
        {
            return dto;
        }

        var details = await _tmdbClient.GetMovieDetailsAsync(tmdbId, cancellationToken);
        if (details == null)
        {
            return dto;
        }

        var imageBaseUrl = await _tmdbClient.GetImageBaseUrlAsync(cancellationToken);

        // Add IMDb ID if available
        if (!string.IsNullOrEmpty(details.ImdbId))
        {
            dto.ProviderIds ??= new Dictionary<string, string>();
            dto.ProviderIds["Imdb"] = details.ImdbId;
        }

        // Runtime (convert minutes to ticks: 1 minute = 600,000,000 ticks)
        if (details.Runtime.HasValue && details.Runtime > 0)
        {
            dto.RunTimeTicks = details.Runtime.Value * 600_000_000L;
        }

        // Tagline
        if (!string.IsNullOrEmpty(details.Tagline))
        {
            dto.Taglines = new[] { details.Tagline };
        }

        // Genres
        if (details.Genres.Count > 0)
        {
            dto.Genres = details.Genres.Select(g => g.Name).ToArray();
            dto.GenreItems = details.Genres.Select(g => new NameGuidPair
            {
                Name = g.Name,
                Id = Guid.Empty
            }).ToArray();
        }

        // Studios (production companies)
        if (details.ProductionCompanies.Count > 0)
        {
            dto.Studios = details.ProductionCompanies.Select(c => new NameGuidPair
            {
                Name = c.Name,
                Id = Guid.Empty
            }).ToArray();
        }

        // Cast and Crew
        if (details.Credits != null)
        {
            var people = new List<BaseItemPerson>();

            // Add cast (actors)
            foreach (var cast in details.Credits.Cast.Take(20))
            {
                people.Add(new BaseItemPerson
                {
                    Name = cast.Name,
                    Role = cast.Character,
                    Type = PersonKind.Actor
                });
            }

            // Add directors
            foreach (var crew in details.Credits.Crew.Where(c => c.Job == "Director"))
            {
                people.Add(new BaseItemPerson
                {
                    Name = crew.Name,
                    Type = PersonKind.Director
                });
            }

            // Add writers
            foreach (var crew in details.Credits.Crew.Where(c => c.Job == "Writer" || c.Job == "Screenplay"))
            {
                people.Add(new BaseItemPerson
                {
                    Name = crew.Name,
                    Type = PersonKind.Writer
                });
            }

            // Add producers (limit to 5)
            foreach (var crew in details.Credits.Crew.Where(c => c.Job == "Producer").Take(5))
            {
                people.Add(new BaseItemPerson
                {
                    Name = crew.Name,
                    Type = PersonKind.Producer
                });
            }

            dto.People = people.ToArray();
        }

        // Update cache with enriched data
        string? imageUrl = null;
        if (!string.IsNullOrEmpty(details.PosterPath))
        {
            imageUrl = $"{imageBaseUrl}w500{details.PosterPath}";
        }
        _itemCache.StoreItem(dto, imageUrl);

        return dto;
    }

    /// <summary>
    /// Enrich a series DTO with full details from TVDB (called when user views item details).
    /// </summary>
    public async Task<BaseItemDto> EnrichSeriesDtoAsync(BaseItemDto dto, CancellationToken cancellationToken = default)
    {
        // Get TVDB ID from provider IDs
        if (dto.ProviderIds?.TryGetValue("Tvdb", out var tvdbIdStr) != true ||
            !int.TryParse(tvdbIdStr, out var tvdbId))
        {
            return dto;
        }

        var details = await _tvdbClient.GetSeriesExtendedAsync(tvdbId, cancellationToken);
        if (details == null)
        {
            return dto;
        }

        // Update overview if we have a better one from extended data
        if (!string.IsNullOrEmpty(details.Overview))
        {
            dto.Overview = details.Overview;
        }

        // Add IMDb ID if available
        if (!string.IsNullOrEmpty(details.ImdbId))
        {
            dto.ProviderIds ??= new Dictionary<string, string>();
            dto.ProviderIds["Imdb"] = details.ImdbId;
        }

        // Average runtime (convert minutes to ticks: 1 minute = 600,000,000 ticks)
        if (details.AverageRuntime.HasValue && details.AverageRuntime > 0)
        {
            dto.RunTimeTicks = details.AverageRuntime.Value * 600_000_000L;
        }

        // Status
        if (details.Status != null)
        {
            dto.Status = details.Status.Name;
        }

        // Genres
        if (details.Genres?.Count > 0)
        {
            dto.Genres = details.Genres.Select(g => g.Name).ToArray();
            dto.GenreItems = details.Genres.Select(g => new NameGuidPair
            {
                Name = g.Name,
                Id = Guid.Empty
            }).ToArray();
        }

        // Studios (network)
        if (details.OriginalNetwork != null)
        {
            dto.Studios = new[]
            {
                new NameGuidPair
                {
                    Name = details.OriginalNetwork.Name,
                    Id = Guid.Empty
                }
            };
        }

        // Season count
        if (details.Seasons?.Count > 0)
        {
            // Filter out specials (season 0) for the count
            var regularSeasons = details.Seasons.Where(s => s.Number > 0).ToList();
            dto.ChildCount = regularSeasons.Count;
        }

        // Create seasons and episodes
        CreateSeasonsAndEpisodes(details, dto.Id, dto.Name ?? "Unknown Series");

        // Update cache with enriched data
        _itemCache.StoreItem(dto, _itemCache.GetImageUrl(dto.Id));

        return dto;
    }

    /// <summary>
    /// Convert a TVDB search result to a Jellyfin BaseItemDto.
    /// </summary>
    public BaseItemDto CreateSeriesDto(TvdbSearchResult series)
    {
        var dynamicUri = DynamicUri.FromTvdb(series.TvdbIdInt);
        var preferredLang = DynamicLibraryPlugin.Instance?.Configuration.PreferredLanguage ?? "eng";

        var dto = new BaseItemDto
        {
            Id = dynamicUri.ToGuid(),
            ServerId = _serverApplicationHost.SystemId,
            Name = series.Name,
            Overview = series.GetLocalizedOverview(preferredLang),
            Type = BaseItemKind.Series,
            IsFolder = true,
            ProviderIds = new Dictionary<string, string>
            {
                { "Tvdb", series.TvdbId },
                { DynamicLibraryProviderId, dynamicUri.ToProviderIdValue() }
            },
            UserData = new UserItemDataDto
            {
                Key = dynamicUri.ToString(),
                PlaybackPositionTicks = 0,
                PlayCount = 0,
                IsFavorite = false,
                Played = false,
                UnplayedItemCount = 0
            }
        };

        // Parse year
        if (int.TryParse(series.Year, out var year))
        {
            dto.ProductionYear = year;
        }
        else if (!string.IsNullOrEmpty(series.FirstAirTime) && series.FirstAirTime.Length >= 4)
        {
            if (int.TryParse(series.FirstAirTime[..4], out var firstAirYear))
            {
                dto.ProductionYear = firstAirYear;
            }
        }

        // Set status if available
        if (!string.IsNullOrEmpty(series.Status))
        {
            dto.Status = series.Status;
        }

        // Set ImageTags so client knows Primary image exists
        if (!string.IsNullOrEmpty(series.ImageUrl))
        {
            dto.ImageTags = new Dictionary<ImageType, string>
            {
                { ImageType.Primary, "dynamic" }
            };
            dto.PrimaryImageAspectRatio = 0.667; // Standard poster ratio
        }

        // Store image URL in cache for ImageFilter to proxy
        _itemCache.StoreItem(dto, series.ImageUrl);

        return dto;
    }

    /// <summary>
    /// Check if a DTO is a dynamic (virtual) item.
    /// </summary>
    public static bool IsDynamicItem(BaseItemDto dto)
    {
        return dto.ProviderIds?.ContainsKey(DynamicLibraryProviderId) == true;
    }

    /// <summary>
    /// Get the DynamicUri from a DTO if it's a dynamic item.
    /// </summary>
    public static DynamicUri? GetDynamicUri(BaseItemDto dto)
    {
        if (dto.ProviderIds?.TryGetValue(DynamicLibraryProviderId, out var value) == true)
        {
            return DynamicUri.FromProviderIdValue(value);
        }
        return null;
    }

    /// <summary>
    /// Generate a stable GUID from a string key.
    /// </summary>
    private static Guid GenerateGuid(string key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    /// <summary>
    /// Create a Season DTO from TVDB season data.
    /// </summary>
    public BaseItemDto CreateSeasonDto(TvdbSeason season, Guid seriesId, string seriesName)
    {
        var seasonId = GenerateGuid($"tvdb:season:{season.Id}");

        var dto = new BaseItemDto
        {
            Id = seasonId,
            ServerId = _serverApplicationHost.SystemId,
            Name = season.Name ?? $"Season {season.Number}",
            Type = BaseItemKind.Season,
            IsFolder = true,
            IndexNumber = season.Number,
            ParentId = seriesId,
            SeriesId = seriesId,
            SeriesName = seriesName,
            ProviderIds = new Dictionary<string, string>
            {
                { "Tvdb", season.Id.ToString() },
                { DynamicLibraryProviderId, $"season:{season.Id}" }
            },
            UserData = new UserItemDataDto
            {
                Key = $"tvdb:season:{season.Id}",
                PlaybackPositionTicks = 0,
                PlayCount = 0,
                IsFavorite = false,
                Played = false,
                UnplayedItemCount = 0
            }
        };

        // Set image if available
        if (!string.IsNullOrEmpty(season.Image))
        {
            dto.ImageTags = new Dictionary<ImageType, string>
            {
                { ImageType.Primary, "dynamic" }
            };
            dto.PrimaryImageAspectRatio = 0.667;
            _itemCache.StoreItem(dto, season.Image);
        }
        else
        {
            _itemCache.StoreItem(dto);
        }

        return dto;
    }

    /// <summary>
    /// Create an Episode DTO from TVDB episode data.
    /// </summary>
    public BaseItemDto CreateEpisodeDto(TvdbEpisode episode, Guid seriesId, string seriesName, Guid seasonId)
    {
        var episodeId = GenerateGuid($"tvdb:episode:{episode.Id}");

        var dto = new BaseItemDto
        {
            Id = episodeId,
            ServerId = _serverApplicationHost.SystemId,
            Name = episode.Name ?? $"Episode {episode.Number}",
            Overview = episode.Overview,
            Type = BaseItemKind.Episode,
            MediaType = Jellyfin.Data.Enums.MediaType.Video,
            IsFolder = false,
            IndexNumber = episode.Number,
            ParentIndexNumber = episode.SeasonNumber,
            ParentId = seasonId,
            SeasonId = seasonId,
            SeriesId = seriesId,
            SeriesName = seriesName,
            ProviderIds = new Dictionary<string, string>
            {
                { "Tvdb", episode.Id.ToString() },
                { DynamicLibraryProviderId, $"episode:{episode.Id}" }
            },
            UserData = new UserItemDataDto
            {
                Key = $"tvdb:episode:{episode.Id}",
                PlaybackPositionTicks = 0,
                PlayCount = 0,
                IsFavorite = false,
                Played = false
            }
        };

        // Parse air date
        if (!string.IsNullOrEmpty(episode.Aired) && DateTime.TryParse(episode.Aired, out var airDate))
        {
            dto.PremiereDate = airDate;
            dto.ProductionYear = airDate.Year;
        }

        // Runtime (convert minutes to ticks)
        if (episode.Runtime.HasValue && episode.Runtime > 0)
        {
            dto.RunTimeTicks = episode.Runtime.Value * 600_000_000L;
        }

        // Set image if available
        if (!string.IsNullOrEmpty(episode.Image))
        {
            dto.ImageTags = new Dictionary<ImageType, string>
            {
                { ImageType.Primary, "dynamic" }
            };
            dto.PrimaryImageAspectRatio = 1.78; // 16:9 for episode thumbnails
            _itemCache.StoreItem(dto, episode.Image);
        }
        else
        {
            _itemCache.StoreItem(dto);
        }

        return dto;
    }

    /// <summary>
    /// Create all season and episode DTOs for a series from TVDB extended data.
    /// </summary>
    public void CreateSeasonsAndEpisodes(TvdbSeriesExtended details, Guid seriesId, string seriesName)
    {
        if (details.Seasons == null || details.Seasons.Count == 0)
        {
            return;
        }

        // Filter to only "official" (aired order) seasons - TVDB returns multiple season types
        // Type "official" is the standard aired order
        var officialSeasons = details.Seasons
            .Where(s => s.Type?.Type?.Equals("official", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();

        // If no official seasons found, fall back to all seasons but dedupe by season number
        if (officialSeasons.Count == 0)
        {
            officialSeasons = details.Seasons
                .GroupBy(s => s.Number)
                .Select(g => g.First())
                .ToList();
        }

        // Create season DTOs
        var seasonDtos = new List<BaseItemDto>();
        var seasonIdMap = new Dictionary<int, Guid>(); // seasonNumber -> seasonId

        foreach (var season in officialSeasons.OrderBy(s => s.Number))
        {
            var seasonDto = CreateSeasonDto(season, seriesId, seriesName);
            seasonDtos.Add(seasonDto);
            seasonIdMap[season.Number] = seasonDto.Id;
        }

        // Update episode counts for each season
        if (details.Episodes != null)
        {
            var episodesBySeason = details.Episodes.GroupBy(e => e.SeasonNumber);
            foreach (var group in episodesBySeason)
            {
                var seasonDto = seasonDtos.FirstOrDefault(s => s.IndexNumber == group.Key);
                if (seasonDto != null)
                {
                    seasonDto.ChildCount = group.Count();
                }
            }
        }

        _itemCache.StoreSeasonsForSeries(seriesId, seasonDtos);

        // Create episode DTOs
        if (details.Episodes != null && details.Episodes.Count > 0)
        {
            var episodeDtos = new List<BaseItemDto>();

            foreach (var episode in details.Episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number))
            {
                // Get the season ID for this episode
                if (!seasonIdMap.TryGetValue(episode.SeasonNumber, out var seasonId))
                {
                    // Create a placeholder season if needed
                    seasonId = GenerateGuid($"tvdb:season:{details.Id}:{episode.SeasonNumber}");
                }

                var episodeDto = CreateEpisodeDto(episode, seriesId, seriesName, seasonId);
                episodeDtos.Add(episodeDto);
            }

            _itemCache.StoreEpisodesForSeries(seriesId, episodeDtos);
        }

        _logger.LogDebug("[DynamicLibrary] Created {SeasonCount} seasons and {EpisodeCount} episodes for {SeriesName}",
            seasonDtos.Count, details.Episodes?.Count ?? 0, seriesName);
    }
}
