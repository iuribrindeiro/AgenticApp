# CLAUDE.md

## Status

`src/Domain` (the F# domain: `Store`, `Deliveryman`), `tools/Mcp` (the MCP server), `tools/McpAudit`
(the coverage guard). Adapters — REST, gRPC, SignalR, Orleans — and the MongoDB repositories are not
built yet. **There are no tests**; do not add or run them until asked.

Target **.NET 10** and the **latest stable version of every package**, unless a pin is justified in the
project file.

## Architecture

- **Domain** (`src/Domain`) — F# class library exposing only *types* and *pure functions*. No I/O, no
  dependency on any other project, and **no package references at all**. All business logic lives here.
- **Mcp** (`tools/Mcp`) — references Domain and is the stdio MCP server. Owns everything about
  *exposing* the domain: the value-object JSON converters (deserialisation routes through `Make`, so it
  validates rather than forges) and `DomainTools.all()`. Serialisation never leaks into Domain.
- **Adapters** — REST, gRPC, SignalR (Redis backplane), Orleans grains. **Thin**: parse input, call a
  Domain function, map the result out. They may call repositories. No business logic — a rule belongs in
  Domain.
- **Repositories** — MongoDB. Fetch and persist only; injected into adapters, **never** referenced from
  Domain.

## MCP tool parity (non-negotiable)

Every REST endpoint, gRPC method, SignalR hub method, Orleans grain method and public Domain function
must have an MCP tool that invokes it. Adding one without its tool is an incomplete change — use
`/add-rest-endpoint`, `/add-grpc-method`, `/add-signalr-method`, `/add-orleans-grain-method`,
`/add-domain-function`.

Registration is **by convention, not by attribute**: `DomainTools.all()` exposes every public Domain
function whose parameters are all *safe*. A tool's name is its code path (`Store.rename`) and its
description is its `///` doc comment — write the comment and the tool documents itself; skip it and
`tools/McpAudit` fails. See `/add-domain-function` for what makes a parameter safe.

## Answering questions about the domain

**Always call `Domain.types` first — before any grep, file read, or search for individual tools.** It is
an MCP tool on the `domain` server that renders the domain's types, their states, the invariants that
constrain them, and every rejection named after your topics, out of the same `///` comments that describe
every other tool. It is generated per call from the loaded assembly, so it is never stale.

`topics` takes an **array**, so ask about everything you need in **one call** — `["online", "blocked"]`,
not one call each. Each call re-pays a fixed cost, and two narrow calls come to more than one wider one.
Omit `topics` for the whole domain.

That holds for **every** question about domain behaviour — what is allowed, what a state means, what
happens in some situation. No question shape justifies skipping it, and it is usually the whole answer.

`Domain.functions` is its companion: signature, intent and parameters. It is several times larger, so
reach for it when you are about to call a function or write code against one, not to settle what the
domain allows.

- **Run a function** on real input → call its own tool. Names are code paths, loaded every session.
- **A multi-step scenario** → the `fsi` server; one script keeps values in scope, where chaining tool
  calls means threading JSON between them by hand.
- **Structure** (signatures, callers, dead code) → the `fsharp` server (`fcs_public_api`,
  `fcs_project_outline`). It is compiler-backed; prefer it over grepping for F# symbols.

**Never grep `src/Domain` to answer a question — `Domain.types` already proves absence.** Every domain
module is `[<ReflectedDefinition>]`, so the tool reads each function's compiled quotation and reports
exactly which functions branch on a state and what each can return. That list is complete over every
exposed function, so a function missing from it does not test that state at all. `tools/McpAudit` fails
as `NO QUOTATION` if any function lacks one, which is what keeps the claim honest. Read the source only
when you are changing it.

If a rule was hard to find, the fix is a better `///` summary: it improves the tool description and
`Domain.types` in one edit.

**Build the solution, not `Domain.fsproj`.** The server hot-reloads from its own output copy of
`Domain.dll`, so `dotnet build AgenticApp.slnx` refreshes the tool list mid-session, while building the
Domain project alone updates a copy the server never reads.

## Commands

```
dotnet build AgenticApp.slnx
dotnet run --project tools/McpAudit             # tool coverage + labelling; exit 1 on problems
dotnet run --project tools/McpAudit -- --report # every public Domain function, and why it is/isn't a tool
tools/check-unused-opens.sh                     # unused `open`s; --fix removes them (one build each, slow)
dotnet fantomas src tools                       # fix F# formatting (pinned 7.0.6)
dotnet format <project>                         # fix C#; F# is not formatted by dotnet format
dotnet fsharplint lint -l fsharplint.json --file-type wildcard "src/Domain/*.fs"  # read lint findings
```

A fresh clone needs no setup — any build bootstraps the F# tooling once (`-p:FsLangMcpBootstrap=false`
skips it). Run the audit after changing any public Domain function.

## Build strictness

Warnings are errors. F#: **FS0025** (unhandled DU case), **FS1182** (unused value or parameter),
**FS3261/FS3264/FS3265** (nullness — a boundary parameter says `string | null` or `Nullable<T>`
explicitly, and nothing past it may be null). C# sets `TreatWarningsAsErrors`.

Formatting and lint are checked by the **build**, not by an editor or a hook, so they hold in CI and for
everyone: drift fails it (`-p:VerifyFormat=false` skips), and so do lint findings
(`-p:VerifyLint=false` skips). Two caveats behind those checks:

- `src/Domain/ValueObject.fs` is in `.fantomasignore` because Fantomas 7.0.6 emits invalid F# for static
  abstract members, so it stays hand-formatted.
- `fsharplint.json` enables two rules: FL0034 `ReimplementsFunction` and FL0035
  `CanBeReplacedWithComposition`. FL0035 only fires on chains of **three or more** functions — the
  threshold is a literal in the rule, not a setting — so two-function compositions rest on review.

`tools/McpAudit` and `tools/check-unused-opens.sh` are **not** in the build; run them yourself.

F# style — doc comments, annotations, combinators, unused code — is in `.claude/rules/fsharp-style.md`,
which loads when you open an F# file.
