# Jellyfin Dynamic Library Plugin

A Jellyfin plugin that creates an "infinite library" by displaying content from TVDB, TMDB, or Stremio addons even when media files don't exist locally. Search results appear as virtual items that can be browsed for metadata or streamed via configurable providers.

## Prerequisites

- Jellyfin 10.11.x or later
- At least one metadata source:
  - [TVDB API key](https://thetvdb.com/api-information) - for TV shows and anime
  - [TMDB API key](https://www.themoviedb.org/settings/api) - for movies
  - Stremio catalog addon URL (e.g., Cinemeta, AIOMetadata)
- (Optional) Stream provider for playback:
  - AIOStreams addon (recommended), OR
  - Embedarr instance, OR
  - Direct URL templates to your streaming service

## Installation

### From Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
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

1. Go to **Dashboard > Plugins > Dynamic Library**
2. Choose a **Catalog Provider**:
   - **Stremio Addon**: Enter a Stremio catalog URL (e.g., `https://v3-cinemeta.strem.io`)
   - **Direct**: Enter your TVDB and/or TMDB API keys
3. (Optional) Configure a **Stream Provider** for playback:
   - **AIOStreams**: Enter your AIOStreams addon URL
   - **Direct**: Configure URL templates
   - **Embedarr**: Enter your Embedarr instance URL
4. (Optional) Enable **Persistence** to save items to your library
5. Save and restart Jellyfin
6. Search for any movie or TV show - results will include virtual items from the configured sources

## Features

### Virtual Library Items
- Search returns results from your configured catalog source alongside your existing library
- Full metadata including posters, backdrops, cast, descriptions, and ratings
- Virtual items are distinguished by a `DynamicLibrary` provider ID

### Catalog Providers
- **Stremio Addon**: Use Cinemeta, AIOMetadata, or any compatible Stremio addon
- **Direct APIs**: Query TVDB/TMDB directly for metadata

### Multiple Stream Providers
- **None**: Browse-only mode for metadata exploration
- **AIOStreams**: Use your configured AIOStreams Stremio addon for multi-source streaming with version selection
- **Embedarr**: Automatic STRM generation via Embedarr API
- **Direct**: Custom URL templates with placeholder support

### Persistent Library
- Optionally save virtual items as real Jellyfin library items (.strm files)
- Full playback tracking, watched status, and resume support
- Automatic detection of new episodes for ongoing series

### Anime Audio Versions
- Optional sub/dub track selection for anime content
- Configurable audio track options (e.g., "sub,dub")
- Works with Direct mode URL templates

### Automatic Subtitles
- Fetches subtitles from OpenSubtitles for virtual items
- Supports multiple languages
- Caches subtitles locally for performance

### Language Override
- Display metadata in your preferred language
- Supports 20+ languages via ISO 639-2/3 codes

## Configuration

### General Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | true | Enable/disable the plugin |
| `CacheTtlMinutes` | 60 | How long to cache API responses |

### Catalog Provider

| Setting | Options | Description |
|---------|---------|-------------|
| `CatalogProvider` | StremioAddon, Direct | Source for metadata and search results |
| `StremioCatalogUrl` | - | Stremio addon URL (when using StremioAddon mode) |

**Stremio Addon Examples:**
```
# Cinemeta (official Stremio catalog)
https://v3-cinemeta.strem.io

# AIOMetadata (your configured instance)
https://your-aiometadata-instance.com/xxxxx
```

### Direct API Settings (when CatalogProvider=Direct)

| Setting | Description |
|---------|-------------|
| `TvdbApiKey` | TVDB v4 API key for TV shows and anime |
| `TmdbApiKey` | TMDB v3 API key for movies |
| `MovieApiSource` | API source for movies: None, TMDB, or TVDB |
| `TvShowApiSource` | API source for TV shows: None, TVDB, or TMDB |
| `MaxMovieResults` | Maximum movie search results (default: 20) |
| `MaxTvShowResults` | Maximum TV show search results (default: 20) |

### Stream Provider

| Setting | Description |
|---------|-------------|
| `StreamProvider` | None (browse only), AIOStreams, Embedarr, or Direct |

#### AIOStreams Settings

| Setting | Description |
|---------|-------------|
| `AIOStreamsUrl` | Full AIOStreams addon URL including encrypted config (e.g., `https://aiostreams.elfhosted.com/E2_xxxxx`) |
| `EnableHlsProbing` | Probe HLS streams for accurate duration (helps with scrubbing on Android TV). Disable if experiencing playback issues with token-based streams. Default: false |

#### Embedarr Settings

| Setting | Description |
|---------|-------------|
| `EmbedarrUrl` | Embedarr instance URL |
| `EmbedarrApiKey` | Embedarr API key (if required) |
| `CreateMediaOnView` | Pre-trigger Embedarr when viewing item details (default: false) |

#### Direct URL Templates

| Setting | Description |
|---------|-------------|
| `DirectMovieUrlTemplate` | URL template for movie streams |
| `DirectTvUrlTemplate` | URL template for TV show streams |
| `DirectAnimeUrlTemplate` | URL template for anime streams |
| `ShowUnreleasedStreams` | Generate stream URLs for unreleased content (default: false) |

**Template Placeholders:**

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

| Setting | Default | Description |
|---------|---------|-------------|
| `EnableAnimeAudioVersions` | false | Show audio track selector for anime |
| `AnimeAudioTracks` | "sub,dub" | Comma-separated track options |

### Language Settings

| Setting | Options | Description |
|---------|---------|-------------|
| `LanguageMode` | Default, Override | Default uses original language from API |
| `PreferredLanguage` | ISO 639-2 code | Language code (eng, jpn, spa, fra, deu, etc.) |

### Subtitle Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `EnableSubtitles` | false | Enable automatic subtitle fetching |
| `OpenSubtitlesApiKey` | - | API key from [OpenSubtitles](https://www.opensubtitles.com/consumers) |
| `SubtitleLanguages` | "en" | Comma-separated ISO 639-1 codes (e.g., "en,es,fr") |
| `MaxSubtitlesPerLanguage` | 1 | Maximum subtitles to download per language |
| `UseJellyfinOpenSubtitlesCredentials` | true | Use credentials from Jellyfin's OpenSubtitles plugin |
| `SubtitleCachePath` | /tmp/jellyfin-dynamiclibrary-subtitles | Local path for cached subtitle files |

### Persistence Settings

Enable persistence to save virtual items as real Jellyfin library items with full playback tracking.

| Setting | Default | Description |
|---------|---------|-------------|
| `EnablePersistence` | false | Enable persistent library items (.strm files) |
| `PersistentLibraryPath` | /dynamic-library | Root folder for persistent items (add as a Jellyfin library) |
| `TriggerLibraryScan` | true | Automatically scan library after creating new items |
| `CreateUnreleasedMedia` | false | Create media for unreleased content |
| `CheckForUpdatesOnView` | true | Check for new episodes when viewing persisted series |

**Setting Up Persistence:**

1. Enable `EnablePersistence` in plugin settings
2. Set `PersistentLibraryPath` to a folder (e.g., `/media/dynamic-library`)
3. Add this folder as a Jellyfin library (Movies and/or Shows)
4. When you click on a virtual item, it creates a .strm file in this folder
5. Library scan adds it as a real item with full tracking

## How It Works

```
Search Query
    |
    v
SearchActionFilter intercepts request
    |
    v
Query catalog source (Stremio addon or TVDB/TMDB)
    |
    v
Create virtual BaseItemDto objects with unique GUIDs
    |
    v
Cache items in memory
    |
    v
Return results to Jellyfin UI
    |
    v
User clicks item -> ItemLookupFilter returns cached DTO
    |
    v
(Optional) PersistenceService creates .strm files
    |
    v
User plays item -> PlaybackInfoFilter gets stream URL
    |
    v
(Optional) SubtitleFilter serves cached subtitles
```

Virtual items use deterministic GUIDs generated from a unique prefix (`jellyfin-dynamiclibrary-plugin:`) to prevent collisions with real library items.

## Known Limitations

- Virtual items (without persistence) are ephemeral and exist only in memory cache
- Playback requires a configured stream provider (AIOStreams, Embedarr, or Direct URLs)
- Some metadata may be incomplete depending on API availability
- Episode translations may not be available for all languages in TVDB
- Android TV may have quirks with version selection (the plugin includes workarounds)

## Troubleshooting

### Version Selection Not Working on Android TV
The plugin includes workarounds for Android TV's version selection quirks. If you're still experiencing issues, try:
1. Clear the Jellyfin app cache on Android TV
2. Ensure you're on the latest plugin version

### Playback Fails Immediately
1. Check that your stream provider is correctly configured
2. For AIOStreams, verify your addon URL is valid and not expired
3. For Direct mode, ensure URL templates include all required placeholders

### Missing Subtitles
1. Verify OpenSubtitles API key is correct
2. Check that the language codes in `SubtitleLanguages` are valid ISO 639-1 codes
3. Subtitles may not be available for all content

## Support

- **Issues:** [GitHub Issues](https://github.com/pythcon/jellyfin-dynamic-library/issues)
- **Discussions:** [GitHub Discussions](https://github.com/pythcon/jellyfin-dynamic-library/discussions)

## Development

### Building Locally

```bash
dotnet build Jellyfin.Plugin.DynamicLibrary/Jellyfin.Plugin.DynamicLibrary.csproj -c Release
```

### Local Testing

```bash
# Start test Jellyfin container
cd jellyfin-test && docker-compose up -d

# Build and install plugin
./install-plugin.sh jellyfin
```

### Release Process

1. Update version in `Jellyfin.Plugin.DynamicLibrary/Jellyfin.Plugin.DynamicLibrary.csproj`:
   ```xml
   <Version>X.Y.Z</Version>
   <AssemblyVersion>X.Y.Z.0</AssemblyVersion>
   <FileVersion>X.Y.Z.0</FileVersion>
   ```

2. Update version in `.github/workflows/build.yaml`:
   - `meta.json` version field
   - ZIP filename (`dynamic-library-X.Y.Z.zip`)

3. Commit changes:
   ```bash
   git add -A && git commit -m "Bump version to X.Y.Z"
   ```

4. Create and push version tag:
   ```bash
   git tag vX.Y.Z && git push && git push --tags
   ```

5. GitHub Actions will automatically:
   - Build the plugin
   - Create a GitHub Release with the ZIP
   - Update `manifest.json` on gh-pages

## License

MIT - See [LICENSE](LICENSE) file for details.
