# sloffer-watcher

Polls a ClickUp list and turns tasks into headless Claude Code runs for **Sloffer** (see `CLAUDE.md` for build, run and config).

```powershell
dotnet user-secrets set "ClickUp:ApiToken" "pk_..." --project src/SlofferWatcher
dotnet run --project src/SlofferWatcher
```

Starts in **DryRun** mode: it only logs what it would do.
