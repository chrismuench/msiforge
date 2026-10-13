# GitHub Branch Protection Settings for MsiForge

## Recommended Branch Protections for master/main

After your repo is public, configure these branch protection rules in GitHub:

### 1. Require pull request reviews before merging
- **Settings**: Settings → Branches → Branch protection rules → master
- **Required**: Yes
- **Options**:
  - Require a pull request before merging
  - Require approvals (2 minimum for a public project)
  - Allow force-requests (uncheck for public projects)
  - Allow auto-merge (optional)

### 2. Disable force pushes
- **Required**: Yes
- Prevents accidental/careless history rewriting

### 3. Disable deleting branches
- **Required**: Yes
- Protects against accidental branch deletion

### 4. Require status checks before merging
- **Required**: Yes
- **Checks**:
  - Build (Windows, macOS, Linux)
  - Test
  - Self-test
- Ensures all CI checks pass before merging

### 5. Include administrators (optional but recommended)
- **Required**: Yes for public projects
- Even repo admins must follow protection rules

## Recommended Repository Settings

### 1. Enable Issue Templates
Already done ✅
- bug_report.md
- feature_request.md

### 2. Enable Pull Request Templates
Create `.github/PULL_REQUEST_TEMPLATE.md`

### 3. Enable Security Policy
Create `.github/SECURITY.md`

### 4. Enable Discussions
Go to Settings → Features → Enable Discussions

### 5. Set up Dependabot
Create `.github/dependabot.yml` for automatic dependency updates

## Additional Recommendations

### 1. Create a SECURITY.md file
Report security issues privately before public disclosure

### 2. Set up automatic closing of old issues
Use GitHub Actions to close stale issues

### 3. Configure issue auto-labeling
Use GitHub Actions to automatically label issues

### 4. Set up CODEOWNERS
Create `.github/CODEOWNERS` to assign reviewers

## Quick Setup Commands

After enabling branch protection in GitHub UI, you may want:

```bash
# Protect master branch locally too
git update-ref refs/heads/master
git config branch.master.remote origin
git config branch.master.merge refs/heads/master
```

## What We Already Have ✅
- Issue templates (bug_report.md, feature_request.md)
- LICENSE.txt (MIT)
- CONTRIBUTING.md
- CODE_OF_CONDUCT.md
- CI/CD workflows

## What to Add Now
1. Branch protection rules (master branch)
2. SECURITY.md
3. PULL_REQUEST_TEMPLATE.md
4. Dependabot config (optional but recommended)
