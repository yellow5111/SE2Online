# Simultaneous control-panel crash fix

Unofficial local modification of SE2 Online 0.18.4, based on yellow5111/SE2Online commit `40566cfc1b93700d8e334218832352f25bedfe1e`, for Space Engineers 2 `4.0.0.3169`. Original copyright and license remain in `LICENSE`.

## Reproduction and cause

The host and a guest open control panels on the same grid while the grid has block groups. The captured host log first reports that a `GridGroup` is already replicated in another observable hierarchy. Registration then fails with `NoMatchingLocalPairingException`. A later grid-mass update retries the incomplete collection registration and crashes with a duplicate `ServerObservedCollection<ObservableList<ControlPanelGroupModel>>` key.

Each viewer already has separate panel models, but their replicated `_group` fields point to the same world group. The fix gives each panel's grid model its own replicated group copies. Server model getters and edits retain the original world groups. Copies follow live property and subgroup changes, preserve group identities within one panel, and unsubscribe when the grid panel closes. Group toolbar requests translate a received copy back to its world group before native validation.

The native member names, types, ordering and attributes remain the same. The network protocol and saved-world format are unchanged.

## Build and check

Use .NET 9 and your installed game assemblies:

```powershell
dotnet build Y51_SE2Online/Y51_SE2Online.csproj -c Release /p:SE2Bin="E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2"
dotnet run --project Tests/ControlPanelGroupChecks/ControlPanelChecks.csproj -- "E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2" "Y51_SE2Online\bin\Release\Y51_SE2Online.dll"
```

Omit the plugin DLL argument to run the same reproduction against the unmodified native hierarchy. It must fail when the second panel registers the shared group.

The check uses the actual native panel factory and hierarchy registration. It covers nested group aliasing, live names/visibility/depth and subgroup copies, native server edits, disposal/reopening, metadata compatibility and toolbar argument restoration. It starts no game, world or peer transport. It does not verify the entire group-move UI or live multiplayer behavior.

## Install and restore

Close Space Engineers 2 before replacing files. Back up your current `Y51_SE2Online.dll` and matching PDB, then replace those two files with the Release output. Keep your existing plugin content, Harmony DLL, settings and launch options. This is a host-side repair and retains the 0.18.4 wire format.

Restart the host, let the guest join, and repeat simultaneous panel opens on the affected grid. To roll back, close the game and restore the two backed-up files. An upstream update can replace this local build; keep this source and the patch if rebasing is needed.
