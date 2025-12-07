# GitLive

Sync tagged releases from a private repository to a public LIVE repository, creating a clean, rewritten history.

## Overview

GitLive publishes commits tagged with `live/*` from your private repository to a public LIVE repository. Each tag becomes a squashed commit in the LIVE repo, hiding your private development history while preserving release content.

## CLI Usage

### Installation

```bash
dotnet publish -c Release -r <runtime> -o <output-dir>
# Example for Linux:
dotnet publish -c Release -r linux-x64 -o ./bin
```

### Basic Commands

```bash
# Sync to LIVE repository (incremental mode)
git-live --url=https://github.com/user/public-repo.git

# Repair mode: resync from first missing tag
git-live --url=https://github.com/user/public-repo.git --repair

# Nuke mode: completely rebuild LIVE repository
git-live --url=https://github.com/user/public-repo.git --nuke

# Dry run: preview changes without pushing
git-live --url=https://github.com/user/public-repo.git --dry-run

# Verbose output
git-live --url=https://github.com/user/public-repo.git -v
git-live --url=https://github.com/user/public-repo.git -vv  # very verbose
```

### Configuration File

Create a `gitlive` file (Z0 format) in your repository root:

```
public-url = https://github.com/user/public-repo.git

ignore:
# = secret.txt
# = internal/
```

With a config file, simply run:

```bash
git-live
```

### Sync Modes

- **Incremental** (default): Syncs only new tags after the last published commit
- **Repair**: Scans all tags and syncs any missing ones
- **Nuke**: Deletes all LIVE tags and completely rebuilds the repository

## GitHub Action Usage

Add to your workflow (`.github/workflows/sync-live.yml`):

```yaml
name: Sync to LIVE

on:
  push:
    tags:
      - 'live/*'

jobs:
  sync:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0  # Required for full history

      - uses: Racso/git-live@main
        with:
          live-url: https://${{ secrets.LIVE_TOKEN }}@github.com/user/public-repo.git
          mode: incremental  # or: repair, nuke
          dry-run: false
          verbosity: normal  # or: verbose, very-verbose
```

See `.github/workflows/sync-live-example.yml` for a complete example with setup instructions.

### Action Authentication

Configure authentication for pushing to LIVE repository:

1. Create a Personal Access Token at https://github.com/settings/tokens with `repo` scope
2. Add it as a repository secret named `LIVE_TOKEN`
3. Use it in the workflow as shown above: `https://${{ secrets.LIVE_TOKEN }}@github.com/...`

## Tagging Strategy

Tag commits you want to publish:

```bash
# Tag current commit for publishing
git tag live/1.0.0
git push origin live/1.0.0

# Then run GitLive or let the action handle it
```

Tags must start with `live/` prefix. The published tags in the LIVE repo will have the prefix removed (e.g., `live/1.0.0` → `1.0.0`).

## How It Works

1. Identifies all `live/*` tags in your repository
2. Creates a temporary git repository  
3. For each tag, creates a squashed commit with the tag's content
4. Pushes commits and tags to the LIVE repository
5. Each LIVE commit contains metadata linking back to the original commit

## Requirements

- .NET 9.0 or later
- Git 2.0 or later
- Read access to source repository
- Write access to LIVE repository
