using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Filters;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DynamicLibrary;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        // Register memory cache if not already registered
        services.AddMemoryCache();

        // Register HttpClient for image proxying
        services.AddHttpClient();

        // Register HttpContextAccessor for accessing request context
        services.AddHttpContextAccessor();

        // Register API clients
        services.AddSingleton<ITvdbClient, TvdbClient>();
        services.AddSingleton<ITmdbClient, TmdbClient>();
        services.AddSingleton<IEmbedarrClient, EmbedarrClient>();
        services.AddSingleton<AniListClient>();
        services.AddSingleton<IOpenSubtitlesClient, OpenSubtitlesClient>();

        // Register caches and services
        services.AddSingleton<DynamicItemCache>();
        services.AddSingleton<SearchResultFactory>();
        services.AddSingleton<DynamicLibraryService>();
        services.AddSingleton<SubtitleService>();

        // Register filters
        services.AddSingleton<RequestLoggerFilter>();
        services.AddSingleton<SearchActionFilter>();
        services.AddSingleton<ItemLookupFilter>();
        services.AddSingleton<ImageFilter>();
        services.AddSingleton<DynamicItemEndpointsFilter>();
        services.AddSingleton<SeasonEpisodeFilter>();
        services.AddSingleton<PlaybackInfoFilter>();
        services.AddSingleton<SubtitleFilter>();

        // Register filters with MVC
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            options.Filters.AddService<RequestLoggerFilter>(order: -100);       // Log ALL requests first (debug)
            options.Filters.AddService<ImageFilter>(order: 0);                  // Run first for images
            options.Filters.AddService<ItemLookupFilter>(order: 0);             // Run first for item lookups
            options.Filters.AddService<DynamicItemEndpointsFilter>(order: 0);   // Handle secondary endpoints
            options.Filters.AddService<SeasonEpisodeFilter>(order: 0);          // Handle seasons/episodes
            options.Filters.AddService<PlaybackInfoFilter>(order: 0);           // Handle playback info for dynamic items
            options.Filters.AddService<SubtitleFilter>(order: 0);               // Handle subtitles for dynamic items
            options.Filters.AddService<SearchActionFilter>(order: 1);
        });
    }
}
