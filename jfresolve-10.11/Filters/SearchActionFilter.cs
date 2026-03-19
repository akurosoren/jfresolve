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

                // Deduplicate: Remove TMDB results that already exist in local results
                // Collect provider IDs from local results for fast lookup
                var localProviderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var localItem in localResults.Items)
                {
                    if (localItem.ProviderIds != null)
                    {
                        if (localItem.ProviderIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
                            localProviderIds.Add("tmdb:" + tmdbId);
                        if (localItem.ProviderIds.TryGetValue("Imdb", out var imdbId) && !string.IsNullOrEmpty(imdbId))
                            localProviderIds.Add("imdb:" + imdbId);
                    }
                }

                // Filter out TMDB results that match local items
                var filteredTmdbDtos = tmdbDtos.Where(dto =>
                {
                    if (dto.ProviderIds == null) return true;
                    if (dto.ProviderIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId)
                        && localProviderIds.Contains("tmdb:" + tmdbId))
                        return false;
                    if (dto.ProviderIds.TryGetValue("Imdb", out var imdbId) && !string.IsNullOrEmpty(imdbId)
                        && localProviderIds.Contains("imdb:" + imdbId))
                        return false;
                    return true;
                }).ToList();

                _log.LogDebug(
                    "Jfresolve: Deduplication removed {Removed} TMDB results already present locally",
                    tmdbDtos.Count - filteredTmdbDtos.Count
                );

                if (filteredTmdbDtos.Count > 0)
                {
                    // Merge: Local results first, then TMDB results
                    var localTotal = localResults.TotalRecordCount;
                    var tmdbTotal = filteredTmdbDtos.Count;

                    // Update the total record count
                    localResults.TotalRecordCount = localTotal + tmdbTotal;

                    var mergedList = new List<BaseItemDto>();
                    
                    // Add local items that were already returned by Jellyfin (which respects start/limit)
                    mergedList.AddRange(localResults.Items);

                    // Calculate how many more items we need to fulfill the 'limit'
                    var remainingLimit = limit - mergedList.Count;
                    if (remainingLimit > 0)
                    {
                        var tmdbStartOffset = Math.Max(0, start - localTotal);
                        var tmdbItemsToTake = filteredTmdbDtos.Skip(tmdbStartOffset).Take(remainingLimit);
                        mergedList.AddRange(tmdbItemsToTake);
                    }

                    // Update the result with merged and correctly paged items
                    localResults.Items = mergedList.ToArray();
                }

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
    /// Uses collection folder visibility checks via ILibraryManager.
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
            {
                _log.LogDebug("Jfresolve: Could not get userId from claims");
                return false;
            }

            var user = _userManager.GetUserById(userId);
            if (user == null)
            {
                _log.LogDebug("Jfresolve: User not found for id {UserId}", userId);
                return false;
            }

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

            // Get all root collection folders from the library manager
            var allCollectionFolders = _libraryManager.GetUserRootFolder().Children.OfType<Folder>().ToList();
            
            _log.LogDebug("Jfresolve: Found {Count} collection folders, checking access for user {User}",
                allCollectionFolders.Count, user.Username);

            // For each configured plugin path, find the matching collection folder
            // and check if the user can see it
            foreach (var pluginPath in paths)
            {
                var normalizedPluginPath = pluginPath.TrimEnd('/', '\\');
                
                foreach (var collectionFolder in allCollectionFolders)
                {
                    var folderPath = collectionFolder.Path?.TrimEnd('/', '\\') ?? "";
                    
                    // Check if the plugin's path matches or is within this collection folder
                    if (normalizedPluginPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase)
                        || folderPath.StartsWith(normalizedPluginPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Use Jellyfin's built-in IsVisible to check user access
                        if (collectionFolder.IsVisible(user))
                        {
                            _log.LogDebug("Jfresolve: User {User} has access to folder {Folder} (path: {Path})",
                                user.Username, collectionFolder.Name, folderPath);
                            return true;
                        }
                        else
                        {
                            _log.LogDebug("Jfresolve: User {User} does NOT have access to folder {Folder} (path: {Path})",
                                user.Username, collectionFolder.Name, folderPath);
                        }
                    }
                }
            }

            _log.LogDebug("Jfresolve: User {User} does not have access to any jfresolve library", user.Username);
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
