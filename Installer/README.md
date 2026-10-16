# Space Engineers 2 Online Installer

A small Windows app that installs the SE2 Online plugin and sets Space Engineers 2's Steam launch options for you.
It is an installer, not a launcher: once it has run, you start the game from Steam as usual, and the installer
deletes itself when you close it after a successful install.

## What it does

1. Finds Steam, your Space Engineers 2 install and the Steam accounts on the PC.
2. Downloads the newest plugin release listed by `https://se2-online.yellow51.dev/version.php` from the SE2 Online
   release storage, and refuses it unless its size, SHA-256 and ECDSA P-256 release signature all match. This is the
   same check the plugin's own auto-update uses (`Core/AutoUpdate.cs`, linked into this project).
3. Unpacks it into the folder you choose (default `C:\SE2Online`). Your SE2 Online settings and favorites in that
   folder are kept on update.
4. Optionally adds `-plugins:<folder>\Y51_SE2Online.dll` to SE2's launch options for the chosen Steam account. Other
   launch options and other plugins are kept; an older SE2 Online path is replaced. Steam only saves launch options
   while it is closed, so the installer asks, closes Steam, edits `userdata\<account>\config\localconfig.vdf` (a
   backup is written beside it as `localconfig.vdf.se2online-backup`) and starts Steam again.

Uninstall removes the plugin files and the launch option (for every account). Tick "Also delete my SE2 Online
settings and favorites" to remove those too.

Using Pulsar? Install SE2 Online from the Pulsar PluginHub in game instead, and don't load it both ways.

## Building

Needs the .NET 10 SDK on Windows.

    dotnet publish Installer/SE2OnlineInstaller.csproj -c Release

The result is one self-contained `SE2OnlineInstaller.exe` (no .NET install needed to run it). Official releases are
additionally Authenticode-signed; a build you make yourself works the same, unsigned.

Command line, for testing:

- `--check report.txt` writes what it detects and proves, without changing anything, that rewriting each account's
  `localconfig.vdf` would only change SE2's launch options.
- `--install-only <folder>` downloads, verifies and installs the plugin there without touching Steam.
- `--screenshot shot.png` saves a picture of the window and exits.

The backdrop is drawn in code; the installer contains none of Keen Software House's artwork.

MIT License, see `../LICENSE`.
