using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Filters;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
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

        // Register API clients
        services.AddSingleton<ITvdbClient, TvdbClient>();
        services.AddSingleton<ITmdbClient, TmdbClient>();
        services.AddSingleton<IEmbedarrClient, EmbedarrClient>();

        // Register caches and services
        services.AddSingleton<DynamicItemCache>();
        services.AddSingleton<SearchResultFactory>();
        services.AddSingleton<DynamicLibraryService>();

        // Register filters
        services.AddSingleton<SearchActionFilter>();
        services.AddSingleton<ItemLookupFilter>();
        services.AddSingleton<ImageFilter>();
        services.AddSingleton<DynamicItemEndpointsFilter>();
        services.AddSingleton<SeasonEpisodeFilter>();

        // Register filters with MVC
        services.PostConfigure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            options.Filters.AddService<ImageFilter>(order: 0);                  // Run first for images
            options.Filters.AddService<ItemLookupFilter>(order: 0);             // Run first for item lookups
            options.Filters.AddService<DynamicItemEndpointsFilter>(order: 0);   // Handle secondary endpoints
            options.Filters.AddService<SeasonEpisodeFilter>(order: 0);          // Handle seasons/episodes
            options.Filters.AddService<SearchActionFilter>(order: 1);
        });
    }
}
