using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.DynamicLibrary.Extensions;

public static class ActionContextExtensions
{
    private static readonly HashSet<string> SearchActionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetItems",
        "GetItemsByUserIdLegacy",
    };

    public static string? GetActionName(this ActionExecutingContext ctx) =>
        (ctx.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

    public static string? GetControllerName(this ActionExecutingContext ctx) =>
        (ctx.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

    public static bool IsApiSearchAction(this ActionExecutingContext ctx)
    {
        var actionName = ctx.GetActionName();
        if (actionName == null) return false;

        // ItemsController actions
        if (SearchActionNames.Contains(actionName))
            return true;

        // SearchController.Get for /Search/Hints
        if (actionName == "Get" && ctx.GetControllerName() == "Search")
            return true;

        return false;
    }

    public static bool TryGetUserId(this ActionExecutingContext ctx, out Guid userId)
    {
        return ctx.HttpContext.TryGetUserId(out userId);
    }

    public static bool TryGetUserId(this HttpContext ctx, out Guid userId)
    {
        userId = Guid.Empty;

        var userIdStr =
            ctx.User.Claims.FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")?.Value
            ?? ctx.Request.Query["userId"].FirstOrDefault();

        return Guid.TryParse(userIdStr, out userId);
    }

    public static bool TryGetActionArgument<T>(
        this ActionExecutingContext ctx,
        string key,
        out T value,
        T? defaultValue = default)
    {
        if (ctx.ActionArguments.TryGetValue(key, out var objValue) && objValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = defaultValue!;
        return false;
    }

    public static HashSet<BaseItemKind> GetRequestedItemTypes(this ActionExecutingContext ctx)
    {
        var requested = new HashSet<BaseItemKind>(new[] { BaseItemKind.Movie, BaseItemKind.Series });

        // Already parsed as BaseItemKind[] by model binder
        if (ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes != null
            && includeTypes.Length > 0)
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            // Only keep Movie and Series
            requested.IntersectWith(new[] { BaseItemKind.Movie, BaseItemKind.Series });
        }

        // Remove excluded types
        if (ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes != null
            && excludeTypes.Length > 0)
        {
            requested.ExceptWith(excludeTypes);
        }

        return requested;
    }
}
