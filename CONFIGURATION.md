# GitLive Configuration Guide

This guide explains how to configure GitLive using the new ConfigReader system.

## Configuration Sources

GitLive can read configuration values from three sources, with the following priority:

1. **CLI Arguments** (highest priority): `--variable=value`
2. **Environment Variables**: `GITLIVE_VARIABLE`
3. **Z0 Configuration File** (lowest priority): `variable = value` in `gitlive.z0`

## Common Configuration Variables

### URL Configuration

Specify the LIVE repository URL using any of these methods:

**CLI Argument:**
```bash
git-live --url=https://github.com/user/public-repo.git
```

**Environment Variable:**
```bash
export GITLIVE_URL="https://github.com/user/public-repo.git"
git-live
```

**Z0 File:**
```
# gitlive.z0
url = https://github.com/user/public-repo.git
# or
public-url = https://github.com/user/public-repo.git
```

### Authentication

#### Username

The username can be provided from all three sources:

**CLI Argument:**
```bash
git-live --url=https://github.com/user/repo.git --user=myusername
```

**Environment Variable:**
```bash
export GITLIVE_USER="myusername"
git-live
```

**Z0 File:**
```
# gitlive.z0
user = myusername
```

#### Password

For security, passwords can only be provided via CLI arguments or environment variables (NOT in the Z0 file):

**CLI Argument:**
```bash
git-live --url=https://github.com/user/repo.git --password=mypassword
```

**Environment Variable (Recommended):**
```bash
export GITLIVE_PASSWORD="mypassword"
git-live
```

**Note:** Passwords stored in Z0 files will be ignored for security reasons.

## Security Levels

The ConfigReader uses security levels to control where sensitive data can be read from:

- **SecureStrict**: Environment variables only (most secure)
- **SecureFlexible**: Environment variables and CLI arguments
- **All**: All three sources (CLI, ENV, Z0)

Current security level assignments:
- **URL**: All (public information)
- **Username**: All (less sensitive)
- **Password**: SecureFlexible (cannot be in Z0 file)

## Variable Naming

### Case Insensitivity

All variable names are case-insensitive:

```bash
# These are all equivalent:
--url=...
--URL=...
--Url=...

export GITLIVE_URL=...
export gitlive_url=...
export GitLive_Url=...
```

### Hyphen/Underscore Equivalence

Hyphens and underscores are treated as equivalent:

```bash
# These are all equivalent:
--public-url=...
--public_url=...

export GITLIVE_PUBLIC_URL=...
export GITLIVE_PUBLIC-URL=...

public-url = ...  # in gitlive.z0
public_url = ...  # in gitlive.z0
```

## Examples

### Basic Authentication with Environment Variables

```bash
# Set credentials
export GITLIVE_USER="myusername"
export GITLIVE_PASSWORD="mytoken"

# Run GitLive
git-live --url=https://github.com/user/public-repo.git
```

### Using Z0 File with Environment Password

```bash
# gitlive.z0
url = https://github.com/user/public-repo.git
user = myusername

# Set password via environment (not in file!)
export GITLIVE_PASSWORD="mytoken"

# Run GitLive
git-live
```

### Override Z0 Config with CLI

```bash
# gitlive.z0 has:
# url = https://github.com/user/public-repo.git

# Override with CLI:
git-live --url=https://github.com/user/different-repo.git
```

### GitHub Actions

```yaml
jobs:
  sync:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      
      - name: Sync to LIVE
        env:
          GITLIVE_URL: https://github.com/user/public-repo.git
          GITLIVE_USER: ${{ secrets.LIVE_USER }}
          GITLIVE_PASSWORD: ${{ secrets.LIVE_TOKEN }}
        run: |
          git-live
```

## Best Practices

1. **Use Environment Variables for Secrets**: Store passwords and tokens in environment variables or secrets management systems, never in Z0 files or CLI arguments in scripts.

2. **Store Public Config in Z0 Files**: Store non-sensitive configuration like URLs and usernames in the `gitlive.z0` file for easy sharing.

3. **Override with CLI for Testing**: Use CLI arguments to temporarily override configuration for testing without modifying files.

4. **Use GitHub Secrets in CI/CD**: When using GitLive in GitHub Actions or other CI/CD systems, use their secrets management features.

## Troubleshooting

### Variable Not Found

If GitLive can't find a configuration variable, check:

1. The variable name is spelled correctly (case doesn't matter)
2. You're using the correct format:
   - CLI: `--variable=value`
   - ENV: `GITLIVE_VARIABLE=value`
   - Z0: `variable = value`
3. The priority order: CLI > ENV > Z0

### Password Ignored from Z0 File

This is intentional for security. Passwords can only come from:
- Environment variables (recommended): `GITLIVE_PASSWORD`
- CLI arguments: `--password=...`

### Authentication Not Working

1. Verify credentials are set correctly
2. Check that the URL supports authentication (HTTPS URLs work best)
3. For GitHub, use a Personal Access Token instead of your account password
4. Run with `-vv` (very verbose) to see authentication debug messages
