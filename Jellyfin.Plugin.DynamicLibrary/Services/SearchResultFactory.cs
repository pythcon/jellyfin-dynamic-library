using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Models;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
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
        var config = DynamicLibraryPlugin.Instance?.Configuration;

        // Parse release date
        DateTime? premiereDate = null;
        if (!string.IsNullOrEmpty(movie.ReleaseDate) && DateTime.TryParse(movie.ReleaseDate, out var parsedDate))
        {
            premiereDate = parsedDate;
        }

        // Check if movie is released (for hiding play button on unreleased content)
        var movieIsReleased = premiereDate.HasValue && premiereDate.Value <= DateTime.UtcNow;
        var shouldBePlayable = config?.ShowUnreleasedStreams == true || movieIsReleased;

        _logger.LogDebug("[DynamicLibrary] CreateMovieDto '{Name}': ReleaseDate={Date}, IsReleased={Released}, ShowUnreleasedStreams={ShowUnreleased}, ShouldBePlayable={ShouldPlay}",
            movie.Title, premiereDate, movieIsReleased, config?.ShowUnreleasedStreams, shouldBePlayable);

        var dto = new BaseItemDto
        {
            Id = dynamicUri.ToGuid(),
            ServerId = _serverApplicationHost.SystemId,
            Name = movie.Title,
            OriginalTitle = movie.OriginalTitle,
            Overview = movie.Overview,
            Type = BaseItemKind.Movie,
            MediaType = shouldBePlayable ? Jellyfin.Data.Enums.MediaType.Video : default,
            IsFolder = false,
            ProductionYear = movie.Year,
            PremiereDate = premiereDate,
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

        // Set LocationType.Virtual to hide play button on Android TV for unreleased content
        // Android TV specifically checks LocationType to determine if content can be played
        if (!shouldBePlayable)
        {
            dto.LocationType = LocationType.Virtual;
        }

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

        // Set/update PremiereDate from detailed response
        if (!string.IsNullOrEmpty(details.ReleaseDate) && DateTime.TryParse(details.ReleaseDate, out var releaseDate))
        {
            dto.PremiereDate = releaseDate;
        }

        // Ensure MediaType is correct based on release status (in case it wasn't set during search)
        var config = DynamicLibraryPlugin.Instance?.Configuration;
        var movieIsReleased = dto.PremiereDate.HasValue && dto.PremiereDate.Value <= DateTime.UtcNow;
        var shouldBePlayable = config?.ShowUnreleasedStreams == true || movieIsReleased;
        dto.MediaType = shouldBePlayable ? Jellyfin.Data.Enums.MediaType.Video : default;

        // Set LocationType.Virtual to hide play button on Android TV for unreleased content
        if (!shouldBePlayable)
        {
            dto.LocationType = LocationType.Virtual;
        }

        _logger.LogDebug("[DynamicLibrary] EnrichMovieDto '{Name}': PremiereDate={Date}, IsReleased={Released}, ShouldBePlayable={ShouldPlay}",
            dto.Name, dto.PremiereDate, movieIsReleased, shouldBePlayable);

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

        // Check if this is anime (needed for episode MediaSources and AniList lookup)
        var isAnime = DynamicLibraryService.IsAnime(dto);

        // If this is anime, get AniList ID using cascading lookup strategy:
        // 1. TVDB RemoteIds (most reliable)
        // 2. MAL ID -> AniList lookup (highly reliable)
        // 3. Title + Year search (fallback)
        if (isAnime)
        {
            int? anilistId = null;

            // Strategy 1: Check TVDB RemoteIds for AniList ID (most reliable - direct link)
            if (!string.IsNullOrEmpty(details.AniListId) && int.TryParse(details.AniListId, out var tvdbAnilistId))
            {
                anilistId = tvdbAnilistId;
                _logger.LogInformation("[DynamicLibrary] EnrichSeriesDto: Got AniList ID {AniListId} from TVDB RemoteIds for '{Name}'",
                    anilistId, dto.Name);
            }
            // Strategy 2: Use MAL ID to lookup AniList ID (highly reliable - MAL/AniList have good linking)
            else if (!string.IsNullOrEmpty(details.MalId) && int.TryParse(details.MalId, out var malId))
            {
                anilistId = await _aniListClient.SearchByMalIdAsync(malId, cancellationToken);
                if (anilistId.HasValue)
                {
                    _logger.LogInformation("[DynamicLibrary] EnrichSeriesDto: Got AniList ID {AniListId} via MAL ID {MalId} for '{Name}'",
                        anilistId, malId, dto.Name);
                }
                else
                {
                    _logger.LogDebug("[DynamicLibrary] EnrichSeriesDto: MAL ID {MalId} lookup failed for '{Name}'", malId, dto.Name);
                }
            }
            // Strategy 3: Fall back to improved title search with year matching
            else
            {
                anilistId = await _aniListClient.SearchByTitleAndYearAsync(dto.Name ?? "", dto.ProductionYear, cancellationToken);
                if (anilistId.HasValue)
                {
                    _logger.LogInformation("[DynamicLibrary] EnrichSeriesDto: Got AniList ID {AniListId} via title+year search for '{Name}' ({Year})",
                        anilistId, dto.Name, dto.ProductionYear);
                }
                else
                {
                    _logger.LogDebug("[DynamicLibrary] EnrichSeriesDto: Title+year search failed for '{Name}' ({Year})", dto.Name, dto.ProductionYear);
                }
            }

            // Store AniList ID if found
            if (anilistId.HasValue)
            {
                dto.ProviderIds ??= new Dictionary<string, string>();
                dto.ProviderIds["AniList"] = anilistId.Value.ToString();
            }

            // Also store MAL ID if available from TVDB (useful for other integrations)
            if (!string.IsNullOrEmpty(details.MalId))
            {
                dto.ProviderIds ??= new Dictionary<string, string>();
                dto.ProviderIds["Mal"] = details.MalId;
                _logger.LogDebug("[DynamicLibrary] EnrichSeriesDto: Added MAL ID {MalId} for '{Name}'", details.MalId, dto.Name);
            }
        }

        // Store series in cache BEFORE creating episodes (so episodes can look it up with all provider IDs)
        _itemCache.StoreItem(dto, _itemCache.GetImageUrl(dto.Id));

        // Get series IMDB ID to pass to episodes
        var seriesImdbId = dto.ProviderIds?.TryGetValue("Imdb", out var imdb) == true ? imdb : null;

        // Create seasons and episodes (with translations if language override enabled)
        await CreateSeasonsAndEpisodesAsync(details, dto.Id, dto.Name ?? "Unknown Series", languageCode, isAnime, seriesImdbId, cancellationToken);

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
    public BaseItemDto CreateEpisodeDto(TvdbEpisode episode, Guid seriesId, string seriesName, Guid seasonId, TvdbTranslationData? translation = null, int? absoluteNumber = null, bool isAnime = false, string? seriesImdbId = null)
    {
        var episodeId = GenerateGuid($"tvdb:episode:{episode.Id}");
        var episodeConfig = DynamicLibraryPlugin.Instance?.Configuration;

        // Use translated name/overview if available, otherwise fall back to original
        var displayName = !string.IsNullOrEmpty(translation?.Name)
            ? translation.Name
            : episode.Name ?? $"Episode {episode.Number}";

        var displayOverview = !string.IsNullOrEmpty(translation?.Overview)
            ? translation.Overview
            : episode.Overview;

        // Parse air date first to determine if episode should be playable
        DateTime? premiereDate = null;
        int? productionYear = null;
        if (!string.IsNullOrEmpty(episode.Aired) && DateTime.TryParse(episode.Aired, out var airDate))
        {
            premiereDate = airDate;
            productionYear = airDate.Year;
        }

        // Check if episode is released (for hiding play button on unreleased content)
        var episodeIsReleased = premiereDate.HasValue && premiereDate.Value <= DateTime.UtcNow;
        var shouldBePlayable = episodeConfig?.ShowUnreleasedStreams == true || episodeIsReleased;

        var dto = new BaseItemDto
        {
            Id = episodeId,
            ServerId = _serverApplicationHost.SystemId,
            Name = displayName,
            Overview = displayOverview,
            Type = BaseItemKind.Episode,
            MediaType = shouldBePlayable ? Jellyfin.Data.Enums.MediaType.Video : default,
            IsFolder = false,
            IndexNumber = episode.Number,
            ParentIndexNumber = episode.SeasonNumber,
            ParentId = seasonId,
            SeasonId = seasonId,
            SeriesId = seriesId,
            SeriesName = seriesName,
            PremiereDate = premiereDate,
            ProductionYear = productionYear,
            ProviderIds = BuildEpisodeProviderIds(episode.Id, absoluteNumber, seriesImdbId),
            UserData = new UserItemDataDto
            {
                Key = $"tvdb:episode:{episode.Id}",
                PlaybackPositionTicks = 0,
                PlayCount = 0,
                IsFavorite = false,
                Played = false
            }
        };

        // Set LocationType.Virtual to hide play button on Android TV for unreleased content
        if (!shouldBePlayable)
        {
            dto.LocationType = LocationType.Virtual;
        }

        // Runtime (convert minutes to ticks)
        // Use episode runtime if available, otherwise fall back to series average runtime
        if (episode.Runtime.HasValue && episode.Runtime > 0)
        {
            dto.RunTimeTicks = episode.Runtime.Value * 600_000_000L;
        }
        else
        {
            // Fall back to series average runtime from cache
            var seriesDto = _itemCache.GetItem(seriesId);
            if (seriesDto?.RunTimeTicks > 0)
            {
                dto.RunTimeTicks = seriesDto.RunTimeTicks.Value;
            }
        }

        // Set image if available
        string? episodeImageUrl = null;
        if (!string.IsNullOrEmpty(episode.Image))
        {
            dto.ImageTags = new Dictionary<ImageType, string>
            {
                { ImageType.Primary, "dynamic" }
            };
            dto.PrimaryImageAspectRatio = 1.78; // 16:9 for episode thumbnails
            episodeImageUrl = GetFullTvdbImageUrl(episode.Image);
        }

        // Add MediaSources for anime with sub/dub version selector (only if episode is playable)
        _logger.LogDebug("[DynamicLibrary] CreateEpisodeDto '{Name}': isAnime={IsAnime}, EnableAudioVersions={EnableAudio}, StreamProvider={Provider}, IsReleased={Released}, ShouldBePlayable={Playable}",
            dto.Name, isAnime, episodeConfig?.EnableAnimeAudioVersions, episodeConfig?.StreamProvider, episodeIsReleased, shouldBePlayable);

        if (isAnime && episodeConfig?.EnableAnimeAudioVersions == true && episodeConfig.StreamProvider == StreamProvider.Direct && shouldBePlayable)
        {
            var audioTracks = episodeConfig.AnimeAudioTracks?
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? new[] { "sub", "dub" };

            if (audioTracks.Length > 0)
            {
                // Get series from cache to access provider IDs for URL building
                var series = _itemCache.GetItem(seriesId);
                var season = dto.ParentIndexNumber ?? 1;
                var episodeNum = dto.IndexNumber ?? 1;

                dto.MediaSources = audioTracks.Select(track =>
                {
                    // Build the actual stream URL for this audio track
                    var streamUrl = BuildAnimeStreamUrl(episodeConfig.DirectAnimeUrlTemplate, dto, series, season, episodeNum, track);

                    // Generate a valid GUID for the MediaSource ID (client expects GUID format)
                    var sourceId = GenerateMediaSourceGuid(dto.Id, track);

                    // Store mapping so we can resolve MediaSource ID -> Episode ID later
                    _itemCache.StoreMediaSourceMapping(sourceId, dto.Id);

                    _logger.LogDebug("[DynamicLibrary] Built stream URL for '{Name}' ({Track}): {Url}, SourceId={SourceId}",
                        dto.Name, track, streamUrl, sourceId);

                    return new MediaSourceInfo
                    {
                        Id = sourceId,
                        Name = track.ToUpperInvariant(),  // "SUB", "DUB" shown in version selector
                        Path = streamUrl,  // Actual stream URL - client plays this directly
                        Protocol = MediaProtocol.Http,
                        Type = MediaSourceType.Default,
                        Container = "hls",
                        IsRemote = true,
                        SupportsDirectPlay = true,  // Client can play this URL directly
                        SupportsDirectStream = false,
                        SupportsTranscoding = false,
                        SupportsProbing = false,
                        RunTimeTicks = dto.RunTimeTicks,
                        MediaStreams = new List<MediaStream>()
                    };
                }).ToArray();

                _logger.LogDebug("[DynamicLibrary] Added {Count} MediaSources to episode '{Name}': {Tracks}",
                    audioTracks.Length, dto.Name, string.Join(", ", audioTracks));
            }
        }
        else
        {
            _logger.LogDebug("[DynamicLibrary] NOT adding MediaSources to episode '{Name}' - conditions not met", dto.Name);
        }

        // Store in cache AFTER MediaSources are added
        _itemCache.StoreItem(dto, episodeImageUrl);

        return dto;
    }

    /// <summary>
    /// Build the provider IDs dictionary for an episode, including the series IMDB ID for streaming lookups.
    /// </summary>
    private Dictionary<string, string> BuildEpisodeProviderIds(int tvdbEpisodeId, int? absoluteNumber, string? seriesImdbId)
    {
        var providerIds = new Dictionary<string, string>
        {
            { "Tvdb", tvdbEpisodeId.ToString() },
            { DynamicLibraryProviderId, $"episode:{tvdbEpisodeId}" },
            { "AbsoluteNumber", absoluteNumber?.ToString() ?? "" }
        };

        // Store series IMDB ID in episode for streaming lookups (AIOStreams, Embedarr, etc.)
        // This ensures the series IMDB is available even if the series isn't in cache
        if (!string.IsNullOrEmpty(seriesImdbId))
        {
            providerIds["SeriesImdb"] = seriesImdbId;
        }

        return providerIds;
    }

    /// <summary>
    /// Generate a deterministic GUID for a MediaSource based on episode ID and audio track.
    /// This ensures the client gets a valid GUID format for the MediaSource ID.
    /// </summary>
    private static string GenerateMediaSourceGuid(Guid episodeId, string audioTrack)
    {
        var input = $"{episodeId:N}:{audioTrack.ToLowerInvariant()}";
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString("N");
    }

    /// <summary>
    /// Build the stream URL for an anime episode using the DirectAnimeUrlTemplate.
    /// </summary>
    private string BuildAnimeStreamUrl(string template, BaseItemDto episode, BaseItemDto? series, int season, int episodeNum, string audioTrack)
    {
        if (string.IsNullOrEmpty(template))
        {
            _logger.LogInformation("[DynamicLibrary] DirectAnimeUrlTemplate is empty");
            return string.Empty;
        }

        var episodeProviderIds = episode.ProviderIds ?? new Dictionary<string, string>();
        var seriesProviderIds = series?.ProviderIds ?? episodeProviderIds;

        // Get absolute episode number if available
        var absoluteEpisode = episodeProviderIds.GetValueOrDefault("AbsoluteNumber", "");

        // Build URL by replacing placeholders
        var url = template
            .Replace("{imdb}", seriesProviderIds.GetValueOrDefault("Imdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tvdb}", seriesProviderIds.GetValueOrDefault("Tvdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{anilist}", seriesProviderIds.GetValueOrDefault("AniList", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tmdb}", seriesProviderIds.GetValueOrDefault("Tmdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", season.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{episode}", episodeNum.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{absolute}", absoluteEpisode, StringComparison.OrdinalIgnoreCase)
            .Replace("{audio}", audioTrack.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", Uri.EscapeDataString(episode.Name ?? ""), StringComparison.OrdinalIgnoreCase);

        // Handle {id} placeholder - use preferred ID based on config
        var config = DynamicLibraryPlugin.Instance?.Configuration;
        var preferredId = config?.AnimePreferredId ?? PreferredProviderId.Imdb;
        var idValue = preferredId switch
        {
            PreferredProviderId.Imdb => seriesProviderIds.GetValueOrDefault("Imdb", ""),
            PreferredProviderId.Tvdb => seriesProviderIds.GetValueOrDefault("Tvdb", ""),
            PreferredProviderId.AniList => seriesProviderIds.GetValueOrDefault("AniList", ""),
            _ => seriesProviderIds.GetValueOrDefault("Imdb", "")
        };
        url = url.Replace("{id}", idValue, StringComparison.OrdinalIgnoreCase);

        return url;
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
        bool isAnime = false,
        string? seriesImdbId = null,
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

                var episodeDto = CreateEpisodeDto(episode, seriesId, seriesName, seasonId, translation, absoluteNumber, isAnime, seriesImdbId);
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
