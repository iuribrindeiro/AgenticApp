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
(the compiler's `Module` suffix is stripped). You never choose a name, so names cannot drift from code.

**The description is the `///` doc comment.** `<summary>` becomes the tool description; each
`<param name="x">` becomes that parameter's schema description:

```fsharp
/// <summary>Assigns a deliveryman to a store. Returns Error if that deliveryman is already assigned.</summary>
/// <param name="deliveryman">The deliveryman to assign.</param>
/// <param name="s">The store to change.</param>
let assignDeliveryman (deliveryman: DeliverymanId) (s: Store) : Result<Store, AssignDeliverymanError> =
```

Internalise this one rule: **a doc comment is not optional documentation, it is the tool's interface.**
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

| Parameter | Exposable? |
|---|---|
| Wire shape (`string \| null`, `Nullable<T>`, `T array \| null`) | Yes |
| Value object (`ReqStr`, `DeliverymanId`) | Yes — converter validates on the way in |
| Model record built only from those (`Store`) | Yes — no field forgeable, so no illegal combination |
| Error union (`StoreError`) | Yes — unforgeable, and schematised as `oneOf` |
| `seq<T>` | **No** — silently dropped from the schema |
| F# `list` as a *parameter* type | **No** — produces an untyped schema; use an array |

That last row is the one that bites: `seq<T>` disappears with no error and no warning, because
`IEnumerable<T>` is treated as a DI-injected service rather than an input. So a Domain function that
takes a collection takes an **array**.

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
`.mcp.json`). Two rules keep that transport intact: **all logging goes to stderr**, since stdout carries
the protocol; and validation failures are raised as `McpException`, because a `JsonException` from
argument binding reaches the client only as a generic "An error occurred invoking '<tool>'", losing the
reason.

## Adding a function

1. Write the domain function, per `add-value-object` and `add-domain-model`. Keep it pure.
2. Give it a `///` `<summary>` and a `<param>` for every parameter.
3. Check its parameters against the table above — an array, not a `seq`.
4. Build, then run the audit.

## Audit

```
dotnet run --project tools/McpAudit             # exit 1 on problems, so it gates CI
dotnet run --project tools/McpAudit -- --report # every public function, and why it is or is not a tool
```

Every failure mode here is silent, so the check is a program rather than a habit:

| Finding | Meaning |
|---|---|
| `NO DESCRIPTION` / `NO PARAM DESC` | Missing `///` `<summary>` or `<param>` |
| `UNTYPED PARAM` | No `type`, `oneOf` or `enum` — a model cannot fill it |
| `NO PARAM SCHEMA` | The generator emitted a bare `true`; the type needs a transform |
| `UNSAFE PARAM` | Forgeable — it would let a caller construct an invalid domain value |
| `BAD NAME` | Not `Type.function`, so it does not mirror the code |

The audit calls `DomainTools.all()` — the same function the host calls — so it audits exactly what
ships rather than a re-derivation that can drift.

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
