# Control-panel regression

Requires .NET 9 and your installed SE2 `Game2` directory. This loads native metadata and exercises real panel factories and observable hierarchy registration without starting a game, world, or transport.

```powershell
dotnet run --project Tests/ControlPanelGroupChecks -c Release -- "E:\SteamLibrary\steamapps\common\SpaceEngineers2\Game2" "Y51_SE2Online\bin\Release\Y51_SE2Online.dll"
```

Omit the DLL argument to reproduce the stock second-viewer `GridGroup` hierarchy assertion. The patched run checks hierarchy isolation, original server identities, unchanged wire members, toolbar argument restoration, mirrored nested groups, edits, close and reopen. A real host/guest reproduction is still required to validate transport and UI behavior.
