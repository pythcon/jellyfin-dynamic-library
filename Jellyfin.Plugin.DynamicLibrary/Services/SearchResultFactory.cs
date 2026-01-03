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
    private readonly AniListClient _aniListClient;
    private readonly DynamicItemCache _itemCache;
    private readonly IServerApplicationHost _serverApplicationHost;
    private readonly ILogger<SearchResultFactory> _logger;

    private const string DynamicLibraryProviderId = "DynamicLibrary";
    private const string TvdbArtworkBaseUrl = "https://artworks.thetvdb.com";

    public SearchResultFactory(
        ITmdbClient tmdbClient,
        ITvdbClient tvdbClient,
        AniListClient aniListClient,
        DynamicItemCache itemCache,
        IServerApplicationHost serverApplicationHost,
        ILogger<SearchResultFactory> logger)
    {
        _tmdbClient = tmdbClient;
        _tvdbClient = tvdbClient;
        _aniListClient = aniListClient;
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

        // Get language settings
        var config = DynamicLibraryPlugin.Instance?.Configuration;
        var languageCode = config?.GetTvdbLanguageCode();

        _logger.LogInformation("[DynamicLibrary] EnrichSeriesDto: {Name}, LanguageCode={Lang}", details.Name, languageCode ?? "null");
        _logger.LogInformation("[DynamicLibrary] EnrichSeriesDto: AvailableTranslations={Langs}",
            details.NameTranslations != null ? string.Join(",", details.NameTranslations) : "none");

        // Fetch translation if language override is enabled and translation is available
        string? localizedName = null;
        string? localizedOverview = null;

        if (!string.IsNullOrEmpty(languageCode) && details.HasTranslation(languageCode))
        {
            var translation = await _tvdbClient.GetSeriesTranslationAsync(details.Id, languageCode, cancellationToken);
            if (translation != null)
            {
                _logger.LogInformation("[DynamicLibrary] EnrichSeriesDto: Got translation - Name={Name}", translation.Name);
                localizedName = translation.Name;
                localizedOverview = translation.Overview;
            }
        }

        // Update name with localized version if available
        if (!string.IsNullOrEmpty(localizedName))
        {
            dto.Name = localizedName;
            // Store original name if different
            if (localizedName != details.Name)
            {
                dto.OriginalTitle = details.Name;
            }
        }

        // Update overview with localized version if available
        if (!string.IsNullOrEmpty(localizedOverview))
        {
            dto.Overview = localizedOverview;
        }
        else if (!string.IsNullOrEmpty(details.Overview))
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

        // Create seasons and episodes (with translations if language override enabled)
        await CreateSeasonsAndEpisodesAsync(details, dto.Id, dto.Name ?? "Unknown Series", languageCode, cancellationToken);

        // If this is anime, query AniList for the AniList ID
        if (DynamicLibraryService.IsAnime(dto))
        {
            var anilistId = await _aniListClient.SearchByTitleAsync(dto.Name ?? "", cancellationToken);
            if (anilistId.HasValue)
            {
                dto.ProviderIds ??= new Dictionary<string, string>();
                dto.ProviderIds["AniList"] = anilistId.Value.ToString();
                _logger.LogInformation("[DynamicLibrary] EnrichSeriesDto: Added AniList ID {AniListId} for anime '{Name}'",
                    anilistId.Value, dto.Name);
            }
            else
            {
                _logger.LogDebug("[DynamicLibrary] EnrichSeriesDto: No AniList ID found for anime '{Name}'", dto.Name);
            }
        }

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
        var config = DynamicLibraryPlugin.Instance?.Configuration;
        var languageCode = config?.GetTvdbLanguageCode(); // Returns language code if Override mode, null otherwise

        _logger.LogInformation("[DynamicLibrary] CreateSeriesDto: Name={Name}, LanguageMode={Mode}, PreferredLang={PrefLang}, EffectiveLangCode={Lang}",
            series.Name, config?.LanguageMode, config?.PreferredLanguage, languageCode ?? "null");
        _logger.LogInformation("[DynamicLibrary] CreateSeriesDto: HasTranslations={HasTrans}, TranslationKeys={Keys}",
            series.Translations?.Count ?? 0, series.Translations != null ? string.Join(",", series.Translations.Keys) : "none");

        // Get localized name and overview using the configured language
        var displayName = series.GetLocalizedName(languageCode);
        var displayOverview = series.GetLocalizedOverview(languageCode);

        _logger.LogInformation("[DynamicLibrary] CreateSeriesDto result: DisplayName={DisplayName}, OriginalName={OriginalName}",
            displayName, series.Name);

        var dto = new BaseItemDto
        {
            Id = dynamicUri.ToGuid(),
            ServerId = _serverApplicationHost.SystemId,
            Name = displayName,
            OriginalTitle = displayName != series.Name ? series.Name : null,
            Overview = displayOverview,
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
    /// Uses a unique prefix to avoid collisions with real Jellyfin items.
    /// </summary>
    private static Guid GenerateGuid(string key)
    {
        // Add unique prefix to avoid GUID collisions with real Jellyfin items
        const string UniquePrefix = "jellyfin-dynamiclibrary-plugin:";
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(UniquePrefix + key));
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
            // Ensure full URL for TVDB images
            var imageUrl = GetFullTvdbImageUrl(season.Image);
            _itemCache.StoreItem(dto, imageUrl);
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
    public BaseItemDto CreateEpisodeDto(TvdbEpisode episode, Guid seriesId, string seriesName, Guid seasonId, TvdbTranslationData? translation = null, int? absoluteNumber = null)
    {
        var episodeId = GenerateGuid($"tvdb:episode:{episode.Id}");

        // Use translated name/overview if available, otherwise fall back to original
        var displayName = !string.IsNullOrEmpty(translation?.Name)
            ? translation.Name
            : episode.Name ?? $"Episode {episode.Number}";

        var displayOverview = !string.IsNullOrEmpty(translation?.Overview)
            ? translation.Overview
            : episode.Overview;

        var dto = new BaseItemDto
        {
            Id = episodeId,
            ServerId = _serverApplicationHost.SystemId,
            Name = displayName,
            Overview = displayOverview,
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
                { DynamicLibraryProviderId, $"episode:{episode.Id}" },
                { "AbsoluteNumber", absoluteNumber?.ToString() ?? "" }
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
            // Ensure full URL for TVDB images
            var imageUrl = GetFullTvdbImageUrl(episode.Image);
            _itemCache.StoreItem(dto, imageUrl);
        }
        else
        {
            _itemCache.StoreItem(dto);
        }

        return dto;
    }

    /// <summary>
    /// Convert a potentially relative TVDB image path to a full URL.
    /// </summary>
    private static string GetFullTvdbImageUrl(string imagePath)
    {
        if (string.IsNullOrEmpty(imagePath))
        {
            return imagePath;
        }

        // If it's already a full URL, return as-is
        if (imagePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            imagePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return imagePath;
        }

        // Prepend the TVDB artwork base URL
        return $"{TvdbArtworkBaseUrl}{imagePath}";
    }

    /// <summary>
    /// Create all season and episode DTOs for a series from TVDB extended data.
    /// Fetches episode translations if language override is enabled.
    /// </summary>
    public async Task CreateSeasonsAndEpisodesAsync(
        TvdbSeriesExtended details,
        Guid seriesId,
        string seriesName,
        string? languageCode,
        CancellationToken cancellationToken = default)
    {
        if (details.Seasons == null || details.Seasons.Count == 0)
        {
            return;
        }

        // Filter to only "official" (aired order) seasons - TVDB returns multiple season types
        // Type "official" is the standard aired order
        // Also exclude Season 0 (Specials) as they are not supported
        var officialSeasons = details.Seasons
            .Where(s => s.Type?.Type?.Equals("official", StringComparison.OrdinalIgnoreCase) == true)
            .Where(s => s.Number > 0) // Exclude specials (Season 0)
            .ToList();

        // If no official seasons found, fall back to all seasons but dedupe by season number
        if (officialSeasons.Count == 0)
        {
            officialSeasons = details.Seasons
                .Where(s => s.Number > 0) // Exclude specials (Season 0)
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

        // Update episode counts for each season (excluding Season 0)
        if (details.Episodes != null)
        {
            var episodesBySeason = details.Episodes
                .Where(e => e.SeasonNumber > 0) // Exclude specials
                .GroupBy(e => e.SeasonNumber);
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

        // Create episode DTOs (excluding Season 0 specials)
        if (details.Episodes != null && details.Episodes.Count > 0)
        {
            var episodes = details.Episodes
                .Where(e => e.SeasonNumber > 0) // Exclude specials (Season 0)
                .OrderBy(e => e.SeasonNumber)
                .ThenBy(e => e.Number)
                .ToList();

            // Build episode count per season for absolute number fallback calculation
            var episodeCountBySeason = episodes
                .GroupBy(e => e.SeasonNumber)
                .ToDictionary(g => g.Key, g => g.Count());

            // Fetch episode translations in parallel if language override is enabled
            var episodeTranslations = new Dictionary<int, TvdbTranslationData>();
            if (!string.IsNullOrEmpty(languageCode))
            {
                _logger.LogDebug("[DynamicLibrary] Fetching translations for {Count} episodes in language {Lang}",
                    episodes.Count, languageCode);

                var translationTasks = episodes.Select(async ep =>
                {
                    var translation = await _tvdbClient.GetEpisodeTranslationAsync(ep.Id, languageCode, cancellationToken);
                    return (ep.Id, translation);
                });

                var results = await Task.WhenAll(translationTasks);
                foreach (var (episodeId, translation) in results)
                {
                    if (translation != null)
                    {
                        episodeTranslations[episodeId] = translation;
                    }
                }

                _logger.LogDebug("[DynamicLibrary] Got translations for {Count}/{Total} episodes",
                    episodeTranslations.Count, episodes.Count);
            }

            var episodeDtos = new List<BaseItemDto>();

            foreach (var episode in episodes)
            {
                // Get the season ID for this episode
                if (!seasonIdMap.TryGetValue(episode.SeasonNumber, out var seasonId))
                {
                    // Create a placeholder season if needed
                    seasonId = GenerateGuid($"tvdb:season:{details.Id}:{episode.SeasonNumber}");
                }

                // Get translation if available
                episodeTranslations.TryGetValue(episode.Id, out var translation);

                // Calculate absolute episode number: use TVDB value if available, otherwise calculate from season/episode
                int absoluteNumber = episode.AbsoluteNumber
                    ?? (GetCumulativeEpisodeCount(episodeCountBySeason, episode.SeasonNumber) + episode.Number);

                var episodeDto = CreateEpisodeDto(episode, seriesId, seriesName, seasonId, translation, absoluteNumber);
                episodeDtos.Add(episodeDto);
            }

            _itemCache.StoreEpisodesForSeries(seriesId, episodeDtos);
        }

        _logger.LogDebug("[DynamicLibrary] Created {SeasonCount} seasons and {EpisodeCount} episodes for {SeriesName}",
            seasonDtos.Count, details.Episodes?.Count ?? 0, seriesName);
    }

    /// <summary>
    /// Calculate the cumulative episode count from all seasons before the given season.
    /// Used for calculating absolute episode numbers when TVDB doesn't provide them.
    /// </summary>
    private static int GetCumulativeEpisodeCount(Dictionary<int, int> episodeCountBySeason, int currentSeason)
    {
        return episodeCountBySeason
            .Where(kvp => kvp.Key < currentSeason)
            .Sum(kvp => kvp.Value);
    }
}
