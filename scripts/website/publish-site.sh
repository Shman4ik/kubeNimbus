#!/usr/bin/env bash
# Publishes the project landing page (website/index.html + assets pulled from
# design/masters, design/screenshots and .github/assets) to the root of the
# gh-pages branch, which GitHub Pages serves at https://shman4ik.github.io/kubeNimbus/.
#
# The same mechanics as pgNimbus's scripts/website/publish-site.sh: the page is
# hand-written static HTML with no external requests, the images are copied from
# where the README already takes them, and publishing is manual. Run it from an
# up-to-date main after the PR that changed the page has merged.
#
# The repository had no gh-pages branch when this was written, so the first run
# starts one as an orphan branch (no history shared with main). GitHub Pages then
# has to be pointed at it once: Settings > Pages > Build and deployment >
# "Deploy from a branch", branch gh-pages, folder / (root).
#
# The script replaces only index.html, .nojekyll and assets/ at the root of
# gh-pages; anything else a later tool puts on that branch is left alone.
#
# Usage:
#   scripts/website/publish-site.sh                                 # stage, commit, and push
#   KUBENIMBUS_SITE_DRY_RUN=1 scripts/website/publish-site.sh       # stage only, print the dir
set -euo pipefail

repo_root="$(git -C "$(dirname "$0")" rev-parse --show-toplevel)"
cd "$repo_root"

assets=(
  design/masters/logo/wordmark-light.png
  design/masters/logo/wordmark-dark.png
  design/masters/logo/social-preview.png
  design/masters/icon/icon-256.png
  .github/assets/bmc-button.svg
  design/screenshots/applications-list.light.png
  design/screenshots/applications-list.dark.png
  design/screenshots/application-page.light.png
  design/screenshots/workloads-list.dark.png
  design/screenshots/pod-detail.dark.png
  design/screenshots/exec-terminal.dark.png
  design/screenshots/cluster-switcher.light.png
  design/screenshots/fleet-list.dark.png
  design/screenshots/applications-demo.gif
  design/screenshots/palette-logs-demo.gif
)

# Fail before touching gh-pages if a file the page links to has moved or been renamed.
for a in "${assets[@]}"; do
  if [[ ! -f "$a" ]]; then
    echo "Missing asset: $a (website/index.html links to it as assets/$(basename "$a"))" >&2
    exit 1
  fi
done

worktree="$(mktemp -d)"
# Only used when gh-pages does not exist yet: the orphan branch the first commit is made on.
orphan_branch="website-publish-orphan"
cleanup() {
  git worktree remove --force "$worktree" 2>/dev/null || rm -rf "$worktree"
  git worktree prune
  git branch -D "$orphan_branch" >/dev/null 2>&1 || true
}
trap cleanup EXIT

if git ls-remote --exit-code --heads origin gh-pages >/dev/null; then
  git fetch origin gh-pages
  git worktree add --detach "$worktree" origin/gh-pages
else
  echo "origin has no gh-pages branch yet; starting one as an orphan branch."
  git worktree add --orphan -B "$orphan_branch" "$worktree"
fi

rm -rf "$worktree/assets"
mkdir -p "$worktree/assets"
cp website/index.html "$worktree/index.html"
# Serve the files as they are, without a Jekyll build.
touch "$worktree/.nojekyll"
for a in "${assets[@]}"; do
  cp "$a" "$worktree/assets/$(basename "$a")"
done

if [[ "${KUBENIMBUS_SITE_DRY_RUN:-0}" == "1" ]]; then
  echo "Staged site in $worktree (dry run, not committing):"
  ls -la "$worktree" "$worktree/assets"
  trap - EXIT   # keep the worktree around for inspection
  echo "Remove it afterwards with: git worktree remove --force \"$worktree\""
  exit 0
fi

git -C "$worktree" add -A
if git -C "$worktree" rev-parse --verify --quiet HEAD >/dev/null \
   && git -C "$worktree" diff --cached --quiet; then
  echo "Site is already up to date on gh-pages."
  exit 0
fi
git -C "$worktree" commit -m "Update project landing page (from website/index.html @ $(git rev-parse --short HEAD))"
git -C "$worktree" push origin HEAD:refs/heads/gh-pages
echo "Published to https://shman4ik.github.io/kubeNimbus/"
