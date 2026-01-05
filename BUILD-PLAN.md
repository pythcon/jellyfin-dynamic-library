# Plugin Distribution Setup

## Goal

Set up GitHub Actions CI/CD and plugin repository hosting so users can install the Dynamic Library plugin from Jellyfin's plugin catalog.

## Overview

1. **GitHub Actions** - Automatically build plugin on push/release
2. **GitHub Pages** - Host `manifest.json` for the plugin repository
3. **GitHub Releases** - Host the compiled plugin ZIP files
4. **README** - Installation instructions for users

## Files to Create

### 1. `build.yaml` (Plugin Metadata)

```yaml
name: "Dynamic Library"
guid: "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
version: 1
targetAbi: "10.11.0.0"
framework: "net9.0"
owner: "pythcon"
overview: "Infinite library powered by TVDB and TMDB"
description: >
  Creates an infinite library by displaying content from TVDB and TMDB
  even when media files don't exist locally. Stream via Embedarr or
  direct URL templates. Includes automatic subtitle fetching from OpenSubtitles.
category: "Metadata"
artifacts:
  - "Jellyfin.Plugin.DynamicLibrary.dll"
changelog: |-
  ## 1.0.0
  - Initial release
  - TVDB and TMDB integration
  - Embedarr and Direct URL streaming
  - OpenSubtitles support with Safari fix
  - Anime audio version selection (sub/dub)
```

### 2. `.github/workflows/build.yaml` (CI/CD)

```yaml
name: Build Plugin

on:
  push:
    branches: [main]
    tags: ['v*']
  pull_request:
    branches: [main]
  workflow_dispatch:

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Restore dependencies
        run: dotnet restore Jellyfin.Plugin.DynamicLibrary/Jellyfin.Plugin.DynamicLibrary.csproj

      - name: Build
        run: dotnet build Jellyfin.Plugin.DynamicLibrary/Jellyfin.Plugin.DynamicLibrary.csproj -c Release --no-restore

      - name: Create plugin ZIP
        run: |
          mkdir -p artifacts
          cd Jellyfin.Plugin.DynamicLibrary/bin/Release/net9.0
          zip -r ../../../../artifacts/dynamic-library.zip *.dll
          cd ../../../../

      - name: Upload artifact
        uses: actions/upload-artifact@v4
        with:
          name: dynamic-library
          path: artifacts/dynamic-library.zip

      - name: Create Release
        if: startsWith(github.ref, 'refs/tags/v')
        uses: softprops/action-gh-release@v1
        with:
          files: artifacts/dynamic-library.zip
        env:
          GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}

  update-manifest:
    needs: build
    if: startsWith(github.ref, 'refs/tags/v')
    runs-on: ubuntu-latest
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          ref: gh-pages
          fetch-depth: 0

      - name: Download artifact
        uses: actions/download-artifact@v4
        with:
          name: dynamic-library

      - name: Calculate checksum
        id: checksum
        run: echo "md5=$(md5sum dynamic-library.zip | cut -d ' ' -f 1)" >> $GITHUB_OUTPUT

      - name: Update manifest
        run: |
          VERSION="${GITHUB_REF#refs/tags/v}"
          TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
          CHECKSUM="${{ steps.checksum.outputs.md5 }}"

          # Update manifest.json with new version
          cat > manifest.json << EOF
          [
            {
              "guid": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
              "name": "Dynamic Library",
              "description": "Infinite library powered by TVDB and TMDB with streaming support",
              "overview": "Browse and stream content from TVDB/TMDB without local files",
              "owner": "pythcon",
              "category": "Metadata",
              "versions": [
                {
                  "version": "${VERSION}.0",
                  "changelog": "See GitHub releases for changelog",
                  "targetAbi": "10.11.0.0",
                  "sourceUrl": "https://github.com/pythcon/jellyfin-dynamic-library/releases/download/v${VERSION}/dynamic-library.zip",
                  "checksum": "${CHECKSUM}",
                  "timestamp": "${TIMESTAMP}"
                }
              ]
            }
          ]
          EOF

      - name: Commit and push
        run: |
          git config user.name "GitHub Actions"
          git config user.email "actions@github.com"
          git add manifest.json
          git commit -m "Update manifest for v${GITHUB_REF#refs/tags/v}" || exit 0
          git push
```

### 3. Initialize `gh-pages` branch

Create the branch with initial manifest:

```bash
git checkout --orphan gh-pages
git rm -rf .
echo '[]' > manifest.json
git add manifest.json
git commit -m "Initialize plugin repository"
git push origin gh-pages
git checkout main
```

### 4. Update `README.md` - Add Installation Section

Add after the Quick Start section:

```markdown
## Installation

### From Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Click **Add** and enter:
   - **Name:** Dynamic Library
   - **URL:** `https://pythcon.github.io/jellyfin-dynamic-library/manifest.json`
3. Click **Save**
4. Go to **Catalog** tab and find "Dynamic Library"
5. Click **Install**
6. Restart Jellyfin

### Manual Installation

1. Download the latest release from [GitHub Releases](https://github.com/pythcon/jellyfin-dynamic-library/releases)
2. Extract the ZIP to your Jellyfin plugins directory:
   - **Linux:** `/var/lib/jellyfin/plugins/`
   - **Docker:** `/config/plugins/` (mapped volume)
   - **Windows:** `C:\ProgramData\Jellyfin\Server\plugins\`
3. Restart Jellyfin
```

## GitHub Settings Required

1. **Enable GitHub Pages:**
   - Go to Settings → Pages
   - Source: Deploy from branch
   - Branch: `gh-pages` / `/ (root)`

2. **Repository URL format:**
   ```
   https://pythcon.github.io/jellyfin-dynamic-library/manifest.json
   ```

## Release Process

To release a new version:

```bash
# Update version in build.yaml changelog
git add .
git commit -m "Prepare v1.0.0 release"
git tag v1.0.0
git push origin main --tags
```

GitHub Actions will automatically:
1. Build the plugin
2. Create a GitHub Release with the ZIP
3. Update the manifest.json on gh-pages

## File Structure After Setup

```
jellyfin-dynamic-library/
├── .github/
│   └── workflows/
│       └── build.yaml          # CI/CD workflow
├── Jellyfin.Plugin.DynamicLibrary/
│   └── ...                     # Plugin source
├── build.yaml                  # Plugin metadata
└── README.md                   # With install instructions

gh-pages branch:
└── manifest.json               # Plugin repository manifest
```
