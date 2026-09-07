---
name: add-domain-function
description: Add a public function to the F# Domain project so it is callable from the dev-mode MCP server. Registration is by convention, so this covers what makes a function exposable and how to document it. Use when adding or changing any public Domain function.
---

Every public Domain function that *can* be called over MCP *is*, automatically. There is no attribute to
add and none to forget.

Write the function itself with `add-value-object` (types, validation, the null boundary) and
`add-domain-model` (states, transitions). This skill covers what makes it reachable.

## You do not decorate anything

`DomainTools.all()` in `tools/Mcp` reflects over the Domain assembly at startup and exposes every public
static function whose parameters are all safe. The Domain has **no MCP package reference at all** —
it is plain F#, and everything about exposure lives in `tools/Mcp`.

Two things follow, and they are the only work a new function needs.

**The name is the code path.** `AgenticApp.Domain.StoreModel.StoreModule.rename` becomes `Store.rename`
(the compiler's `Module` suffix is stripped). You never choose a name, so names cannot drift from code —
but only the *last* segment is kept, so two modules sharing a short name in different files would claim
one tool name between them. The audit fails on that (`NAME COLLISION`) rather than letting one shadow
the other.

**The description is the `///` doc comment.** `<summary>` becomes the tool description; each
`<param name="x">` becomes that parameter's schema description:

```fsharp
/// <summary>Assigns a deliveryman to a store. Returns Error if that deliveryman is already assigned.</summary>
/// <param name="deliveryman">The deliveryman to assign.</param>
/// <param name="store">The store to change.</param>
let assignDeliveryman deliveryman store =
```

Internalise this one rule: **a doc comment is not optional documentation, it is the tool's interface —
and the repo's index.** It feeds two surfaces from one edit: this tool's description, and the
`Domain.types` tool that renders the domain's rules on request. A rule stated in a `///` summary is
findable both ways; a rule left implicit costs a grep every time someone asks. Write the summary for the
person who will ask *"can this happen?"*, not just for the caller.

The same applies to a `///` on a **type** — `Domain.types` lists every documented type with its DU
cases or record fields, and that is where an agent reads what states exist. An invariant spanning a
whole collection (*"may not hold two Online memberships"*) belongs on the type that owns it, not only
on the function that enforces it.
`dotnet run --project tools/McpAudit` fails when a summary or any `<param>` is missing. They are read
from the compiler-generated `Domain.xml`, so `GenerateDocumentationFile` must stay on.

There is deliberately **no opt-out attribute**: nothing has needed one, so none exists. If a public
function must be kept off the wire, first ask whether it should be public at all — `internal` removes it
from the surface and from the tool list at once. Add an opt-out only when a genuine case appears.

## What makes a parameter exposable

Two independent checks. Conflating them hides real capability, which is a mistake this project already
made once:

- **Unforgeable** — can a deserializer produce a value the domain would have rejected? A wire shape
  cannot. A value object cannot: its converter routes through `Make`, so `"   "` is rejected before a
  `ReqStr` exists. A record of unforgeable fields cannot, since no combination of them is illegal. A
  **union** cannot either — every case is legal by construction.
- **Schematisable** — can it be given a schema a model can fill? `FSharp.SystemTextJson` claims records,
  unions and F# lists, so the schema exporter emits a bare `true` (match anything) for them.
  `DomainTools` describes each shape instead: a union becomes `oneOf` over its cases with `Case` pinned
  by `const`, a record becomes an object, a list becomes an array — recursively.

A type failing *either* check is skipped, silently, which is why the audit exists.

**The rule composes.** Safety is decided structurally and recursively, so you do not need to read
`ValueObjectJson` to predict it — a shape is exposable when every leaf it bottoms out in is:

| Parameter shape | Exposable? | Why |
|---|---|---|
| Wire shape (`string \| null`, `Nullable<T>`, `T array \| null`) | Yes | The exporter describes it directly |
| Value object (`ReqStr`, `DeliverymanId`) | Yes | Its converter routes through `Make` |
| Collection value object (`DeliverymanIds`) | Yes | Same, and its `'In` may be raw *or* already-built elements |
| Record of exposable fields (`Store`) | Yes | No field forgeable, so no combination illegal |
| Union of exposable cases (`StoreError`, or a `Membership` of value objects) | Yes | Every case is legal by construction; schematised as `oneOf` |
| Array or F# list of any of the above (`StoreError[]`, `Membership array`) | Yes | Schematised as an array of the element schema |
| `seq<T>` | **No** | Silently dropped from the schema |
| A plain record or union containing a raw non-wire type | **No** | Nothing describes or validates that leaf |

Two consequences worth stating plainly, because they save inventing plumbing that is not needed:

- A **union of value objects** is exposable, so a `Membership = Owner of ReqStr | Driver of DeliverymanId`
  needs nothing extra.
- A **collection value object over those** is exposable, so `StoreMemberships.Make` may take
  `Membership array | null` — already-built elements — and still enforce its own collection rule. Taking
  raw elements (`Nullable<int64> array`, as `DeliverymanIds` does) is equally valid; choose raw when the
  boundary should parse them, built when the caller already holds valid values.

So a new model and **every one of its transitions register as tools with no extra plumbing**, provided
each field bottoms out in the table above.

To check a specific type without reading source, run `dotnet run --project tools/McpAudit -- --report`:
anything unexposable is listed with the exact parameter that blocked it.

`seq<T>` is the one that bites: it disappears with no error and no warning, because `IEnumerable<T>` is
treated as a DI-injected service rather than an input. A Domain function taking a collection takes an
**array**.

## Why models need no private constructor

A model built entirely from value objects cannot be constructed in an invalid state, so it is a plain
public record — and therefore a legal tool parameter. Deserializing a hostile payload into `Store` is
rejected field by field, with the domain's own messages.

The one thing that escapes is an invariant no field owns. "Deliverymen are distinct" was once enforced
procedurally inside `Store.create`, and a public record let `[1,1]` straight through. The fix was not to
re-privatise the record but to give the invariant a type (`DeliverymanIds`). See `add-domain-model`.

## Errors round-trip

Because error unions are exposable, a failure returned by one tool can be handed straight back to its
`describe`:

```
Store.create  →  {"Case":"Error","Fields":[[{"Case":"Id",...},{"Case":"Name",...}]]}
Store.describe →  "id holds an unrepresentable id (00000000-…)"
                  "name is required"
```

So every model wants a `describe` for each of its error unions — that is what turns a wire payload back
into a sentence.

## Host wiring

Registered Development-only:

```csharp
builder.Services.AddMcpServer().WithTools(AgenticApp.Mcp.DomainTools.all());
```

`WithToolsFromAssembly` cannot be used: it accepts serializer options but not schema options, and both
are needed — the converter to validate value objects, the schema transform to describe them.

`tools/Mcp` is also the stdio server itself (`dotnet run --project tools/Mcp`, registered in
`.mcp.json`), and it **hot-reloads**: the Domain assembly is loaded into a collectible
`AssemblyLoadContext` and watched, so `dotnet build AgenticApp.slnx` mid-session republishes the tool
list and notifies the client. A function you add becomes callable without a reconnect.

It watches the server's **own** output copy of `Domain.dll`, so build the **solution** — building
`Domain.fsproj` alone refreshes a copy the server never reads, and the tool list silently stays stale.
The watched path is logged to stderr at startup if a reload ever fails to fire.

Three things that make that work, each of which cost a debugging round:

- **Mutate `McpServerOptions.ToolCollection`**, not a collection of your own. `WithTools` copies into the
  server's collection, so edits to yours are invisible to clients — the reload logs success while
  `tools/list` returns the old set.
- **Resolve the marker interfaces by name**, from the assembly of the type being inspected. A reloaded
  Domain is a different `Type` identity, so `typeof<IValueObjectMarker>` matches nothing and every value
  object silently degrades to a plain union. `ValueObjectNames` keeps the compile-time link.
- **Load from a byte array, and debounce.** Mapping the file would lock the DLL the compiler is about to
  overwrite, and a build writes it more than once.

Two rules keep the transport intact: **all logging goes to stderr**, since stdout carries
the protocol; and validation failures are raised as `McpException`, because a `JsonException` from
argument binding reaches the client only as a generic "An error occurred invoking '<tool>'", losing the
reason.

## Adding a function

1. Write the domain function, per `add-value-object` and `add-domain-model`. Keep it pure.
2. Give it a `///` `<summary>` and a `<param>` for every parameter.
3. Check its parameters against the table above — an array, not a `seq`.
4. Build, then run the audit. Nothing else to regenerate: `Domain.types` and `Domain.functions` read the assembly per call,
   so a new function and its doc comment are live as soon as the solution builds.

## Audit

```
dotnet run --project tools/McpAudit             # exit 1 on problems, so it gates CI
dotnet run --project tools/McpAudit -- --report # every public function, and why it is or is not a tool
```

Every failure mode here is silent, so the check is a program rather than a habit:

| Finding | Meaning |
|---|---|
| `NOT A TOOL` | Every parameter is safe, yet the server did not register it — a silent coverage gap |
| `NAME COLLISION` | Two functions map to one tool name; the client would see one where the domain has two |
| `NO DESCRIPTION` / `NO PARAM DESC` | Missing `///` `<summary>` or `<param>` |
| `UNTYPED PARAM` | No `type`, `oneOf` or `enum` — a model cannot fill it |
| `NO PARAM SCHEMA` | The generator emitted a bare `true`; the type needs a transform |
| `UNSAFE PARAM` | Forgeable — it would let a caller construct an invalid domain value |
| `BAD NAME` | Not `Type.function`, so it does not mirror the code |

The audit calls `DomainTools.all()` — the same function the host calls — so it audits exactly what
ships rather than a re-derivation that can drift. It decides *which* functions those are the same way:
`--report` labels a function `TOOL` when its `DomainTools.toolName` is in the registered set, and
otherwise prints why `DomainTools.isExposable` turned it down — generic, no parameters, or the exact
parameter that blocked it. Registration is by convention, so there is no attribute for the audit to
look for either; an audit that went looking for one would call every function untooled.


## Types are inferred

Never annotate a return type. Annotate a parameter only where the compiler or the **tool schema** needs
it: a `string | null` boundary (redundant to F#, but the schema loses `"null"` without it — the audit
fails that as `NON-NULL STRING`), a parameter matched against `| null`, an interface implementation, a
generic, or a value reached only through a coercion. `Nullable<T>` needs nothing.

Carry the meaning in the **parameter name** instead (`deliveryman`, not `d`) — it is also the MCP
schema's property name. See the full rule in CLAUDE.md.


## Doc comments split in two

`<summary>` is one sentence — the rule — and is shown on every match, being also the MCP tool's
description. `<remarks>` holds the reasoning and edge cases, and `Domain.types` reveals it only to a
reader who asked about this thing by name. Write the reasoning either way; put it in the right tag.
`tools/McpAudit` fails a summary over 200 chars as `LONG SUMMARY`. In a tagged comment you must escape
`<`, `>` and `&` yourself — F# only escapes untagged ones, and one stray `<` makes `Domain.xml`
unparseable, which costs every tool its description.

## Checklist

1. Does every public function have a `///` `<summary>` and a `<param>` for each parameter?
2. Are collection parameters arrays, never `seq` or F# `list`?
3. Is anything you wanted hidden actually `internal`, rather than public-but-excluded?
4. Does `dotnet build AgenticApp.slnx` — it fails on any warning (FS0025, FS1182, FS3261, FS3264, FS3265)
   **and on formatting drift**; run `dotnet fantomas src tools` to fix that.
5. Does the audit report 0 problems, and does `--report` show no unexpected exclusions?

## Interop note

An F# module sharing its name with a type gets a `Module` suffix in compiled form — the `Store` module
becomes `StoreModule`. From C# that is `StoreModel.StoreModule.create(...)`. Reflection also writes
nested types as `Outer+Inner` while XML docs use `Outer.Inner`, which is why `DomainDocs` normalises
before matching.
