# GitLive

Sync tagged releases from a private repository to a public LIVE repository with clean, rewritten history.

## Overview

GitLive publishes commits tagged with `live/*` from your private repository to a public LIVE repository. Each tag becomes a squashed commit in the LIVE repo, hiding your development history while preserving release content.

## Installation

### CLI Tool

```bash
dotnet publish -c Release -r <runtime> -o <output-dir>
```

Common runtimes: `linux-x64`, `win-x64`, `osx-x64`, `osx-arm64`

Example:
```bash
dotnet publish -c Release -r linux-x64 -o ./bin
export PATH="$PATH:$(pwd)/bin"
```

### GitHub Action

No installation needed. Use directly in workflows with `Racso/git-live@main`.

## CLI Usage

### Basic Command

```bash
git-live --url=https://github.com/user/public-repo.git
```

### Options

#### Mode Options

- `--incremental` (default) - Sync only new tags after the last published commit
- `--repair` - Scan all tags and sync any missing ones. Use after modifying repo history (rebases, force-pushes) or tag modifications that desynced the tag history
- `--nuke` - Delete all LIVE tags and completely rebuild the repository. Use after major history rewrites or to fix severe desynchronization

#### Execution Options

- `--dry-run` - Preview changes without pushing to LIVE repository
- `--url=<url>` - Specify the LIVE repository URL (can also use `GITLIVE_URL` env var or `gitlive.z0` config file)
- `--user=<username>` - Username for authentication
- `--password=<password>` - Password/token for authentication
- `--config-file=<path>` - Path to config file (default: `gitlive.z0`)

#### Verbosity Options

- `-v` or `--verbose` - Show detailed progress information
- `-vv` or `--very-verbose` - Show all operations and debug information

### Authentication

**Environment Variables (Recommended):**
```bash
export GITLIVE_USER="username"
export GITLIVE_PASSWORD="ghp_token123"
git-live --url=https://github.com/user/public-repo.git
```

**CLI Arguments:**
```bash
git-live --url=https://github.com/user/repo.git --user=username --password=token
```

**Configuration File:**
```
public-url = https://github.com/user/public-repo.git
user = username
```

**Priority:** CLI arguments > Environment variables > Configuration file

**Note:** Passwords cannot be stored in `gitlive.z0` for security reasons.

### Configuration File

Create `gitlive.z0` in your repository root:

```
public-url = https://github.com/user/public-repo.git
user = myusername

files:
= - secret.txt
= - internal/
= - *.key
```

Then simply run:
```bash
git-live
```

### File Selection

Control which files sync to LIVE using ordered add (+) and remove (-) rules.

**Starting State:**
- First rule `+`: Start empty, only added files included
- First rule `-`: Start with all files, only removed files excluded

**Examples:**

Sync only documentation:
```
files:
= + *.md
= + docs/*.txt
```

Sync all except secrets:
```
files:
= - secret.txt
= - .env
= - *.key
= - internal/
```

Sync images except one:
```
files:
= + *.png
= + *.jpg
= - logo-draft.png
```

Sync source code excluding tests:
```
files:
= + src/*.cs
= - src/*Test.cs
```

## GitHub Action Usage

### Basic Workflow

Create `.github/workflows/sync-live.yml`:

```yaml
name: Sync to LIVE

on:
  push:
    tags:
      - 'live/*'
  workflow_dispatch:
    inputs:
      mode:
        description: 'Sync mode'
        required: false
        default: 'incremental'
        type: choice
        options:
          - incremental
          - repair
          - nuke
      dry-run:
        description: 'Dry run (preview without pushing)'
        required: false
        default: false
        type: boolean
      verbosity:
        description: 'Verbosity level'
        required: false
        default: 'normal'
        type: choice
        options:
          - normal
          - verbose
          - very-verbose

jobs:
  sync:
    runs-on: ubuntu-latest
    
    permissions:
      contents: read
    
    steps:
      - name: Checkout repository
        uses: actions/checkout@v4
        with:
          fetch-depth: 0
      
      - name: Sync to LIVE
        uses: Racso/git-live@main
        env:
          GITLIVE_USER: ${{ vars.GITLIVE_USER }}
          GITLIVE_PASSWORD: ${{ secrets.GITLIVE_PASSWORD }}
        with:
          live-url: https://${{ secrets.LIVE_TOKEN }}@github.com/username/public-repo.git
          mode: ${{ inputs.mode || 'incremental' }}
          dry-run: ${{ inputs.dry-run || false }}
          verbosity: ${{ inputs.verbosity || 'normal' }}
```

### Action Inputs

- `live-url` - URL of the LIVE repository (can be omitted if using `gitlive.z0` config file or `GITLIVE_URL` env var)
- `mode` - Sync mode: `incremental`, `repair`, or `nuke` (default: `incremental`)
- `dry-run` - Preview without pushing: `true` or `false` (default: `false`)
- `verbosity` - Verbosity level: `normal`, `verbose`, or `very-verbose` (default: `normal`)
- `config-file` - Path to config file (optional)

### Setup

1. Create a Personal Access Token at https://github.com/settings/tokens with `repo` scope
2. Add as repository secret: `LIVE_TOKEN`
3. Add username as repository variable: `GITLIVE_USER` (Settings → Secrets and variables → Actions → Variables)
4. Add password as repository secret: `GITLIVE_PASSWORD`
5. Update `live-url` with your repository

## Usage Examples

### Publish a Release

```bash
git tag live/1.0.0
git push origin live/1.0.0
```

If using GitHub Actions, the workflow triggers automatically. For CLI:

```bash
git-live --url=https://github.com/user/public-repo.git
```

### Preview Changes

```bash
git-live --url=https://github.com/user/repo.git --dry-run -v
```

### Repair After Rebase

After rebasing or force-pushing, tags may desync. Use repair mode:

```bash
git-live --url=https://github.com/user/repo.git --repair
```

### Complete Rebuild

After major history rewrites or severe issues:

```bash
git-live --url=https://github.com/user/repo.git --nuke -v
```

### Using Config File

```bash
cat > gitlive.z0 << 'EOF'
public-url = https://github.com/user/public-repo.git
user = myusername

files:
= - .env
= - secrets/
EOF

export GITLIVE_PASSWORD="token"
git-live
```

## How It Works

1. Identifies all `live/*` tags in source repository
2. Creates a temporary git repository
3. For each tag, creates a squashed commit with the tag's content
4. Pushes commits and tags to LIVE repository
5. Each commit contains metadata linking to the original

Tags in LIVE have the prefix removed: `live/1.0.0` → `1.0.0`

## Troubleshooting

### No Tags Published

**Problem:** "No new tags to publish" message appears.

**Solutions:**
- Verify tags exist: `git tag | grep "^live/"`
- Check tags are fetched: `git fetch --tags`
- Try repair mode: `git-live --repair`
- Use verbose mode to see details: `git-live -vv`

### Authentication Failures

**Problem:** Push fails with authentication error.

**Solutions:**
- Verify credentials are set correctly
- For GitHub, use Personal Access Token, not password
- Check token has `repo` scope
- Test credentials: `git ls-remote https://token@github.com/user/repo.git`
- Use very verbose mode: `git-live -vv`

### Tags Out of Sync

**Problem:** LIVE repository has different tags than expected.

**Solutions:**
- Use repair mode to resync: `git-live --repair`
- If severely desynced, use nuke mode: `git-live --nuke`
- Verify tags in both repos: `git ls-remote --tags <url>`

### Wrong Files in LIVE

**Problem:** Unwanted files appear in LIVE repository.

**Solutions:**
- Review file selection rules in `gitlive.z0`
- Remember first rule determines starting state
- Test with dry-run: `git-live --dry-run -v`
- If already pushed, fix rules and use: `git-live --nuke`

### History Desynchronization

**Problem:** After rebasing, force-pushing, or modifying tags, LIVE repository is out of sync.

**Cause:** Git history modifications change commit hashes, breaking the link between source and LIVE tags.

**Solutions:**
- Use repair mode to resync missing tags: `git-live --repair`
- For major issues, rebuild completely: `git-live --nuke`
- Avoid rewriting history of already-published tags

### Workflow Not Triggering

**Problem:** GitHub Actions workflow doesn't run when pushing tags.

**Solutions:**
- Verify workflow file is in `.github/workflows/`
- Check tag matches pattern: `live/*`
- Ensure workflow is on default branch
- Check workflow run history for errors
- Verify repository secrets/variables are set

### Dry Run Shows Unexpected Changes

**Problem:** Dry run output shows different changes than expected.

**Solutions:**
- Use very verbose mode: `git-live --dry-run -vv`
- Check file selection rules
- Verify which tags are being processed
- Compare with LIVE: `git ls-remote --tags <live-url>`

## Requirements

- .NET 9.0 or later
- Git 2.0 or later
- Read access to source repository
- Write access to LIVE repository
