# LANCommander GOG plugin

A plugin for LANCommander servers that has the ability to link GOG accounts and import games from them. Built into the plugin is a library to extract data from GOG offline Windows installers. Scripts are also generated from compiled Inno Setup scripts.

## Layout

| Path | What it is |
| --- | --- |
| `src/LANCommander.GOG` | Reads GOG's Inno Setup installers as data. |
| `src/LANCommander.GOG.Packaging` | Turns an installer into an `.lcx` package. |
| `src/LANCommander.Plugins.GOG` | The server plugin: GOG sign-in and API, the import queue and worker, and the admin pages. |
| `tests/` | xunit and bUnit tests for all of the above. |

## Installing

On a LANCommander server (2.2.0-alpha.2 or later), open **Settings → Plugins**. Find GOG on the **Browse**
tab and install it, or download the `.nupkg` attached to a
[release](https://github.com/LANCommander/LANCommander.Plugins.GOG/releases) and upload it on the
**Installed** tab. The plugin is installed when the server restarts.

## Using it

The plugin adds a **GOG** entry to the server's navigation for administrators.

**Accounts.** Open the GOG sign-in page from the Accounts page and sign in. GOG then shows
a mostly blank page at `https://embed.gog.com/on_login_success?...&code=...`. Paste that address
back and link the account. Tokens refresh by themselves. If GOG stops accepting them, the account
is flagged and has to be linked again.

**Library.** Lists the games of one account or all of them. Choose the installer language and the
archive storage location, select games, and import them. A game that was imported before is
updated with a new version rather than added again.

**Imports.** Shows each import's stage (looking up, downloading, extracting, packaging,
importing), with progress and download speed, and lets you cancel and retry. A failed import keeps
its downloaded installer, so retrying it resumes the download. History survives restarts. An
import that a restart cut short shows as failed, ready to retry.

**Settings.** Sets the work directory, how many imports run at once (takes effect on restart), the
default language and storage location, checksum verification, and whether to keep work files.

Each import needs room in the work directory for the installer, the extracted game and the package
at once. The installer and the extracted game are deleted before the server copies the archive out
of the package.

## Limitations

- Only offline Windows installers are imported. Games GOG offers only for Linux or macOS fail
  with a message that says so.
- DLC, patches, language packs and extras are not imported.
- Installers `LANCommander.GOG` cannot read fail with its explanation: Inno Setup 6 builds, ANSI
  builds, bzip2 or encrypted payloads, and the RAR-wrapped installers some pre-2013 builds used.
