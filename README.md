[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Git.Runners.Linux/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/Soenneker.Git.Runners.Linux/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Git.Runners.Linux/daily-automatic-update.yml?style=for-the-badge&label=Daily%20Update)](https://github.com/soenneker/Soenneker.Git.Runners.Linux/actions/workflows/daily-automatic-update.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Git.Runners.Linux/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/Soenneker.Git.Runners.Linux/actions/workflows/codeql.yml)

# Soenneker.Git.Runners.Linux

Defines the build library util contract.

> This is an automation runner, not a package intended for application consumption.

## What the runner does

- `IBuildLibraryUtil.Build(cancellationToken)` — Builds build Library.
- `IFileOperationsUtil.Process(filePath, cancellationToken)` — Processes the pending work managed by the file operations.
- `Constants.FileName` — The file name.
- `Constants.Library` — The library.
- `ConsoleHostedService.StartAsync(cancellationToken)` — Starts the console hosted service and begins its background work.

## What you get

- `IBuildLibraryUtil` — Defines the build library util contract.
- `IFileOperationsUtil` — Defines the file operations util contract.
- `Constants` — Represents the constants.
- `ConsoleHostedService` — Represents the console hosted service.

## API at a glance

| API | What it does | Result / important behavior |
| --- | --- | --- |
| `IBuildLibraryUtil.Build(cancellationToken)` | Builds build Library. | A task whose result is the text returned by build. |
| `IFileOperationsUtil.Process(filePath, cancellationToken)` | Processes the pending work managed by the file operations. | A task that completes when the full processing workflow has finished. |
| `ConsoleHostedService.StartAsync(cancellationToken)` | Starts the console hosted service and begins its background work. | A task that completes after the console hosted service has started. |
| `ConsoleHostedService.StopAsync(cancellationToken)` | Stops the console hosted service and waits for its background work to finish. | A task that completes after the console hosted service has stopped. |

## Practical notes

- Cancellation stops pending work; it does not undo work that has already completed.
