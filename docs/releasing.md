# Releasing and updates

Night-loop updates itself from this repository's GitHub Releases using [Velopack](https://velopack.io). The
server isn't involved, so updates still arrive when the server is down.

## Making a release

1. Optional: write the notes in `docs/releases/v<version>.md` (for example `docs/releases/v0.2.0.md`). They
   become the GitHub Release text and show in Settings → Updates.
2. Start the release, either way:
   - **Actions** tab → **Release** → **Run workflow**, type the version (like `0.2.0`), run it on `main`; or
   - push a tag: `git tag v0.2.0 && git push origin v0.2.0`.
3. `.github/workflows/release.yml` tests, publishes a self-contained build, checks every screen loads, packs it
   with `vpk` and publishes the release with:
   - `NightLoop-win-Setup.exe`: the installer people download once,
   - `NightLoop-win-Portable.zip`: no install, still updates itself,
   - `NightLoop-<version>-full.nupkg` and, from the second release on, a small `-delta.nupkg`, plus
     `releases.win.json`: what installed apps download.

Versions are `MAJOR.MINOR.PATCH` and must go up. Pre-releases are ignored by the updater.

## What the app does

| When | What happens |
| --- | --- |
| Launch (and Retry on the loading screen) | Checks GitHub. If there's a newer release it downloads it, installs it and reopens before you sign in. Gives up after 8 s if GitHub doesn't answer. |
| Home → Refresh | Fetches announcements and checks for updates. A new version installs and restarts straight away, unless a macro is running. |
| Settings → Check now | Same as Refresh, for updates only. |
| Every 4 hours while open | Downloads quietly and shows **Update ready · Restart** in the sidebar, Home and Settings. Never restarts on its own. |
| Server says `426 update_required` | The loading screen shows **Update now**. |

Before restarting, the app sends the last macro edit to the server and stops every macro, releasing held keys.
A download that fails leaves the current version working and is tried again next time.

Updates only work in a copy installed from a release. Running from Visual Studio or `dotnet run` shows
"Updates off" in Settings.

## Announcements

The Home screen's **Updates & announcements** box is [`announcements.md`](../announcements.md) on `main`. Edit
it on GitHub and the app picks it up when Home opens (at most once a minute), on Refresh, and every 10 minutes.
It supports `# headings`, `- bullets` and paragraphs; `<!-- comments -->` are hidden. The last copy is saved in
`%LOCALAPPDATA%\NightLoop\announcements.md` so it shows offline too.

The app reads it through the GitHub API (no delay, 60 requests an hour per IP) and falls back to
raw.githubusercontent.com (cached by GitHub for up to about 5 minutes).

## Code signing

Builds aren't signed yet, so Windows SmartScreen warns on first run. `vpk pack` takes `--signTemplate` (or
`--signParams` with a certificate) when there is one; keep the certificate in a GitHub Actions secret.
