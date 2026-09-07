---
name: add-domain-model
description: Create or update a domain model (entity or aggregate) in the F# Domain project — states modelled as discriminated unions so no combination of fields can express an illegal state, with pure transitions between them. Use when adding a model, adding or changing a field, or adding a lifecycle state.
---

Models compose the value objects from `add-value-object` (read that first for `ReqStr`, `OptStr`,
`Validation`, private constructors, and the null boundary). This skill is about the layer above: how the
fields fit together so the *shape* of the type carries the domain's states.

## The rule

**No state is represented by a combination of field values, or by a field's presence or absence.** If
the domain has states, they are discriminated union cases. Each case carries exactly the data that state
has — always present, always `Req*` — and no data it doesn't.

The consequence: reading a model never means inferring what it is from a pattern of nulls and booleans.
You `match`, and the compiler hands you exactly the data that state holds.

## What this replaces

```fsharp
// ❌ Eight representable combinations. Two are legal.
type Customer =
    { Name: ReqStr
      IsDeactivated: bool
      DeactivatedAt: OptDateTimeOffset
      DeactivationReason: OptStr }
```

`IsDeactivated = true` with no date. A date with no reason. A reason while still active. Every one of
those compiles, and every function touching this type has to decide what to do about them — usually by
guessing, differently each time.

```fsharp
// ✅ Two representable states. Both are legal. The data lives with the state that has it.
[<RequireQualifiedAccess>]
type CustomerStatus =
    | Active
    | Deactivated of deactivatedAt: ReqDateTimeOffset * reason: ReqStr
```

The boolean is gone (the case *is* the flag), and the two fields that only mean something when
deactivated now cannot exist unless the customer is.

## When a field may still be `Opt*`

`Opt*` is not banned — it is banned as a way of encoding state. Use it only for data that is genuinely
unknown, where nothing in the domain branches on whether it is there. Three questions; **any** yes means
it is a state and belongs in a DU case:

1. **Does a business rule, invariant, or permitted operation depend on its presence?**
   `DeactivatedAt` decides whether the customer can be billed. `Nickname` decides nothing.
2. **Would you `match` on its presence to choose behaviour**, rather than merely to display it?
3. **Does its presence correlate with another field's presence?** If `DeactivatedAt` present implies
   `DeactivationReason` present, two fields must move together — that is the banned combination, and
   both belong in one case.

`Nickname: OptStr` passes all three: a customer without one is not in a different mode. Keep it.

Never wrap a value object in `option` — no `OptStr option`, no `ReqStr option`. `Opt*` already carries
its own absence; `option` on top of it is a second, redundant way to say nothing, and the two can
disagree.

## Models do not need private constructors

A model built entirely from value objects **cannot be constructed in an invalid state**, so its record
is public and its fields are public. Every field type already refuses bad input, and the DU shape
already rules out illegal combinations. A private constructor on top of that guards nothing — it just
costs you accessor functions and blocks the model from being an MCP tool parameter.

Verified: with the value-object converters registered, deserializing straight into a public `Store`
rejects an empty id, a blank name, and a non-positive deliveryman id, each with the domain's own
message. Nothing gets past the field types.

**The one thing that does escape is an invariant that no field owns.** `Store` originally enforced
"deliverymen are distinct" procedurally inside `create`, and a public record let `[1,1]` straight
through. The fix is not to re-privatise the record — it is to give the invariant a type:

```fsharp
/// The "deliverymen are distinct" rule lives here, in a type, rather than
/// procedurally in Store.create.
type DeliverymanIds =
    private
    | DeliverymanIds of DeliverymanId list
```

with `tryAdd`/`tryRemove` so the rule is enforced in exactly one place. `Store` then has no invariant of
its own and stays a plain public record.

So the rule is: **when you reach for a private constructor on a model, you have found an invariant that
belongs in a type.** Move it there instead. Value objects keep their private constructors — they are
where validation lives; models compose them and need none.

The payoff is not just tidiness: a model whose every field is safe is a legal MCP tool parameter, so
transitions like `rename` and `assignDeliveryman` become tools directly, with no raw-input duplicate and
no repository indirection.

## Two shapes of model

**A record with a status DU** — when the states share most of their data and differ in a slice of it:

```fsharp
type Customer =
    { Name: ReqStr
      Nickname: OptStr
      SignedUpAt: ReqDateTimeOffset
      Status: CustomerStatus }
```

**The model itself as a DU** — when the states differ in *shape*, so a shared record would be mostly
fields that don't apply:

```fsharp
[<RequireQualifiedAccess>]
type Order =
    | Draft of lines: OrderLine list
    | Placed of lines: OrderLine * rest: OrderLine list * placedAt: ReqDateTimeOffset
    | Cancelled of cancelledAt: ReqDateTimeOffset * reason: ReqStr
```

A `Draft` may be empty and has no placement date. A `Placed` order always has a date and **at least one
line** — encoded as `first * rest` rather than a `list`, so an empty placed order cannot be built.
Reach for that trick whenever "non-empty" is part of the invariant.

Choose by asking whether a shared record would carry fields that are meaningless in some states. If yes,
the model is the DU.

## Worked example

```fsharp
namespace AgenticApp.Domain

open System

[<AutoOpen>]
module CustomerModel =

    /// The lifecycle. Data that only exists in one state lives *in* that state,
    /// and is a Req* value that always exists there.
    [<RequireQualifiedAccess>]
    type CustomerStatus =
        | Active
        | Deactivated of deactivatedAt: ReqDateTimeOffset * reason: ReqStr

    [<RequireQualifiedAccess>]
    type CustomerError =
        | Name of ReqStrError
        | SignedUpAt of ReqDateTimeOffsetError

    /// Public: every field is a value object or a state DU, so no combination of them
    /// is illegal. No private constructor, and therefore usable as an MCP tool parameter.
    type Customer =
        { Name: ReqStr
          Nickname: OptStr
          SignedUpAt: ReqDateTimeOffset
          Status: CustomerStatus }

    [<RequireQualifiedAccess>]
    type DeactivateError =
        | AlreadyDeactivated of since: ReqDateTimeOffset
        | BeforeSignUp

    [<RequireQualifiedAccess>]
    type ReactivateError =
        | NotDeactivated

    module Customer =

        /// A new customer is Active. There is no way to construct a Deactivated
        /// one directly - it can only be reached through `deactivate`.
        let create
            (name: string | null)
            (nickname: string | null)
            (signedUpAt: Nullable<DateTimeOffset>)
            : Validation<Customer, CustomerError> =

            let build n nick s =
                { Name = n
                  Nickname = nick
                  SignedUpAt = s
                  Status = CustomerStatus.Active }

            build
            <!> Validation.field CustomerError.Name (ReqStr.create name)
            <*> Ok(OptStr.create nickname)   // total constructor: already a value, just lift it
            <*> Validation.field CustomerError.SignedUpAt (ReqDateTimeOffset.create signedUpAt)

        // Accessors return primitives so they are usable on the wire; the value objects
        // are public, so an in-process caller can reach for the wrapped type when it wants
        // one. Each needs a /// doc comment - it becomes the MCP tool description.
        let name customer = ReqStr.value customer.Name
        let nickname (c: Customer) : string option = OptStr.value c.Nickname
        let signedUpAt (c: Customer) : DateTimeOffset = ReqDateTimeOffset.value c.SignedUpAt
        let status (c: Customer) = c.Status

        /// Transitions take built value objects and validate nothing: the types already did.
        let deactivate at reason customer =
            match c.Status with
            | CustomerStatus.Deactivated(since, _) -> Error(DeactivateError.AlreadyDeactivated since)
            | CustomerStatus.Active ->
                if ReqDateTimeOffset.value at < ReqDateTimeOffset.value c.SignedUpAt then
                    Error DeactivateError.BeforeSignUp
                else
                    Ok { c with Status = CustomerStatus.Deactivated(at, reason) }

        let reactivate customer =
            match c.Status with
            | CustomerStatus.Active -> Error ReactivateError.NotDeactivated
            | CustomerStatus.Deactivated _ -> Ok { c with Status = CustomerStatus.Active }

        /// Plain data edits need no state check - they are legal in every state.
        let rename newName customer = { customer with Name = newName }
```

Points worth copying:

- **`create` produces the initial state only.** Nobody can construct a `Deactivated` customer out of
  nothing; that state is reachable only by a transition that checked the rules to get there.
- **Each transition owns an error type** naming only the ways *it* can fail, exactly as value objects do.
  `ReactivateError` has no `BeforeSignUp` case because reactivation cannot fail that way.
- **Transitions take an already-built model and already-built value objects.** Everything reaching a
  transition is valid by construction, so a transition validates *nothing* — the types did it. Parsing
  happened once, in the value object's own `create`:

  ```fsharp
  let rename newName store = { store with Name = newName }

  let assignDeliveryman deliveryman store =
      if List.contains deliveryman s.Deliverymen then
          Error(AssignDeliverymanError.AlreadyAssigned(DeliverymanId.value deliveryman))
      else
          Ok { s with Deliverymen = s.Deliverymen @ [ deliveryman ] }
  ```

  Two anti-patterns to avoid: taking raw values and parsing inside the transition (duplicates work
  `create` already did, and invents failure modes the operation does not have), and taking raw *model*
  fields to rebuild the model (re-runs validation that already succeeded). The transition's error union
  then covers only its own rule — `AlreadyAssigned`, not `InvalidId`.
- **Edits that are legal in every state don't return `Result`.** `rename` cannot fail, so it returns
  `Customer` — same honesty rule as total constructors.

## Exhaustiveness is not free — turn it on

The design's payoff is that adding a state forces every decision point to be revisited. By default it
does not: an unhandled case is only **warning FS0025**, and the build still succeeds. Add a
`Suspended` case and two transitions silently keep compiling with a gap in them.

Make it binding in the Domain `.fsproj`:

```xml
<WarningsAsErrors>$(WarningsAsErrors);FS0025</WarningsAsErrors>
```

Then adding a case fails the build at every site that has not accounted for it, which is the entire
point.

For the same reason, **never write `| _ ->` when matching a domain state DU.** A wildcard is what turns
the compiler's list of "here is everywhere you must think" back into silence. Match cases explicitly,
even when several share a body:

```fsharp
| Order.Placed _
| Order.Cancelled _ -> Error PlaceError.NotADraft     // explicit, still fails when a case is added
```

## Updating an existing model

**Adding a state:** add the case, build, and fix every FS0025 the compiler reports — that list is the
complete set of places the new state matters. Do not shortcut it with a wildcard.

**Adding a field:** decide first whether it belongs to the model or to one state. If it is only
meaningful in some states, it goes in those cases, not on the record. A field added to the record
"because most states have it" is how the eight-combination version above gets rebuilt.

**Adding a transition:** it takes the model plus already-built value objects, and returns
`Result<Model, TError>` — or the bare model when nothing can fail. Its error DU names only its own rule.
Match states explicitly.

A transition **is** an MCP tool automatically — registration is by convention, and its parameters are
all safe. You add no attribute; you add a `///` doc comment.

This composes further than it first looks, so do not build plumbing you do not need: a **union of value
objects** is exposable, and so is a **collection value object over such a union**, whose `Make` may take
already-built elements (`Membership array | null`) rather than raw ones. A whole model and every one of
its transitions therefore register with no extra work, provided each field bottoms out in a value
object, a wire shape, or a record/union/array of those. `add-domain-function` has the full table, and
`dotnet run --project tools/McpAudit -- --report` answers it for a specific type.

**Rehydrating from a repository:** repositories rebuild models from persisted data, which means
reconstructing a state that `create` cannot produce. Give the model an explicit function for it that
validates the same way, and keep it obviously distinct from `create` so normal code doesn't reach for
it. A stored row that cannot be rebuilt is corrupt data — return the error, don't coerce it.

## Write the invariant where it can be found

A model's rules should be readable from the tool list without opening the file. `StoreMemberships.create`
saying *"Each store may appear once, and at most one membership may be Online"* answers "can he be online
at two stores?" outright. The same rule left only in the code costs a search every time it is asked.

So when a `create` or a transition enforces something, state it in that function's `///` summary — the
constraint, not just the mechanics. See `add-domain-function`.


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

1. Is the record public, with no private constructor? If you wanted one, which invariant were you
   guarding — and can it move into a type of its own?
2. Is every state a DU case, with no boolean flag or nullable field standing in for one?
3. Does each case carry exactly the data that state has, all of it `Req*` and always present?
4. Does every `Opt*` field pass all three questions above — nothing branches on it?
5. Is there no `option` wrapped around a value object?
6. Does `create` produce only the initial state, with other states reachable only through transitions?
7. Does each transition take built value objects and validate nothing itself, with an error type naming
   only its own rule, returning a bare model when it cannot fail?
8. Are there no `| _ ->` wildcards over a domain state DU?
9. Is `FS0025` in `WarningsAsErrors`, so an unhandled case fails the build?
10. Does `create` take the outside world's shape for every parameter — `string | null`,
   `Nullable<T>` for value types, `T array | null` for collections (never `seq` or F# `list`)? Run the
   null probe from `add-value-object`.
11. Does every public function carry a `///` `<summary>` and a `<param>` per parameter? They become the
   MCP tool's description and schema, and `tools/McpAudit` fails without them.
12. Does the **model type and each state DU** carry a `///` summary? `Domain.types` lists every
   documented type with its cases and fields — that is where an agent reads what states exist and which
   combinations are impossible, without opening source. Put a collection-wide invariant on the type that
   owns it. Nothing to regenerate: it is read from the assembly per call.
11. Does `dotnet build AgenticApp.slnx` — it fails on any warning (FS0025, FS1182, FS3261, FS3264, FS3265)
   **and on formatting drift**; run `dotnet fantomas src tools` to fix that.

Sanity check on the finished type: count the states it can represent and the states the domain actually
has. If the first number is larger, some combination of fields is expressing something illegal.
