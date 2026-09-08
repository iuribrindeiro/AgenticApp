# AgenticApp

**What if a coding agent could ask the domain what it allows, instead of grepping for it?**

This is a proof of concept for a way of building .NET services where the domain model *is* an MCP
server. The business rules live in a dependency-free F# library; a companion server reflects over its
compiled assembly and exposes it to an AI agent as ~100 live tools — one per public domain function,
plus two that render the domain's documented intent as prose.

The doc comments you write for humans become the tool descriptions the agent reads. Write the comment
once; it documents itself in both places, and the audit fails the moment you skip one.

There is no HTTP API here yet. **The MCP server is the interface.**

---

## The problem this solves

Ask an agent *"can a blocked deliveryman collect an order?"* and it greps, opens three files, follows
control flow, and gives you an answer hedged with "based on what I found." Two failure modes follow:

1. **It can't prove absence.** "I didn't find a check for that" and "there is no check for that" look
   identical from the outside. Grep can't tell them apart.
2. **It goes stale.** Anything the agent learned from reading files is a snapshot. The next edit
   invalidates it silently.

Both are solved by making the domain answer for itself, from its own compiled assembly, per call.

---

## What the custom tools provide

### 1. `Domain.types` — what the domain allows

The cheap, wide entry point. Types, states, invariants, and every rejection that names your topic. It
takes an **array** of topics, so one call covers everything you need.

<details open>
<summary><code>Domain.types(topics: ["blocked", "collect"])</code> — real output, abridged</summary>

```
**Standing** — union. A deliveryman's standing at one store. Orthogonal to presence:
he can be blocked whether he is online or offline, and blocking never moves him.
- `Active`
- `Blocked`

**Collector** — union. Who is performing a collection.
  The difference is a rule, not bookkeeping: a deliveryman collecting an order himself
  must be standing in his store's collect area, while the store collecting on his behalf
  may do so wherever he is — that case exists precisely for when he cannot be there.
  What neither can do is collect for a deliveryman who is offline or blocked.
- `Deliveryman`
- `Store`

### Errors these can return
- **CollectError** — `NotAssigned` · `AssignedToAnother of deliverymanId: int64`
  · `AlreadyCollected of deliverymanId: int64` · `AlreadyCompleted`
  · `NotOnlineAt of storeId: Guid` · `BlockedAt of storeId: Guid` · `NotInCollectArea`

## Rejections that name this
- `Order.collect` rejects with `CollectError.BlockedAt`
- `Deliveryman.block` rejects with `BlockError.AlreadyBlocked`

## What branches on this
- `Order.assign`        tests `OrderStatus.Collected` — may return `AlreadyAssignedTo`, `AlreadyCompleted`
- `Order.collect`       tests `OrderStatus.Collected` — may return `AlreadyCollected`, `AlreadyCompleted`, …
- `Order.isHeldBy`      tests `OrderStatus.Collected` — cannot fail
- `Deliveryman.block`   tests `Standing.Blocked`     — may return `AlreadyBlocked`, `NotAMember`, `NotJoined`

**Complete**: read from the compiled quotation of every one of the 98 exposed functions,
so any function absent above does not test a "blocked"/"collect" case at all.
No grep needed, and it cannot go stale.
```
</details>

Note what that last paragraph is doing. Every domain module is `[<ReflectedDefinition>]`, so the
server reads each function's **compiled quotation** and reports exactly which functions branch on a
given state and what each can return. The listing is therefore *complete over every exposed function*
— which turns absence into proof rather than an inconclusive search. `tools/McpAudit` fails with
`NO QUOTATION` if any function lacks one, which is what keeps that claim honest.

### 2. `Domain.functions` — signature, intent, parameters

The companion, for when you're about to write code against a function. Several times larger, so it's
the second call, not the first.

<details>
<summary><code>Domain.functions(topics: ["collect"])</code> — real output, abridged</summary>

```
### Order.collect
`(collector: Collector) (deliveryman: Deliveryman) (order: Order) → Result<Order, CollectError>`

Records an order being collected.

This is the one transition with a real precondition, and it spans both aggregates: the
order must be Assigned to this deliveryman, and he must be online at the store the order
was placed at, and not blocked there. A deliveryman collecting an order himself must also
be inside that store's collect area; the store collecting on his behalf need not be, which
is the whole point of the distinction. An offline or blocked deliveryman can have an order
collected neither way.

- `collector`   — Who is collecting it - the deliveryman himself, or the store on his behalf.
- `deliveryman` — The deliveryman the order is assigned to.
- `order`       — The order to change.
```
</details>

### 3. One tool per public function — run it on real input

Tool names are code paths. `Order.collect` is callable with a JSON payload and returns the real
`Result`:

```jsonc
// → Order.collect
{
  "collector":   { "Case": "Deliveryman" },
  "deliveryman": { "Id": 42, "Name": "Ana", "Stores": [] },
  "order": {
    "Id": "6f9619ff-…", "Client": "3f2504e0-…", "Store": "1b4e28ba-…",
    "Status": { "Case": "Assigned", "Fields": [42] }
  }
}

// ← the domain's actual return value
{ "Case": "Error", "Fields": [ { "Case": "NotOnlineAt", "Fields": ["1b4e28ba-…"] } ] }
```

```jsonc
// → Order.describeCollect  { "error": { "Case": "NotOnlineAt", "Fields": ["1b4e28ba-…"] } }
// ← "he is not online at store 1b4e28ba-…, where the order was placed"
```

That is the domain executing, not an agent predicting what it would do. The parameter schemas are
generated from the F# types, so a union arrives as a `oneOf` over its cases and the agent can't
invent a state that doesn't exist.

### 4. Value objects that validate on the way in

Deserialisation routes through each value object's `Make`, so the JSON boundary **validates rather
than forges**. An agent cannot hand the domain a `DeliverymanId` of `0`, a blank `ReqStr`, or a
`StoreMemberships` with two `Online` entries — the same constructor that protects F# call sites
protects the tool call.

### 5. It reloads itself mid-session

The server watches its own copy of `Domain.dll` in a collectible `AssemblyLoadContext`. Run
`dotnet build AgenticApp.slnx` and the tool list is rebuilt and re-announced over
`notifications/tools/list_changed` — no client reconnect. Add a function, build, call it, all in one
session. A failed reload keeps the previous list, so a half-written assembly never empties the server.

---

## Why this beats the alternatives

| | Grep + read files | Hand-written MCP tools | This |
| --- | --- | --- | --- |
| Can prove a rule is *absent* | ✗ | ✗ | ✓ (quotations cover every exposed function) |
| Goes stale | ✓ | ✓ (drifts from code) | ✗ (generated per call from the loaded assembly) |
| Cost of adding a function | — | write the tool, its schema, its description | write the `///` comment |
| Illegal input reachable | — | ✓ (hand-rolled parsing) | ✗ (routes through `Make`) |
| Tokens to answer "what's allowed?" | many files | many narrow tools | one `Domain.types` call |
| Enforced | nothing | code review | build + `McpAudit` exit 1 |

The measurable win is the last two rows. `Domain.types` is a *wide, cheap* call — one round trip
covering several topics at once — where the naive alternative is a dozen file reads or a dozen narrow
tool calls, each re-paying a fixed cost. And nothing here relies on an agent remembering to be
careful: skipping a doc comment fails the audit, and the audit is one command.

---

## Registration is by convention, not by attribute

There is no `[<McpServerTool>]` to remember. `DomainTools.all()` exposes every public domain function
whose parameters are all *safe* (value objects and primitives it can round-trip). A tool's name is its
code path; its description is its `///` comment.

That convention is what lets `tools/McpAudit` be strict about things an attribute scheme structurally
cannot catch:

| Failure | Meaning |
| --- | --- |
| `NOT A TOOL` | Every parameter is safe, so it should be exposed — but it isn't. |
| `NAME COLLISION` | Two modules would resolve to one tool name; the client would see one where the domain has two. |
| `GENERIC` | A dropped type annotation silently generalised the function, so it vanished from the list with no error anywhere. |
| `NO QUOTATION` | Missing `[<ReflectedDefinition>]`, which would quietly break `Domain.types`' completeness claim. |
| `NO DESCRIPTION` | Someone skipped the doc comment, leaving an unlabelled tool. |

```bash
dotnet run --project tools/McpAudit              # → "100 tool(s) discovered, 0 problem(s)"; exit 1 on problems
dotnet run --project tools/McpAudit -- --report  # every public function, and why it is/isn't a tool
```

The audit imports `DomainTools.isExposable` and `DomainTools.toolName` from the server itself rather
than restating them, so it cannot drift from what actually ships.

---

## The domain

A delivery marketplace — stores, deliverymen, orders. It exists to be non-trivial enough that the
tooling has something real to answer questions *about*.

| Aggregate | Shape |
| --- | --- |
| **Store** | Id, name, a distinct set of deliverymen. No lifecycle states, so no status DU — inventing one would model a distinction the domain doesn't have. |
| **Deliveryman** | Id, name, and store memberships: `Invited` \| `Declined` \| `Offline` \| `Online` \| `Removed`. `Online` carries a `Presence` (queue area / collect area, each with the instant he entered) and a `Standing` (`Active` \| `Blocked`). |
| **Order** | Id, client, store, and an `OrderStatus`: `Preparing` → `Prepared` → `Assigned` → `Collected` → `Completed`. |

Everything is modelled so that **illegal states are unrepresentable**:

- The deliveryman lives *inside* the order states that have one — "assigned with no deliveryman"
  cannot be written down.
- An area's entry timestamp exists only in the `Inside` case, so there's no date for an area he never
  entered.
- Standing and presence are orthogonal: blocking never moves him.
- Rules spanning a collection live in a *type* (`StoreMembershipsError`, `DeliverymanIdsError`)
  rather than procedurally inside a `create`.
- Every failure is its own union case, named for what went wrong. No stringly-typed errors —
  which is also why `Domain.types` can list rejections by name.

That discipline is what makes the domain legible to a tool in the first place: the states are in the
type system, so reflection can read them.

---

## Layout

```
src/Domain/        F# class library. Types and pure functions only.
                   No I/O, no project references, no package references at all.
tools/Mcp/         The stdio MCP server. Owns everything about *exposing* the domain:
                   value-object JSON converters and DomainTools.all().
                   Serialisation never leaks into Domain.
tools/McpAudit/    Coverage guard. Exit 1 on any of the failures above.
```

Adapters (REST, gRPC, SignalR + Redis backplane, Orleans grains) and the MongoDB repositories are
designed but not built. There are no tests yet.

The parity rule, once adapters land: **every** REST endpoint, gRPC method, hub method, grain method
and public domain function must have an MCP tool that invokes it. Adding one without its tool is an
incomplete change.

---

## Build

Requires the **.NET 10** SDK. A fresh clone needs no setup — any build bootstraps the F# tooling once.

```bash
dotnet build AgenticApp.slnx                     # build — this is what reloads the MCP server
dotnet run --project tools/McpAudit              # coverage + labelling
tools/check-unused-opens.sh                      # unused `open`s; --fix removes them
dotnet fantomas src tools                        # F# formatting (Fantomas 7.0.6, pinned)
```

Build the **solution**, not `Domain.fsproj` — the server hot-reloads from its own output copy of
`Domain.dll`, and building the domain project alone updates a copy the server never reads.

**Warnings are errors, and so is drift.** Formatting and lint run *inside the build* rather than in an
editor hook, so they hold in CI and for everyone (`-p:VerifyFormat=false`, `-p:VerifyLint=false` to
skip). F# additionally promotes FS0025 (unhandled DU case), FS1182 (unused value or parameter) and the
nullness warnings to errors — a boundary parameter must say `string | null` explicitly, and nothing
past it may be null.

---

## The agent's toolchain

`.mcp.json` wires up three servers, each for a different question shape:

| Server | Answers |
| --- | --- |
| `domain` | *What does the domain allow, and what happens on this input?* This repo's own server. |
| `fsharp` | *What is the structure?* `fslangmcp` — compiler-backed public API, project outline, dead code. Beats grepping for F# symbols. |
| `fsi` | *What happens across several steps?* An F# Interactive session where values stay in scope, instead of threading JSON between tool calls by hand. |

`CLAUDE.md` routes between them and `.claude/skills/` carries the conventions for extending the
domain — `add-domain-function`, `add-domain-model`, `add-value-object`, and one per adapter type.

---

## License

Not yet licensed.
