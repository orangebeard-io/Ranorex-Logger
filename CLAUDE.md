# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a .NET library (`Orangebeard.RanorexListener`) that implements Ranorex's `IReportLogger` interface to report Ranorex test results to the Orangebeard test observability platform. It is published as a NuGet package.

**Target framework:** `net48` (Ranorex 10.x). The project previously multi-targeted `net462` and `net48`; net462 was dropped.

## Build

Build requires Ranorex Studio to be installed at `C:\Program Files (x86)\Ranorex\Studio\Bin\` — the project references `Ranorex.Core.dll` and `Ranorex.Libs.Util.dll` from that path via `<HintPath>`.

If Ranorex Studio isn't installed, a local, untracked `rxLibs/` folder can hold copies of the needed DLLs (`Ranorex.Core.dll`, `Ranorex.Libs.Util.dll`, and — only needed by the test project, see below — `Ranorex.Common.Net35.dll`) as a fallback. The canonical source for these is the private `orangebeard-io/rx-libs` repo. `rxLibs/` is intentionally not committed here: Ranorex's DLLs are proprietary and shouldn't be redistributed in this public repo. Both `RanorexOrangebeardListener.csproj` and `RanorexOrangebeardListenerTests.csproj` reference both locations via `Exists()`-conditioned `<Reference>` entries, preferring the real Ranorex Studio install when present. CI populates `rxLibs/` by checking out `rx-libs` directly — see Release process below.

```powershell
# Restore packages
msbuild -t:restore RanorexOrangebeardListener.sln

# Build (Release generates the NuGet package due to <GeneratePackageOnBuild>true</GeneratePackageOnBuild>)
msbuild RanorexOrangebeardListener.csproj /p:Configuration=Release /p:NoWarn=1591

# Build (Debug)
dotnet build RanorexOrangebeardListener.csproj
```

`RanorexOrangebeardListenerTests/` covers the pure/static helper logic (string formatting, log-level mapping, screenshot dedup hashing, `TypeTree`) via MSTest + Moq — run with `dotnet test RanorexOrangebeardListenerTests/RanorexOrangebeardListenerTests.csproj`. The Ranorex-event-driven logic (`LogText`, `LogData`, `HandlePotentialStartFinishLog`, `DetermineStartTestItemRequest`, and the rest of the item-lifecycle handling) is still untested: `OrangebeardAsyncV3Client` (from `Orangebeard.Client`) has no virtual members or interface to mock, and the Ranorex state it reads (`ActivityStack.Current`, `TestSuite.Current`, `TestModuleLeaf.Current`) is static engine state with no test seam — that logic is validated by integrating the library into a live Ranorex test suite instead.

## Architecture

The entire logger lives in two layers:

### `OrangebeardLogger` (root class, `OrangebeardLogger.cs`)
Implements Ranorex's `IReportLogger`. Ranorex calls its two entry points during a test run:
- `LogText` — receives structured start/finish lifecycle events (via the `activity` key in `metaInfos`) and plain log messages
- `LogData` — receives binary data, currently only `System.Drawing.Image` screenshots

Screenshots reach Orangebeard through two paths that can overlap: Ranorex calls `LogData` directly, and on a failed step `LogErrorScreenshots` separately walks `ActivityStack.Current.Children` looking for `ReportItem`s with a `ScreenshotFileName`. Both paths funnel through `LogData`, which records every filename it sends in `_reportedErrorScreenshots`; `LogErrorScreenshots` skips filenames already in that set. This is what prevents duplicate screenshots and screenshots from earlier test cases leaking into a later failing test.

`LogText` routes to one of two paths:
1. **Lifecycle events** (`HandlePotentialStartFinishLog`): `activity` key present → start or finish a suite/test/step on Orangebeard
2. **Log entries**: no `activity` key → send a log line, optionally with a file attachment

### `RunContext/` — type tracking
- **`TypeTree`**: a linked tree node tracking the nesting of Ranorex items as they open and close. Each node holds a `Type` string (`"suite"`, `"test"`, `"step"`, `"before"`, `"after"`). The current node is `_tree` in `OrangebeardLogger`; it descends on start and ascends (`GetParent()`) on finish.
- **`ItemCreationData`**: a plain DTO carrying the resolved name, type, description, attributes, and start time for a new item before it is sent to Orangebeard.

### Ranorex → Orangebeard type mapping
The trickiest logic is `DetermineStartTestItemRequest`, which maps Ranorex activity types to Orangebeard item types:

| Ranorex activity | Orangebeard type |
|---|---|
| `testsuite` | `suite` |
| `testcontainer` (smart folder) | `suite` if outside a test case, `step` if inside |
| `testcontainer` (test case) | `test` |
| `smartfolder_dataiteration` / `smartfolder_runiteration` | `suite` or `step` (same context rule) |
| `testcase_dataiteration` / `testcase_runiteration` | `test` |
| `testmodule` (setup node) | `before` or `step` |
| `testmodule` (teardown node) | `after` or `step` |
| `testmodule` (regular) | `step` |

The flag `_isTestCaseOrDescendant` tracks whether execution is currently inside a test case, which drives the suite-vs-step decision for smart folders.

### `EnsureReportingIsInSync`
Guards against the logger being attached after a test suite has already started. If the Orangebeard context has no active suite when the first lifecycle event arrives, it auto-creates the top-level suite from `TestSuite.Current` before processing the actual event.

### Orangebeard client
`OrangebeardAsyncV3Client` (from the `Orangebeard.Client` NuGet package, v3.1.0) is the async HTTP client. It manages `TestRunContext` (active test run, suite stack, active test/step UUIDs). Configuration is read from environment variables or `orangebeard.json` via `OrangebeardConfiguration`.

## Release process

Releases are automated via GitHub Actions (`.github/workflows/release.yml`). On every push to `master`:
1. The minor version is bumped in the `.csproj` automatically.
2. `orangebeard-io/rx-libs` (a private repo holding `Ranorex.Core.dll`, `Ranorex.Libs.Util.dll`, `Ranorex.Common.Net35.dll` — see the Build section above) is checked out into `rxLibs/`, authenticated via the `RX_LIBS_TOKEN` secret.
3. The project is built with MSBuild, using the `rxLibs/` fallback references.
4. A GitHub release is created and tagged with the version.
5. The NuGet package is published to nuget.org via [Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) — a short-lived API key obtained via GitHub OIDC (`NuGet/login@v1`), not a stored `NUGET_API_KEY` secret. This requires a `NUGET_USER` secret (the nuget.org profile name) and a Trusted Publishing policy on nuget.org whose **Workflow File** is set to exactly `release.yml`.

**Do not manually bump the version** — the CI pipeline handles it.
