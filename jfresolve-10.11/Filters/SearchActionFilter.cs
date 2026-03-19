using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jfresolve.Filters;

/// <summary>
/// Intercepts search requests and returns TMDB results (based on Gelato's SearchActionFilter pattern)
/// </summary>
public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly IDtoService _dtoService;
    private readonly JfresolveManager _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<SearchActionFilter> _log;

    public SearchActionFilter(
        IDtoService dtoService,
        JfresolveManager manager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<SearchActionFilter> log
    )
    {
        _dtoService = dtoService;
        _manager = manager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _log = log;
    }

    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // Check if search is enabled in configuration
        if (!JfresolvePlugin.Instance?.Configuration.EnableSearch ?? true)
        {
            await next();
            return;
        }

        // Check if this is a search action and get search term
        if (!IsSearchAction(ctx) || !TryGetSearchTerm(ctx, out var searchTerm))
        {
            await next();
            return;
        }

        // Handle "local:" prefix - pass through to default Jellyfin search
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm.Substring(6).Trim();
            await next();
            return;
        }

        // Get requested item types from query parameters
        var requestedTypes = GetRequestedItemTypes(ctx);
        if (requestedTypes.Count == 0)
        {
            // No supported types requested, let Jellyfin handle it
            await next();
            return;
        }

        // Get pagination parameters
        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        // Call next() first to let Jellyfin perform the standard local search
        var executedContext = await next();

        _log.LogDebug(
            "Jfresolve: Search executed. Result type: {ResultType}",
            executedContext.Result?.GetType().FullName ?? "null"
        );

        // Intercept the result and merge TMDB results if appropriate
        if (executedContext.Result is ObjectResult objResult && objResult.Value is QueryResult<BaseItemDto> localResults)
        {
            // Permission Check: Verify if user has access to jfresolve libraries
            var hasAccess = UserHasAccessToJfresolve(executedContext.HttpContext);
            _log.LogDebug("Jfresolve: User has access to jfresolve libraries: {HasAccess}", hasAccess);

            if (!hasAccess)
            {
                _log.LogDebug("Jfresolve: User does not have access to any jfresolve library, skipping TMDB search");
                return;
            }

            // Search TMDB for all requested types
            var baseItems = await SearchTmdbAsync(searchTerm, requestedTypes);

            _log.LogInformation(
                "Jfresolve: Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} tmdbResults={Results} localResults={Local}",
                searchTerm,
                string.Join(",", requestedTypes),
                start,
                limit,
                baseItems.Count,
                localResults.TotalRecordCount
            );

            if (baseItems.Count > 0)
            {
                // Convert BaseItems to DTOs
                var tmdbDtos = ConvertBaseItemsToDtos(baseItems);

                // Merge: Local results first, then TMDB results
                // Careful with pagination: Jellyfin already applied start/limit to localResults.Items
                var localTotal = localResults.TotalRecordCount;
                var tmdbTotal = tmdbDtos.Count;

                // Update the total record count
                localResults.TotalRecordCount = localTotal + tmdbTotal;

                var mergedList = new List<BaseItemDto>();
                
                // Add local items that were already returned by Jellyfin (which respects start/limit)
                mergedList.AddRange(localResults.Items);

                // Calculate how many more items we need to fulfill the 'limit'
                var remainingLimit = limit - mergedList.Count;
                if (remainingLimit > 0)
                {
                    // Calculate where to start in TMDB results
                    // If start < localTotal, we take TMDB results from the beginning (index 0)
                    // If start >= localTotal, we skip the items covered by the local results
                    var tmdbStartOffset = Math.Max(0, start - localTotal);
                    var tmdbItemsToTake = tmdbDtos.Skip(tmdbStartOffset).Take(remainingLimit);
                    mergedList.AddRange(tmdbItemsToTake);
                }

                // Update the result with merged and correctly paged items
                localResults.Items = mergedList.ToArray();

                _log.LogInformation(
                    "Jfresolve: Merged search results. Total: {Total}, Displayed: {Displayed}",
                    localResults.TotalRecordCount,
                    localResults.Items.Count
                );
            }
        }
        else
        {
            _log.LogWarning(
                "Jfresolve: Could not intercept search result. Result is not ObjectResult<QueryResult<BaseItemDto>>. Actual: {Type}",
                executedContext.Result?.GetType().FullName ?? "null"
            );
        }
    }

    /// <summary>
    /// Checks if the current user has access to at least one of the libraries managed by the plugin.
    /// </summary>
    private bool UserHasAccessToJfresolve(HttpContext httpContext)
    {
        try
        {
            // Get userId from JWT claims (same pattern as Common.TryGetUserId)
            var userIdStr = httpContext.User.Claims
                .FirstOrDefault(c => c.Type is "UserId" or "Jellyfin-UserId")
                ?.Value;

            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out var userId))
                return false;

            var user = _userManager.GetUserById(userId);
            if (user == null) return false;

            // Get configured paths that could be used for search results
            var config = JfresolvePlugin.Instance?.Configuration;
            if (config == null) return false;

            var paths = new List<string>();
            if (config.PathMode == Configuration.PathConfigMode.Simple)
            {
                if (!string.IsNullOrWhiteSpace(config.MoviePath)) paths.Add(config.MoviePath);
                if (!string.IsNullOrWhiteSpace(config.SeriesPath)) paths.Add(config.SeriesPath);
                if (config.EnableAnimeFolder && !string.IsNullOrWhiteSpace(config.AnimePath)) paths.Add(config.AnimePath);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(config.MovieSearchPath)) paths.Add(config.MovieSearchPath);
                if (!string.IsNullOrWhiteSpace(config.SeriesSearchPath)) paths.Add(config.SeriesSearchPath);
                if (config.EnableAnimeFolderAdvanced && !string.IsNullOrWhiteSpace(config.AnimeSearchPath)) paths.Add(config.AnimeSearchPath);
            }

            if (paths.Count == 0) return true;

            // Verify if any of these paths are within libraries visible to the user
            // Note: Admins can see all folders, so this check inherently grants them access
            foreach (var path in paths)
            {
                var folder = _manager.TryGetFolder(path);
                if (folder != null && folder.IsVisible(user))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Jfresolve: Error checking user permissions in search filter");
            return false;
        }
    }

    /// <summary>
    /// Search TMDB for all requested item types (similar to Gelato's SearchMetasAsync)
    /// </summary>
    private async Task<List<BaseItem>> SearchTmdbAsync(string searchTerm, HashSet<BaseItemKind> requestedTypes)
    {
        var tasks = new List<Task<List<BaseItem>>>();

        foreach (var itemType in requestedTypes)
        {
            tasks.Add(_manager.SearchTmdbAsync(searchTerm, itemType));
        }

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(r => r).ToList();
    }

    /// <summary>
    /// Convert BaseItems to DTOs (based on Gelato's ConvertMetasToDtos)
    /// </summary>
    private List<BaseItemDto> ConvertBaseItemsToDtos(List<BaseItem> baseItems)
    {
        var options = new DtoOptions
        {
            EnableImages = true,
            EnableUserData = false,
        };

        var dtos = new List<BaseItemDto>(baseItems.Count);

        foreach (var baseItem in baseItems)
        {
            var dto = _dtoService.GetBaseItemDto(baseItem, options);

            // Use the BaseItem's ID (already set in JfresolveManager)
            dto.Id = baseItem.Id;

            dtos.Add(dto);
        }

        return dtos;
    }

    private bool IsSearchAction(ActionExecutingContext ctx)
    {
        var actionName = ctx.ActionDescriptor?.DisplayName ?? "";
        return actionName.Contains("GetItems", StringComparison.OrdinalIgnoreCase) ||
               actionName.Contains("GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetSearchTerm(ActionExecutingContext ctx, out string searchTerm)
    {
        searchTerm = string.Empty;

        if (ctx.ActionArguments.TryGetValue("searchTerm", out var value) && value is string term)
        {
            searchTerm = term;
            return !string.IsNullOrWhiteSpace(searchTerm);
        }

        return false;
    }

    private HashSet<BaseItemKind> GetRequestedItemTypes(ActionExecutingContext ctx)
    {
        var requested = new HashSet<BaseItemKind>(
            new[] { BaseItemKind.Movie, BaseItemKind.Series }
        );

        // Check for includeItemTypes parameter
        if (ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes != null
            && includeTypes.Length > 0)
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            // Only keep Movie and Series (we only support these types)
            requested.IntersectWith(new[] { BaseItemKind.Movie, BaseItemKind.Series });
        }

        // Remove excluded types
        if (ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes != null
            && excludeTypes.Length > 0)
        {
            requested.ExceptWith(excludeTypes);
        }

        // If mediaTypes=Video, exclude Series (Gelato pattern)
        if (ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
            && mediaTypes != null
            && mediaTypes.Contains(MediaType.Video))
        {
            requested.Remove(BaseItemKind.Series);
        }

        return requested;
    }
}

// Helper extension methods
public static class ActionContextExtensions
{
    public static bool TryGetActionArgument<T>(
        this ActionExecutingContext ctx,
        string key,
        out T value,
        T defaultValue = default!)
    {
        if (ctx.ActionArguments.TryGetValue(key, out var objValue) && objValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = defaultValue!;
        return false;
    }
}
