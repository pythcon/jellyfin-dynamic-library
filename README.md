# Jellyfin Dynamic Library Plugin

A Jellyfin plugin that creates an "infinite library" by displaying content from TVDB and TMDB even when media files don't exist locally. Search results appear as virtual items that can be browsed for metadata or streamed via configurable providers.

## Prerequisites

- Jellyfin 10.11.x or later
- At least one metadata API key:
  - [TVDB API key](https://thetvdb.com/api-information) - for TV shows and anime
  - [TMDB API key](https://www.themoviedb.org/settings/api) - for movies
- (Optional) Stream provider for playback:
  - Embedarr instance, OR
  - Direct URL templates to your streaming service

## Installation

### From Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
   - **Repository Name:** Dynamic Library
   - **Repository URL:** `https://pythcon.github.io/jellyfin-dynamic-library/manifest.json`
3. Click **Save**
4. Go to the **Catalog** tab and find "Dynamic Library"
5. Click **Install**
6. Restart Jellyfin

### Manual Installation

1. Download the latest release from [GitHub Releases](https://github.com/pythcon/jellyfin-dynamic-library/releases)
2. Extract the ZIP to your Jellyfin plugins directory:
   - **Linux:** `/var/lib/jellyfin/plugins/DynamicLibrary/`
   - **Docker:** `/config/plugins/DynamicLibrary/` (mapped volume)
   - **Windows:** `C:\ProgramData\Jellyfin\Server\plugins\DynamicLibrary\`
3. Restart Jellyfin

## Quick Start

1. Go to **Dashboard → Plugins → Dynamic Library**
2. Enter your TVDB and/or TMDB API keys
3. Select API sources for each content type (Movies, TV Shows)
4. (Optional) Configure a stream provider for playback
5. Save and restart Jellyfin
6. Search for any movie or TV show - results will include virtual items from the configured APIs

## Features

### Virtual Library Items
- Search returns results from TVDB/TMDB alongside your existing library
- Full metadata including posters, backdrops, cast, descriptions, and ratings
- Virtual items are distinguished by a `DynamicLibrary` provider ID

### Flexible Content Sources
- **Movies**: TMDB or TVDB
- **TV Shows**: TVDB or TMDB
- **Anime**: TVDB with AniList ID support

### Multiple Stream Providers
- **None**: Browse-only mode for metadata exploration
- **Embedarr**: Automatic STRM generation via Embedarr API
- **Direct**: Custom URL templates with placeholder support

### Anime Audio Versions
- Optional sub/dub track selection for anime content
- Configurable audio track options (e.g., "sub,dub")
- Uses `{audio}` placeholder in URL templates

### Automatic Subtitles
- Fetches subtitles from OpenSubtitles for virtual items
- Supports multiple languages
- Caches subtitles locally for performance

### Language Override
- Display metadata in your preferred language
- Supports 20+ languages via ISO 639-2/3 codes

## Configuration

### API Keys

| Setting | Description |
|---------|-------------|
| `TvdbApiKey` | TVDB v4 API key for TV shows and anime |
| `TmdbApiKey` | TMDB v3 API key for movies |

### Content Sources

| Setting | Options | Description |
|---------|---------|-------------|
| `MovieApiSource` | None, TMDB, TVDB | API source for movie searches |
| `TvShowApiSource` | None, TVDB, TMDB | API source for TV show searches |

### Stream Provider

| Setting | Description |
|---------|-------------|
| `StreamProvider` | None (browse only), Embedarr, or Direct |
| `EmbedarrUrl` | Embedarr instance URL |
| `EmbedarrApiKey` | Embedarr API key (if required) |
| `CreateMediaOnView` | Pre-trigger Embedarr when viewing item details |

### Direct URL Templates

Templates support these placeholders:

| Placeholder | Description |
|-------------|-------------|
| `{id}` | Preferred provider ID based on config |
| `{imdb}` | IMDB ID (tt1234567) |
| `{tmdb}` | TMDB ID |
| `{tvdb}` | TVDB ID |
| `{anilist}` | AniList ID (anime only) |
| `{season}` | Season number |
| `{episode}` | Episode number |
| `{absolute}` | Absolute episode number |
| `{audio}` | Audio track (sub/dub) |
| `{title}` | URL-encoded title |

**Example Templates:**
```
# Movies
https://stream.example.com/movie/{imdb}

# TV Shows
https://stream.example.com/tv/{imdb}/{season}/{episode}

# Anime with audio selection
https://stream.example.com/anime/{anilist}/{episode}?audio={audio}
```

### Provider ID Preferences

Control which ID is used for stream lookups:

| Setting | Options | Default |
|---------|---------|---------|
| `MoviePreferredId` | IMDB, TMDB | IMDB |
| `TvShowPreferredId` | IMDB, TVDB | IMDB |
| `AnimePreferredId` | IMDB, TVDB, AniList | IMDB |

### Anime Audio Versions

| Setting | Description |
|---------|-------------|
| `EnableAnimeAudioVersions` | Show audio track selector for anime |
| `AnimeAudioTracks` | Comma-separated track options (default: "sub,dub") |

### Language Settings

| Setting | Description |
|---------|-------------|
| `LanguageMode` | Default (original) or Override |
| `PreferredLanguage` | ISO 639-2 code (eng, jpn, spa, fra, deu, etc.) |

### Subtitle Settings

| Setting | Description |
|---------|-------------|
| `EnableSubtitles` | Enable automatic subtitle fetching |
| `OpenSubtitlesApiKey` | API key from [OpenSubtitles](https://www.opensubtitles.com/consumers) |
| `SubtitleLanguages` | Comma-separated ISO 639-1 codes (e.g., "en,es,fr") |
| `UseJellyfinOpenSubtitlesCredentials` | Use credentials from Jellyfin's OpenSubtitles plugin |
| `SubtitleCachePath` | Local path for cached subtitle files |

### Other Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | true | Enable/disable the plugin |
| `CacheTtlMinutes` | 60 | How long to cache API responses |
| `MaxMovieResults` | 20 | Maximum movie search results |
| `MaxTvShowResults` | 20 | Maximum TV show search results |

## How It Works

```
Search Query
    ↓
SearchActionFilter intercepts request
    ↓
Query TVDB/TMDB APIs
    ↓
Create virtual BaseItemDto objects with unique GUIDs
    ↓
Cache items in memory
    ↓
Return results to Jellyfin UI
    ↓
User clicks item → ItemLookupFilter returns cached DTO
    ↓
User plays item → PlaybackInfoFilter gets stream URL
    ↓
(Optional) SubtitleFilter serves cached subtitles
```

Virtual items use deterministic GUIDs generated from a unique prefix (`jellyfin-dynamiclibrary-plugin:`) to prevent collisions with real library items.

## Known Limitations

- Virtual items are ephemeral and exist only in memory cache
- Playback requires a configured stream provider (Embedarr or Direct URLs)
- Some metadata may be incomplete depending on API availability
- Episode translations may not be available for all languages in TVDB

## Support

- **Issues:** [GitHub Issues](https://github.com/pythcon/jellyfin-dynamic-library/issues)
- **Discussions:** [GitHub Discussions](https://github.com/pythcon/jellyfin-dynamic-library/discussions)

## License

MIT - See [LICENSE](LICENSE) file for details.
