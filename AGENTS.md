# LinuxFiles development

LinuxFiles is an independent Linux-only file manager built with C#/.NET and Uno Platform (Skia desktop, X11/XWayland). There is no upstream sync or Windows support. Preserve inherited licenses, notices and contributor attribution; see [upstream policy](docs/linux-port/upstream-policy.md).

## Scope and style

- Identify the smallest relevant project and read nearby code before changing it.
- Follow `.editorconfig`, existing MVVM, service, command and localization patterns. Keep comments concise and avoid hard-coded user-visible strings.
- Preserve CRLF on files that use it. Avoid generated files, unrelated cleanup and dependency changes.
- Use targeted `rg` searches and cap potentially large command output. Before deleting files or types, check all references, including XAML, project exclusions and tests, for Linux consumers.
- Treat file operations, clipboard, drag/drop, previews, archives, settings persistence and localization as high-risk areas.
- Route OS functionality through `Files.Platform.Abstractions` and `Files.Platform.Linux`. Prefer existing native wrappers and source-generated `LibraryImport`; avoid ad hoc interop and runtime reflection that cannot be proven safe for trimming/AOT.
- Treat filenames, desktop entries and archive contents as untrusted. Avoid shell interpolation; use existing fd-relative helpers for recursive destructive operations.

## Build and verify

```sh
dotnet build src/Files.App -f net10.0-desktop
dotnet build tests/Files.Platform.Tests
dotnet test --project tests/Files.Platform.Tests/Files.Platform.Tests.csproj --no-build
```

Use the .NET SDK at `/usr/local/share/dotnet` (11.0.100-rc.1). The 10.x SDKs
(`/opt/homebrew/bin/dotnet`, `~/.dotnet`) do not run the `[GeneratedDependencyProperty]`
source generator, so `Files.App.Controls` fails with ~118 `CS9248`/`CS0759`
"partial property must have an implementation part" errors:

```sh
export PATH=/usr/local/share/dotnet:$PATH
```

Build affected projects first and run builds sequentially. Report checks performed and anything unverified. If the desktop toolchain is unavailable, use reference searches and state that the build was not run.

Use `scripts/linux/headless-run.sh` for UI checks on a private display with a throwaway home. Do not launch against the user's display or alter their desktop settings. Clean up processes you start and never commit private home data or screenshots.

## Git and reviews

Do not revert unrelated changes. Commit or push only when requested; stage only task files. Before a commit, run `git status --short` and `git diff --check`. Use a concise message describing the behavior change.

For requested PRs, use `.github/PULL_REQUEST_TEMPLATE.md` and prefix the title with `Fix:`, `Feature:` or `Code Quality:`. Target this fork, never upstream. Include validation and remaining limitations.
