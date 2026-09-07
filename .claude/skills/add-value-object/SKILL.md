---
name: add-value-object
description: Build value objects in the F# Domain project — types that make illegal states unrepresentable, with private constructors, per-type error unions, and validation that runs on every path in including deserialization. Use when adding or changing a value type, a field on one, or any validation rule.
---

A value object is a type that cannot hold an invalid value. It is the only place validation lives:
everything downstream — models, transitions, adapters — receives values already known to be good, and
re-checks nothing.

For composing them into entities see `add-domain-model`; for how they reach the wire see
`add-domain-function`.

## The rules

1. **Illegal states are unrepresentable.** If a value can't legally exist, the type must make it
   impossible to build, not merely rejected by a check somewhere.
2. **Strings are `ReqStr` or `OptStr`, never `string`.** Instants are `ReqDateTimeOffset` or
   `OptDateTimeOffset`, never `DateTimeOffset`. Emptiness is decided by `String.IsNullOrWhiteSpace`.
3. **No `null` past construction.** `Make` is the boundary with the outside world and accepts whatever
   it sends; nothing downstream is ever null. Absence is `Option`.
4. **Exactly one way in, and its return type is honest.** Partial (some input is rejected) returns
   `Result<T, TError>`; total (nothing is rejected) returns `T`. Never wrap a total constructor in `Ok`
   — that forces callers to unwrap a branch that cannot occur.
5. **Every type owns its error type.** No shared `ValidationError`: a shared error can express failures a
   given type cannot have, which is the modelling mistake rule 1 forbids, committed for errors.
6. **Values are immutable.** A change is a pure function returning a new value.

## The contract

Every value object implements interfaces from `ValueObject.fs`. This is not decoration — the JSON
converters and MCP schema generation find value objects *by these interfaces*, so a type that forgets one
fails to compile rather than silently dropping out of serialization:

```fsharp
type IValueObject<'Wire> =                       // instance: the underlying value
    abstract Wire: 'Wire

type IPartialValueObject<'Self, 'In, 'Err ...> = // static: can reject its input
    static abstract Make: 'In -> Result<'Self, 'Err>
    static abstract Explain: 'Err -> string

type ITotalValueObject<'Self, 'In ...> =         // static: accepts everything
    static abstract Make: 'In -> 'Self
```

`'In` is the **inbound** shape — what the outside world sends — and is generally wider than `'Wire`:
`string | null` in, `string` out. Getting this backwards produces FS3261 nullness errors.

## The shape

```fsharp
namespace AgenticApp.Domain

open System

[<AutoOpen>]
module Primitives =

    /// Everything that can go wrong building a ReqStr. Nothing else can.
    [<RequireQualifiedAccess>]
    type ReqStrError =
        | Missing

    /// A string guaranteed non-null, non-whitespace, and trimmed.
    type ReqStr =
        private
        | ReqStr of string

        interface IValueObject<string> with
            member this.Wire = let (ReqStr v) = this in v

        interface IPartialValueObject<ReqStr, (string | null), ReqStrError> with
            /// Boundary: `value` may be null. Null and whitespace are both Missing.
            static member Make(value: string | null) =
                match value with
                | null -> Error ReqStrError.Missing
                | v when String.IsNullOrWhiteSpace v -> Error ReqStrError.Missing
                | v -> Ok(ReqStr(v.Trim()))

            static member Explain(e) =
                match e with
                | ReqStrError.Missing -> "is required"

    module ReqStr =
        /// <summary>Validates a required string. Returns Ok with the trimmed value, or Error if it is null, empty or whitespace.</summary>
        /// <param name="value">The raw string. Must be non-blank.</param>
        let create (value: string | null) =
            make<ReqStr, _, _> value

        /// <summary>Unwraps a validated required string.</summary>
        /// <param name="v">The validated string.</param>
        let value (reqStr: ReqStr) = (reqStr :> IValueObject<string>).Wire

        /// <summary>Renders a ReqStr failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describe error = explain<ReqStr, _, _> error
```

Four mechanics that are load-bearing:

- **`private` must be inside a module.** On a type declared directly in a `namespace` there is no
  enclosing module, so `private` degrades to assembly-wide and any Domain file can forge one. Inside
  `[<AutoOpen>] module Primitives` it scopes properly, and forging fails with **FS1093**.
- **Static abstract members are explicit implementations**, so `ReqStr.Make` does not resolve. Call them
  through the constrained helpers `make<T,_,_>` / `explain<T,_,_>` / `makeTotal<T,_>`; the companion
  module's `create` does exactly that. FS3535 is suppressed in the `.fsproj` for this.
- **Match `null` explicitly.** F# does not narrow through `String.IsNullOrWhiteSpace`, so `v.Trim()`
  after an `if` still warns. A `match` with a `null` case narrows and builds clean.
- **Every `///` doc comment is public API.** `<summary>` becomes the MCP tool description and each
  `<param>` becomes a schema description — see `add-domain-function`. Omit one and the audit fails.

## Boundary parameter types

`Make` takes whatever the outside world can actually send. This is mechanical, so there is no judgement
to exercise:

| What it is | Inbound type |
|---|---|
| Reference type (`string`) | `string \| null` |
| Value type (`Guid`, `int64`, `DateTimeOffset`) | `Nullable<T>` |
| Collection | `T array \| null` — **never `seq` or F# `list`** |

`Nullable<T>` matters for *every* value type: a `create` taking a bare `int64` cannot be called with the
`long?` the caller holds, which pushes the null check back outside the Domain. Collections must be
arrays because `seq<T>` is silently dropped from MCP schemas and an F# `list` produces an untyped one.

## Absence versus present-but-invalid

Different, and conflating them is the main trap:

- **Absent** is a legal state for an optional type: it yields `None`, never an error.
- **Present but not legally representable** is *always* an error, on optional types as much as required
  ones. Never coerce such a value to `None` — that discards evidence of corrupt data at the moment you
  detected it.

| Type | Absent | Present but invalid |
|---|---|---|
| `ReqStr` | `null` / whitespace → `Missing` | — |
| `OptStr` | `null` / whitespace → `None` | *(none possible — so it is total)* |
| `ReqDateTimeOffset` | null `Nullable` → `Missing` | `MinValue` → error |
| `OptDateTimeOffset` | null `Nullable` → `None` | `MinValue` → error |

`OptStr` is total because a blank string genuinely is how "the user typed nothing" arrives.
`OptDateTimeOffset` is partial because `MinValue` is `default(DateTimeOffset)` leaking through a
deserializer — corruption, not absence. Ask the same of any new type, including `Guid.Empty`.

## Errors are per-type

```fsharp
[<RequireQualifiedAccess>]
type OptDateTimeOffsetError =        // no Missing case: absence is legal here
    | Unrepresentable of DateTimeOffset
```

`[<RequireQualifiedAccess>]` is required — case names like `Missing` recur across error types and would
otherwise shadow. Error unions **carry primitives, not value objects**, because they cross the wire.

`Make` takes no field name: a value object does not know what field it is bound to. The model tags the
error at the composition site.

## Invariants over a collection

When a rule spans several values — "these ids are distinct" — it still belongs in a type, not in the
model's `create`. `DeliverymanIds` is the worked example: a private single-case union over
`DeliverymanId list`, with `tryAdd`/`tryRemove` enforcing distinctness in one place.

This matters more than it looks. A model built from value objects needs no private constructor (see
`add-domain-model`) — but only if *every* invariant it has is carried by a field's type. A rule left
procedural in `create` is one a deserializer can walk straight past.

## Accumulating errors

`Validation<'T,'E> = Result<'T, 'E list>` is generic in the error, so each aggregate supplies its own.
The module is `internal`: adapters pattern-match the `Result`, they never compose it.

**Accumulate by default; bind only when genuinely dependent.** `Result.bind` stops at the first failure,
and a caller who sent four bad fields should learn about four. The test is whether the next step needs
the previous one's *value*:

```fsharp
// Independent inputs -> accumulate.
build
<!> Validation.field StoreError.Id (StoreId.create storeId)
<*> Validation.field StoreError.Name (ReqStr.create name)
<*> Validation.field StoreError.Deliverymen (DeliverymanIds.create deliverymanIds)

// A rule that inspects a parsed value -> accumulate, then bind once.
DeliverymanId.create deliverymanId
|> Validation.field AssignDeliverymanError.InvalidId
|> Result.bind (fun d -> ...)
```

A bind chain over independent inputs compiles, passes tests written one bad field at a time, and quietly
reports a quarter of what it knows.

## Serialization is validation

Value objects reach the wire through a converter that routes every read through `Make`
(`tools/Mcp/ValueObjectJson.fs`). Deserializing `"   "` into a `ReqStr` **fails** with the domain's own
message. That is what makes a value object a legal MCP tool parameter, and why the interfaces above are
mandatory rather than stylistic.

Never register the F# union converter app-wide: a converter broad enough to read a domain type can also
write one, and that is exactly the hole `private` exists to close.


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

1. Is the type inside `[<AutoOpen>] module`, with a `private` single-case union?
2. Does it implement `IValueObject` plus `IPartialValueObject` **or** `ITotalValueObject`?
3. Does it own a `[<RequireQualifiedAccess>]` error union holding *only* failures it can have — no
   `Missing` on an optional type — carrying primitives rather than value objects?
4. Does `Make` take the inbound shape from the table above and match `null` explicitly?
5. Is a present-but-invalid value an error rather than coerced to `None`?
6. Does every public function carry a `///` `<summary>` and a `<param>` for each parameter?
7. Do independent inputs accumulate, with `Result.bind` only where a step needs the previous value?
8. Does `dotnet build AgenticApp.slnx` — it fails on any warning (FS0025, FS1182, FS3261, FS3264, FS3265)
   **and on formatting drift**; run `dotnet fantomas src tools` to fix that.
9. Does `dotnet run --project tools/McpAudit` report 0 problems?

Then run the two mechanical checks, each a temporary file that is built and deleted.

**The forge test** must **fail** with FS1093 — proving the private constructor holds:

```fsharp
namespace AgenticApp.Domain
module Forge =
    let a = ReqStr "   "
    let b = StoreId System.Guid.Empty
```

**The null probe** must **compile** — proving every `create` accepts what the world can send:

```fsharp
namespace AgenticApp.Domain
module NullProbe =
    let a = ReqStr.create null
    let b = ReqDateTimeOffset.create (System.Nullable())
    let c = DeliverymanId.create (System.Nullable())
```

A `create` taking a bare `int64` or a non-null `string` fails here — the exact mistake that pushes null
handling back outside the Domain.
