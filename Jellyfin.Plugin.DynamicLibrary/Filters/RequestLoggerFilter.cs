using System.Linq;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Debug filter that logs ALL incoming requests to help diagnose
/// Android TV vs web client differences.
/// </summary>
public class RequestLoggerFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly ILogger<RequestLoggerFilter> _logger;

    // Controllers we care about for debugging playback
    private static readonly HashSet<string> InterestingControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "MediaInfo",
        "Videos",
        "Items",
        "UserLibrary",
        "TvShows",
        "Playstate",
        "Sessions",
        "Universal"
    };

    public RequestLoggerFilter(ILogger<RequestLoggerFilter> logger)
    {
        _logger = logger;
    }

    // Run very early to log before any interception
    public int Order => -100;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

        // Only log interesting controllers
        if (controllerName != null && InterestingControllers.Contains(controllerName))
        {
            var args = string.Join(", ", context.ActionArguments.Select(kv =>
            {
                var value = kv.Value?.ToString() ?? "null";
                // Truncate long values
                if (value.Length > 100) value = value.Substring(0, 100) + "...";
                return $"{kv.Key}={value}";
            }));

            _logger.LogWarning("[DynamicLibrary:Request] {Controller}/{Action} Args=[{Args}] Path={Path}",
                controllerName,
                actionName,
                args,
                context.HttpContext.Request.Path);
        }

        await next();
    }
}
