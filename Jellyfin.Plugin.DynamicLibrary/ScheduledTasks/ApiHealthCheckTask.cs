using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.ScheduledTasks;

/// <summary>
/// Scheduled task that verifies API connectivity to configured providers.
/// </summary>
public class ApiHealthCheckTask : IScheduledTask
{
    private readonly ITvdbClient _tvdbClient;
    private readonly ITmdbClient _tmdbClient;
    private readonly ILogger<ApiHealthCheckTask> _logger;

    public ApiHealthCheckTask(
        ITvdbClient tvdbClient,
        ITmdbClient tmdbClient,
        ILogger<ApiHealthCheckTask> logger)
    {
        _tvdbClient = tvdbClient;
        _tmdbClient = tmdbClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "DynamicLibraryApiHealthCheck";

    /// <inheritdoc />
    public string Name => "Dynamic Library: API Health Check";

    /// <inheritdoc />
    public string Description => "Verifies connectivity to TVDB and TMDB APIs and refreshes authentication tokens.";

    /// <inheritdoc />
    public string Category => "Dynamic Library";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Dynamic Library API health check");

        var config = DynamicLibraryPlugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration not available");
            progress.Report(100);
            return;
        }

        var totalChecks = 2;
        var completedChecks = 0;

        try
        {
            // Check TVDB
            if (!string.IsNullOrEmpty(config.TvdbApiKey) && config.TvShowApiSource == ApiSource.Tvdb)
            {
                _logger.LogDebug("Checking TVDB API connectivity...");
                try
                {
                    // Attempt a simple search to verify connectivity
                    var results = await _tvdbClient.SearchSeriesAsync("test", cancellationToken);
                    _logger.LogInformation("TVDB API check passed - connection successful");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TVDB API check failed");
                }
            }
            else
            {
                _logger.LogDebug("TVDB not configured, skipping health check");
            }

            completedChecks++;
            progress.Report((double)completedChecks / totalChecks * 100);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Check TMDB
            if (!string.IsNullOrEmpty(config.TmdbApiKey) && config.MovieApiSource == ApiSource.Tmdb)
            {
                _logger.LogDebug("Checking TMDB API connectivity...");
                try
                {
                    // Attempt a simple search to verify connectivity
                    var results = await _tmdbClient.SearchMoviesAsync("test", cancellationToken);
                    _logger.LogInformation("TMDB API check passed - connection successful");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "TMDB API check failed");
                }
            }
            else
            {
                _logger.LogDebug("TMDB not configured, skipping health check");
            }

            completedChecks++;
            progress.Report(100);

            _logger.LogInformation("Dynamic Library API health check completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("API health check task was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during API health check");
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run every 12 hours to keep tokens fresh
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(12).Ticks
            }
        };
    }
}
