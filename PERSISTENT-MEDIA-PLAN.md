# Persistent Media Implementation Plan

## Goal

Convert dynamic/ephemeral items into persistent Jellyfin library items when a user clicks on them. This enables:
- Playback tracking (watched status, resume points)
- User-specific data (favorites, ratings)
- Collections and playlists
- Jellyfin's recommendation engine

## Approach

Create `.strm` files that point to our plugin's stream endpoint. The endpoint dynamically resolves the actual stream URL based on current settings.

## Architecture

```
User clicks dynamic item
    ↓
Plugin creates folder + .strm file
    ↓
Jellyfin scans, fetches metadata, stores all provider IDs
    ↓
User clicks Play
    ↓
Jellyfin reads .strm → hits /DynamicLibrary/Stream/...
    ↓
Plugin queries Jellyfin for item's provider IDs
    ↓
Plugin applies current URL template from settings
    ↓
302 redirect to actual stream URL
```

## Folder Structure

```
/dynamic-library/                              # Configurable root path
├── Movies/
│   └── Rush Hour 2 (2001) [imdbid-tt0266915]/
│       └── Rush Hour 2 (2001).strm
│
├── Shows/
│   └── Breaking Bad (2008) [tvdbid-81189]/
│       └── Season 01/
│           └── Breaking Bad - S01E01 - Pilot.strm
│
└── Anime/
    └── Demon Slayer (2019) [anilistid-101922]/
        └── Season 01/
            └── Demon Slayer - S01E01 - Cruelty.strm
```

**Naming Conventions:**
- `[imdbid-tt1234567]` - Jellyfin auto-matches IMDB
- `[tvdbid-12345]` - Jellyfin auto-matches TVDB
- `[tmdbid-12345]` - Jellyfin auto-matches TMDB
- `[anilistid-12345]` - For anime (with AniList plugin)
- `Season XX` - Two-digit season folders
- `SXXEXX` - Season/episode in filename

## .strm File Contents

Each `.strm` file contains a URL to our plugin endpoint:

**Movies:**
```
http://jellyfin:8096/DynamicLibrary/Stream/movie/tt0266915
```

**TV Episodes:**
```
http://jellyfin:8096/DynamicLibrary/Stream/tv/tt0903747/1/1
```

**Anime Episodes:**
```
http://jellyfin:8096/DynamicLibrary/Stream/anime/101922/1/sub
```

### URL Generation Strategy

Use the request's host header when creating the .strm:
```csharp
var baseUrl = $"{Request.Scheme}://{Request.Host}";
var strmContent = $"{baseUrl}/DynamicLibrary/Stream/movie/{imdbId}";
```

This automatically uses whatever URL the user accesses Jellyfin through.

## New Endpoints

### 1. Stream Redirect Endpoints

**Movie:**
```
GET /DynamicLibrary/Stream/movie/{imdbId}
```
- Query Jellyfin for item with IMDB ID
- Get all provider IDs from Jellyfin's response
- Apply `DirectMovieUrlTemplate` from settings
- Return 302 redirect to resolved URL

**TV Episode:**
```
GET /DynamicLibrary/Stream/tv/{seriesImdbId}/{season}/{episode}
```
- Query Jellyfin for series with IMDB ID
- Get provider IDs
- Apply `DirectTvUrlTemplate` from settings
- Return 302 redirect

**Anime Episode:**
```
GET /DynamicLibrary/Stream/anime/{anilistId}/{episode}/{audio}
```
- Query Jellyfin for series with AniList ID
- Get provider IDs
- Apply `DirectAnimeUrlTemplate` from settings
- Include audio track (sub/dub) in URL
- Return 302 redirect

### 2. Persist Item Endpoint

```
POST /DynamicLibrary/Persist/{itemId}
```
- Called when user clicks on a dynamic item
- Creates folder structure and .strm file
- Triggers library scan for the new item
- Returns path to created item

## Configuration

New settings to add:

| Setting | Default | Description |
|---------|---------|-------------|
| `PersistentLibraryPath` | `/dynamic-library` | Root folder for persistent items |
| `EnablePersistence` | `false` | Enable automatic item persistence |
| `TriggerLibraryScan` | `true` | Auto-scan after creating items |

## Implementation Steps

### Phase 1: Stream Redirect Endpoint

1. Create `StreamController.cs` with redirect endpoints
2. Implement Jellyfin item lookup by provider ID
3. Reuse existing `ReplacePlaceholders()` logic for URL building
4. Return 302 redirects

**Files:**
- `Api/StreamController.cs` (new)

### Phase 2: Persistence Service

1. Create `PersistenceService.cs` for file operations
2. Implement folder/file creation with proper naming
3. Handle Movies, TV Shows, and Anime separately
4. Sanitize titles for filesystem safety

**Files:**
- `Services/PersistenceService.cs` (new)

### Phase 3: Integration

1. Add persist endpoint to `DynamicLibraryController.cs`
2. Hook into `ItemLookupFilter` to auto-persist on view (optional)
3. Trigger library scan via Jellyfin API after creation

**Files:**
- `Api/DynamicLibraryController.cs` (modify)
- `Filters/ItemLookupFilter.cs` (modify)

### Phase 4: Configuration

1. Add new settings to `PluginConfiguration.cs`
2. Update `configPage.html` with new options

**Files:**
- `Configuration/PluginConfiguration.cs` (modify)
- `Configuration/configPage.html` (modify)

## Deduplication (Important!)

Prevent showing dynamic results for items that already exist in the user's library.

### Search Deduplication

```
User searches "Rush Hour 2"
    ↓
Plugin queries TMDB → Gets result with imdb=tt0266915
    ↓
Plugin checks Jellyfin: "Any item with imdb=tt0266915?"
    ↓
YES → Don't include in dynamic results (user already has it)
NO  → Show dynamic result
```

**Implementation in `SearchActionFilter.cs`:**
1. After fetching API results, collect all provider IDs
2. Query Jellyfin library for items matching those IDs
3. Filter out any dynamic results that match existing items
4. Return only truly new items

### Persistence Deduplication

```
User clicks dynamic item
    ↓
Check: Does item with imdb=tt0266915 exist in library?
    ↓
YES → Skip creation, optionally redirect to existing item
NO  → Create .strm file
```

**Implementation in `PersistenceService.cs`:**
1. Before creating folder/.strm, query Jellyfin by provider ID
2. If exists, return existing item's ID instead of creating duplicate
3. Log that item was skipped

### Jellyfin API for Deduplication

```csharp
// Query items by provider ID
var query = new InternalItemsQuery
{
    HasAnyProviderId = new Dictionary<string, string>
    {
        { "Imdb", "tt0266915" }
    },
    Recursive = true
};
var existingItems = _libraryManager.GetItemsResult(query);
```

## Edge Cases

1. **Item already exists** - Check before creating, skip if exists (see Deduplication above)
2. **Invalid characters in title** - Sanitize for filesystem
3. **Missing provider IDs** - Fall back to available IDs
4. **Library not configured** - User must set up library in Jellyfin pointing to `PersistentLibraryPath`
5. **Anime absolute numbering** - Store absolute episode number for anime
6. **Same title, different item** - Always match by provider ID, not title

## User Setup Required

After enabling persistence, user must:

1. Set `PersistentLibraryPath` in plugin settings
2. Create a Jellyfin library pointing to that path:
   - Add Library → Type: Movies/Shows → Folder: `/dynamic-library/Movies` or `/dynamic-library/Shows`
3. Enable the library in Jellyfin

## Example Flow

1. User searches "Rush Hour 2" → Dynamic item appears
2. User clicks on it → Plugin creates:
   ```
   /dynamic-library/Movies/Rush Hour 2 (2001) [imdbid-tt0266915]/
       └── Rush Hour 2 (2001).strm
   ```
   Contents: `http://jellyfin:8096/DynamicLibrary/Stream/movie/tt0266915`
3. Jellyfin scans → Creates library item with full metadata
4. User clicks Play → Jellyfin reads .strm → Hits our endpoint
5. Endpoint queries Jellyfin: "Item with imdb=tt0266915?"
6. Jellyfin returns: `{imdb: "tt0266915", tmdb: "7459"}`
7. Plugin applies template: `https://stream.example.com/movie/tt0266915.m3u8`
8. Returns 302 redirect → Video plays
9. Playback tracked, watch status saved!

## Future Enhancements

- Bulk persist all search results
- Scheduled cleanup of unwatched items
- Sync watch status back to external services
- Support for movie/show collections
