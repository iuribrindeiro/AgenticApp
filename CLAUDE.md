# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

Three projects exist: `src/Domain` (the F# domain), `tools/Mcp` (the MCP server), and `tools/McpAudit`
(the coverage guard). The adapters — REST, gRPC, SignalR, Orleans — and the MongoDB repositories are not
built yet. **There are no tests**; do not add or run them until asked.

Target **.NET 10** everywhere and use the **latest stable version of every package/framework** unless a
pin is explicitly justified in the fsproj/csproj.

## Architecture

- **Domain** — an **F# class library** (`src/Domain`). It exposes only *types* and *pure functions*. No
  I/O, no async over I/O, no dependency on any other project in the solution. All business logic lives
  here, and it has **no package references at all** — exposure over MCP is entirely `tools/Mcp`'s job.
- **Mcp** (`tools/Mcp`) — references Domain, and is also the **MCP server itself** (stdio). Holds
  everything about *exposing* the domain over MCP: the
  value-object JSON converters (deserialization routes through `Make`, so it validates rather than
  forges) and `DomainTools.all()`, which builds the tool list with both serializer and schema options.
  Serialization concerns live here, never in Domain. It sits under `tools/` because the MCP server is a
  development-only surface for agent clients, not part of the shipped application.
- **Adapters** — REST API, gRPC services, SignalR hubs (Redis backplane), and Orleans grains. These are
  **thin**: parse/validate input, call a Domain function, map the result out. They may call repositories
  to fetch and persist data. They must not contain business logic — if a rule needs writing, it belongs
  in the F# Domain project.
- **Repositories** — MongoDB is the data store. Repositories only fetch and persist; no business rules.
  They are injected into adapters and are **never** referenced from Domain code.

## MCP tool parity (non-negotiable)

The app also hosts an **MCP server, enabled in Development only**, which Claude Code attaches to.
Every one of the following must have a corresponding MCP tool that invokes it directly:

- each REST API endpoint
- each gRPC method
- each SignalR hub method
- each Orleans grain method
- each publicly exposed F# Domain function

Adding one of these without its MCP tool is an incomplete change. Use the matching skill so the tool is
registered in the same pass: `/add-rest-endpoint`, `/add-grpc-method`, `/add-signalr-method`,
`/add-orleans-grain-method`, `/add-domain-function`.

Registration is **by convention, not by attribute** — there is nothing to forget. `DomainTools.all()`
exposes every public Domain function whose parameters are all *safe* — meaning both **unforgeable** (a
deserializer cannot produce a value the domain would reject) and **schematisable** (it can be given a
schema a model can fill). Wire shapes, value objects, unions, and records and lists built from those all
qualify. If a public function ever needs to be kept off the wire, add an opt-out attribute then — there
is none today because nothing has needed one.

- **The tool's name is its code path** — `Store.rename`, `ReqStr.create`.
- **Its description is its `///` doc comment**: `<summary>` becomes the tool description and each
  `<param name="x">` becomes that parameter's schema description. Write the doc comment and the tool
  documents itself; skip it and `tools/McpAudit` fails the build.

See `/add-domain-function`.

## First-time setup

None: `dotnet restore` (so, any build) bootstraps the F# language tooling on a fresh clone via
`Directory.Build.targets`, once per clone, guarded by `.config/.fslangmcp-bootstrapped`. Pass
`-p:FsLangMcpBootstrap=false` to skip it, e.g. in CI.

It has to run because FsLangMCP's bootstrap installs fsautocomplete, ProjInfo and Fantomas as **global**
tools; the local manifest alone leaves the MCP server failing with
`An error occurred trying to start process 'fsautocomplete'`.

`.mcp.json` registers two MCP servers for anyone opening this repo in Claude Code.

**`domain`** serves this project's own domain functions — every `[<McpServerTool>]` in the Domain
assembly, currently 11. It runs via `dotnet run --project tools/Mcp`, so it rebuilds on start and always
reflects the current domain code. Two rules keep the stdio transport intact: all logging goes to stderr
(`LogToStandardErrorThreshold`), and validation failures are raised as `McpException` — a `JsonException`
from argument binding reaches the client only as a generic "An error occurred invoking '<tool>'", losing
the reason.

**`fsharp`** registers **FsLangMCP** for anyone opening this repo in Claude
Code. It gives compiler-backed F# intelligence — `find` for cross-project symbol search, `check` for a
real compilation verdict, type info, refactoring previews — instead of text search. Prefer it over
grepping for F# symbols. It preloads `src/Domain/Domain.fsproj`; call `set_project` to point it
elsewhere.

## Commands

```
dotnet build AgenticApp.slnx
dotnet run --project tools/McpAudit             # MCP tool coverage + labelling; exit 1 on problems
dotnet run --project tools/McpAudit -- --report # every public Domain function, and why it is/isn't a tool
tools/check-unused-opens.sh                     # unused `open`s; --fix removes them (slow: one build each)
dotnet format <project>               # C# only; F# is not formatted by dotnet format
```

`tools/McpAudit` fails if a public Domain function that could be an MCP tool is not one, or if a tool
lacks a name, title, or descriptions. Run it after changing any public Domain function.

## Build strictness

Projects treat these as **errors**, not warnings:

- **FS0025** — an unhandled DU case. Adding a state must break every site that ignores it.
- **FS1182** — an unused value or parameter. It is opt-in, so it needs
  `<OtherFlags>$(OtherFlags) --warnon:1182</OtherFlags>` alongside the `WarningsAsErrors` entry.
- **FS3261 / FS3264 / FS3265** — nullness. A boundary parameter must say `string | null` or
  `Nullable<T>` explicitly; nothing past it may be null, and no downcast may reintroduce one.

C# projects set `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`. A build that emits warnings is a
build that is failing slowly, so the count stays at zero.

## No unused code

Nothing in this repo may be unused: no unused function (private or public), type, case, parameter, or
`open`. If something has no caller, delete it rather than leaving it for a future caller that may not
arrive. Write the function when the thing that needs it exists.

FS1182 covers unused locals and parameters only.

**Unused `open`s** have no compiler warning and no analyzer package (Ionide.Analyzers has none), so
`tools/check-unused-opens.sh` asks the compiler directly: it blanks each `open`, rebuilds, and reports
the ones that still compile. Exact rather than heuristic, but ~1 build per `open`, so run it before a
PR rather than on every build. `--fix` deletes them.

**Unused functions** have no check at all — F# warns on neither private nor public ones. That is
enforced by review, by `dotnet run --project tools/McpAudit -- --report`, and by deleting on sight.

## Formatting

Formatting is verified by the **build**, not by an editor or agent hook, so it holds for every developer
and in CI. `Directory.Build.targets` runs `fantomas --check` on `.fsproj` projects and
`dotnet format --verify-no-changes` on `.csproj` ones after Build; drift fails the build like any
warning. Both are incremental via a stamp file — a repeat build skips them.

```
dotnet fantomas src tools     # fix F#   (pinned 7.0.6 in .config/dotnet-tools.json)
dotnet format <project>       # fix C#   (style in .editorconfig)
```

Pass `-p:VerifyFormat=false` to skip. `src/Domain/ValueObject.fs` is in `.fantomasignore`: Fantomas
7.0.6 cannot format interfaces with static abstract members and emits invalid F#, so it stays
hand-formatted.

