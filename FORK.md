# Fork notes (branch `self-healing`)

This fork of [rogerfar/rdt-client](https://github.com/rogerfar/rdt-client) makes Sonarr/Radarr/Lidarr recover on their own from releases the debrid provider can't handle.

Image: `ghcr.io/neihtq/rdtclient:latest`, built by `.github/workflows/fork-docker-image.yml` on every push to `self-healing`.

## Changes compared to upstream
- `torrents/add` returns **409 Conflict** for permanent provider errors (e.g. "Infringing file"), so the *arr skips the release and tries the next one. Temporary errors still return `Fails.`. Rejected torrents are deleted.
- Permanently rejected hashes are stored in the `RejectedTorrents` table and refused immediately when added again.
- Opt-in `ArrNotifier`: when a torrent errors or stalls, the item is removed from the *arr queue with blocklist, which triggers a new search.
- Settings: *Settings → qBittorrent / \*darr → Rejected releases* and *\*arr failure notifications*.

## Rebasing on upstream
```sh
git fetch upstream --tags
git rebase upstream/main
cd server && dotnet test RdtClient.sln
git push --force-with-lease
```

Note: `.gitignore` contains `data`. On case-insensitive file systems it also matches `RdtClient.Data/Data` and `Models/Data`, so new files there need `git add -f`.
