using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.ScheduledTasks;

/// <summary>
/// Scheduled task that cleans up expired cache entries.
/// </summary>
public class CacheCleanupTask : IScheduledTask
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<CacheCleanupTask> _logger;

    public CacheCleanupTask(IMemoryCache cache, ILogger<CacheCleanupTask> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "DynamicLibraryCacheCleanup";

    /// <inheritdoc />
    public string Name => "Dynamic Library: Cache Cleanup";

    /// <inheritdoc />
    public string Description => "Cleans up expired cache entries from the Dynamic Library plugin to free memory.";

    /// <inheritdoc />
    public string Category => "Dynamic Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Dynamic Library cache cleanup task");

        try
        {
            progress.Report(10);

            // MemoryCache automatically removes expired entries, but we can
            // trigger a compaction to aggressively reclaim memory
            if (_cache is MemoryCache memoryCache)
            {
                // Get current stats before cleanup
                var statsBefore = memoryCache.Count;
                _logger.LogDebug("Cache entries before cleanup: {Count}", statsBefore);

                progress.Report(30);

                // Compact 25% of the cache - this removes least recently used entries
                memoryCache.Compact(0.25);

                progress.Report(80);

                var statsAfter = memoryCache.Count;
                _logger.LogInformation(
                    "Cache cleanup completed. Entries before: {Before}, after: {After}, removed: {Removed}",
                    statsBefore,
                    statsAfter,
                    statsBefore - statsAfter);
            }
            else
            {
                _logger.LogDebug("Cache is not a MemoryCache instance, skipping compaction");
            }

            progress.Report(100);
            _logger.LogInformation("Dynamic Library cache cleanup task completed");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Cache cleanup task was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cache cleanup");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run daily at 4 AM
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        };
    }
}
