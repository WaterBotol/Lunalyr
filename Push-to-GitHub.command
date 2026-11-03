#!/bin/bash
# Optional owner-run upload, when the ChatGPT connection cannot write the repo.
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")" && pwd -P)"
cd "$PROJECT_DIR"
if ! command -v git >/dev/null 2>&1; then
  echo "Git is required. Use GitHub Desktop, or the Git that comes with Xcode’s command-line tools."
  exit 1
fi
if ! git rev-parse --git-dir >/dev/null 2>&1; then
  git init -b main
elif [[ "$(cd -- "$(git rev-parse --show-toplevel)" && pwd -P)" != "$PROJECT_DIR" ]]; then
  echo "Put the project files at the repository root before using this upload command."
  exit 1
fi
if [[ "$(git branch --show-current)" != main ]]; then
  echo "This upload command expects main. It will not change your current branch."
  exit 1
fi
ORIGIN_URL="$(git config --get remote.origin.url || true)"
case "$ORIGIN_URL" in
  "") git remote add origin https://github.com/WaterBotol/Lunalyr.git ;;
  https://github.com/WaterBotol/Lunalyr.git|https://github.com/WaterBotol/Lunalyr|git@github.com:WaterBotol/Lunalyr.git) ;;
  *) echo "This folder points to another repository; nothing was uploaded."; exit 1 ;;
esac
PUSH_URL="$(git config --get-all remote.origin.pushurl || true)"
case "$PUSH_URL" in
  ""|https://github.com/WaterBotol/Lunalyr.git|https://github.com/WaterBotol/Lunalyr|git@github.com:WaterBotol/Lunalyr.git) ;;
  *) echo "The push destination points to another repository; nothing was uploaded."; exit 1 ;;
esac
if ! git diff --cached --quiet; then
  echo "There are already staged changes. Commit or unstage them before using this upload command."
  exit 1
fi
if command -v gh >/dev/null 2>&1 && gh auth status --hostname github.com >/dev/null 2>&1; then
  # Repository-local helper only; no token is printed or written into source.
  git config --replace-all credential.https://github.com.helper ''
  git config --add credential.https://github.com.helper '!gh auth git-credential'
  if ! git var GIT_AUTHOR_IDENT >/dev/null 2>&1; then
    GH_IDENTITY="$(gh api user --jq '[.login, (.id | tostring)] | @tsv')"
    IFS=$'\t' read -r LUNALYR_GH_LOGIN LUNALYR_GH_ID <<< "$GH_IDENTITY"
    if [[ ! "$LUNALYR_GH_LOGIN" =~ ^[A-Za-z0-9-]+$ || ! "$LUNALYR_GH_ID" =~ ^[0-9]+$ ]]; then
      echo "Could not read your GitHub author identity. No commit was made."
      exit 1
    fi
    if [[ -z "$(git config user.name || true)" ]]; then git config user.name "$LUNALYR_GH_LOGIN"; fi
    if [[ -z "$(git config user.email || true)" ]]; then
      git config user.email "$LUNALYR_GH_ID+$LUNALYR_GH_LOGIN@users.noreply.github.com"
    fi
  fi
fi
if ! git var GIT_AUTHOR_IDENT >/dev/null 2>&1; then
  echo "Sign in first: gh auth login --hostname github.com --git-protocol https --web --scopes workflow"
  echo "Then run this command again. Alternatively, upload this folder with GitHub Desktop."
  exit 1
fi
git add -- Sources Scripts Tests Tools .github .gitignore \
  README.md CHANGES.md VALIDATION.md INTEROPERABILITY.md UPDATE_SETUP.md THIRD_PARTY_NOTICES.md \
  LICENSE UPSTREAM.json UPSTREAM_README.md project.yml \
  Build.command Run-Tests.command Measure-Performance.command Push-to-GitHub.command
if ! git diff --cached --quiet; then
  git commit -m "Update Lunalyr lyrics, audio response and GitHub builds"
fi
echo "Pushing to WaterBotol/Lunalyr. GitHub authentication may be required."
echo "If sign-in fails: gh auth login --hostname github.com --git-protocol https --web --scopes workflow"
# No force push: remote work is never overwritten.
git push -u origin main
echo "Uploaded. The Mac build will appear in the repository’s Actions tab."
