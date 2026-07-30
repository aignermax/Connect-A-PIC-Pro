# Release Process

This document describes the automated release process for Connect-A-PIC Pro.

## Overview

The release process is fully automated through GitHub Actions workflows. When you create a new version, the system will:

1. ✅ Update version in project files
2. 🏷️ Create a git tag automatically
3. 🏗️ Build Windows and Linux executables
4. 🧪 Run all unit tests
5. 📝 Generate a smart changelog
6. 📦 Create a GitHub release with artifacts

## Creating a New Release

### Step 1: Trigger Version Change Workflow

1. Go to **Actions** → **Version Change** workflow
2. Click **Run workflow**
3. Enter the new version number (e.g., `0.4.0`) **without** the `v` prefix
4. Click **Run workflow**

This will:
- Update `CAP.Desktop/CAP.Desktop.csproj` with the new version
- Create a pull request with the version change

### Step 2: Review and Merge PR

1. Review the automatically created PR titled `chore: update version to X.X.X`
2. Verify the version number is correct in the csproj file
3. Merge the PR

### Step 3: Automatic Release Creation

**Everything else happens automatically!**

When the PR is merged:
1. **Auto-Tag Workflow** creates tag `vX.X.X` (runs in ~5 seconds)
2. **Build & Publish Workflow** is triggered by the tag:
   - Builds Windows and Linux executables (~2-3 minutes)
   - Runs all unit tests
   - Generates changelog from git commits
   - Creates GitHub release with artifacts

### Step 4: Verify Release

1. Go to **Releases** page
2. Verify the new release appears with:
   - Correct version tag (e.g., `v0.4.0`)
   - Changelog with categorized commits
   - Windows and Linux download links

### Step 5: Manual smoke test on a fresh machine

Some things CI cannot catch (first-run provisioning of external tools). On a
machine/VM without dev tools installed:

- [ ] Install via MSI and launch the app
- [ ] FDTD solver: availability is detected, and a small simulation runs end-to-end (from #590)

## Workflow Details

### 1. Version Change Workflow (`version_change.yaml`)

**Trigger:** Manual via workflow_dispatch

**What it does:**
- Updates version in `CAP.Desktop/CAP.Desktop.csproj`
- Creates a pull request with the change

**Inputs:**
- `version`: Version number without `v` prefix (e.g., `0.4.0`)

### 2. Auto-Tag on Version PR Merge (`auto_tag_on_version_pr.yaml`)

**Trigger:** Automatically when a version PR is merged

**What it does:**
- Extracts version from csproj file
- Checks if tag already exists
- Creates and pushes tag `vX.X.X` if it doesn't exist

**Why it's separate:**
- Ensures tag is created AFTER the version is actually updated in main branch
- Prevents duplicate or premature tag creation

### 3. Build & Publish Workflow (`Build_Exe.yaml`)

**Trigger:** Automatically when a tag starting with `v` is pushed

**What it does:**

#### Test Phase
- Runs all unit tests
- Fails if any test fails

#### Build Phase
- Builds for Windows (win-x64) and Linux (linux-x64)
- Creates self-contained single-file executables
- No .NET installation required for end users

#### Release Phase
- Extracts version from csproj and tag name
- Generates smart changelog from commit messages
- Creates GitHub release with:
  - Proper version tag (no more `0.1.0` issues!)
  - Categorized changelog
  - Installation instructions
  - Windows and Linux artifacts

## Changelog Generation

The changelog is automatically generated from commit messages and categorized:

### Categories

| Commit Pattern | Category |
|----------------|----------|
| `feat:`, `feature:`, `add:`, `implement:` | ✨ New Features |
| `fix:`, `bugfix:` | 🐛 Bug Fixes |
| `refactor:`, `improve:`, `update:`, `enhance:`, `chore:` | 🔧 Improvements |
| Contains `BREAKING` or `breaking change` | ⚠️ Breaking Changes |
| Everything else | 📝 Other Changes |

### Writing Good Commit Messages

For best changelog results, use conventional commit prefixes:

```bash
# Features
git commit -m "feat: Add multi-component selection"
git commit -m "add: Implement dark mode support"

# Bug fixes
git commit -m "fix: Export JSON button now works in diagnostics"
git commit -m "bugfix: Components maintain visual selection after drag"

# Improvements
git commit -m "refactor: Replace Manhattan router with smooth CSC planner"
git commit -m "improve: Better routing for close pins"
git commit -m "chore: Update dependencies to latest versions"

# Breaking changes
git commit -m "feat!: Change routing API (BREAKING: removes old connector interface)"
```

## Example Changelog Output

```markdown
## Connect-A-PIC Pro v0.4.0

### ✨ New Features
- feat: Add multi-component selection with box selection
- add: Implement group drag operations

### 🐛 Bug Fixes
- fix: Export JSON button now works in Routing Diagnostics
- fix: Components maintain visual selection after drag

### 🔧 Improvements
- refactor: Replace 632-line Manhattan router with 391-line smooth CSC planner
- improve: Better routing for close pins with smooth curved paths
- chore: Update Avalonia to 11.2.1

---
**12 commits** since v0.3.1

## 📦 Installation

### Windows
1. Download `Connect-A-PIC-Pro-0.4.0-win-x64.zip`
2. Extract and run `Connect-A-PIC-Pro.exe`

### Linux
1. Download `Connect-A-PIC-Pro-0.4.0-linux-x64.tar.gz`
2. Extract: `tar xzf Connect-A-PIC-Pro-*.tar.gz`
3. Run: `./Connect-A-PIC-Pro`

No .NET installation required — everything is included.
```

## Troubleshooting

### Problem: Tag was created but no release

**Cause:** Build or tests failed

**Solution:**
1. Check **Actions** → **Build & Publish** workflow run
2. Fix the failing tests or build errors
3. Delete the tag: `git push origin :vX.X.X`
4. Fix the issues and create a new version

### Problem: Duplicate tags or wrong version

**Cause:** Tag was manually created before workflow ran

**Solution:**
1. Delete the incorrect tag:
   ```bash
   git tag -d vX.X.X
   git push origin :vX.X.X
   ```
2. Let the workflow create the tag automatically

### Problem: Changelog is empty or incomplete

**Cause:** No previous tag found, or commits don't match patterns

**Solution:**
- Ensure previous releases have tags (v0.1.0, v0.2.0, etc.)
- Use conventional commit message prefixes (feat:, fix:, etc.)
- Manually edit the release description after creation if needed

### Problem: Release has wrong version tag

**Cause:** This was the original bug - now fixed!

**Solution:**
- The workflow now correctly uses `${{ github.ref_name }}` from the tag trigger
- Verification step warns if tag doesn't match csproj version

## Manual Release (Fallback)

If workflows fail, you can create a release manually:

```bash
# 1. Update version
# Edit CAP.Desktop/CAP.Desktop.csproj, set <Version>0.4.0</Version>

# 2. Commit and tag
git add CAP.Desktop/CAP.Desktop.csproj
git commit -m "chore: update version to 0.4.0"
git tag v0.4.0
git push origin main
git push origin v0.4.0

# 3. Build locally
dotnet publish CAP.Desktop/CAP.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
dotnet publish CAP.Desktop/CAP.Desktop.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# 4. Create GitHub release manually with the built artifacts
```

## Security Notes

### Required Permissions

The workflows require these permissions:
- `contents: write` - To create tags and releases
- `GITHUB_TOKEN` - Automatically provided, no secrets needed

### No External Dependencies

The changelog generation uses only built-in git commands and bash:
- No external services or APIs
- No API keys or secrets required
- Runs entirely in GitHub Actions

## Future Improvements

Potential enhancements for the release process:

1. **AI-Generated Summaries**: Use Claude API to generate high-level release summaries
2. **Release Notes Template**: Add PR descriptions to changelog
3. **Auto-Update Checker**: Implement in-app update notifications
4. **Pre-Release Support**: Add workflow for beta/rc versions
5. **Rollback Support**: One-click rollback to previous version
