# Contributing to Lunima

`main` is protected — all changes go through a pull request.

## Workflow

1. Branch off `main` (`feat/...`, `fix/...`, `docs/...`).
2. Keep the PR **small and focused — one concern per PR.** Big, mixed PRs are the
   main thing that makes review painful; split them.
3. Open the PR. To merge you need the **`🔍 xUnit Tests`** check green and
   **1 approval**.
4. Merge, then delete the branch.

## Build & test

```bash
dotnet build
python3 tools/smart_test.py        # run tests (avoid raw `dotnet test` — it floods the console)
```

## Architecture (the bar that matters)

Keep the layering and MVVM clean — this is what we look at in review:

- **Core is UI-free.** `Connect-A-Pic-Core` (domain, simulation, S-matrix) must not
  depend on Avalonia, ViewModels, or Views. UI/ViewModels live in `CAP.Avalonia`,
  data access in `CAP-DataAccess`.
- **MVVM** (CommunityToolkit): logic and state go in ViewModels
  (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`); Views only bind.
  No business logic in code-behind.
- One responsibility per class; add tests for new logic.
- **Cross-platform parity.** Lunima targets macOS, Linux, and Windows *together* — don't let
  one OS fall behind. Never call `Process.Start` directly or hardcode OS shell commands: use
  `ProcessLaunchFactory` for external tools (python/docker) and `IUrlLauncher` for opening
  URLs/files. Use `Path.Combine` and `InvariantCulture` for machine output; condition
  Windows-only `.csproj` properties; and keep the release matrix (Windows MSI + portable,
  Linux tarball, macOS `.dmg`) in sync. Details: [CLAUDE.md §1.2](CLAUDE.md).

Full rules: [CLAUDE.md](CLAUDE.md).
