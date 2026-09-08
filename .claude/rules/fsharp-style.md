---
paths:
  - "**/*.fs"
  - "**/*.fsi"
  - "**/*.fsx"
---

# F# style

These load when you touch an F# file. `CLAUDE.md` holds what every session needs; enforcement —
which of these the build checks and which rest on review — is in its **Build strictness** section.

## Doc comments: one sentence in `<summary>`, the reasoning in `<remarks>`

Both types and functions. `<summary>` is the rule in a sentence; it is the MCP tool's description and is
shown on **every** match, so an essay there is paid for by every reader of every broad question.
`<remarks>` carries the why and the edge cases, and `Domain.types` shows it only to a reader who asked
about that thing **by name or case** — not to one who matched its prose incidentally. Write the
reasoning; just put it in the right tag.

- `tools/McpAudit` fails a summary over 200 characters as `LONG SUMMARY`.
- **Escape `<`, `>` and `&`** once a comment uses tags: F# escapes *untagged* doc comments for you and
  passes tagged ones through verbatim, so a bare `typedefof<_>` makes `Domain.xml` unparseable — which
  silently costs every tool its description.

## Type inference over annotations

F# infers types; state them only where the compiler or the **tool schema** needs them. Carry the meaning
in the parameter's **name** instead — camelCase of its type (`deliveryman`, `storeMemberships`, `error`),
never `d`, `v` or `e`. That name is also the MCP schema's property name, so a vague one reaches every
client.

**Never annotate a return type.** Keep a parameter's annotation only for a `string | null` boundary (the
schema loses `"null"` without it, though F# does not care), a parameter matched against `| null`, an
interface implementation, a generic, or a value reached only through a coercion. `Nullable<T>` needs
nothing; everything else drops.

Going too far fails two ways that the compiler cannot see, so `tools/McpAudit` owns them: **`GENERIC`**
(an over-generalised function silently vanishes from the tool list) and **`NON-NULL STRING`** (the
narrowing above). It may also leave an `open` unused. Reasoning and examples are in `/add-value-object`.

## Prefer the built-in combinators

A lambda whose body only threads its parameter through named functions is a composition.
That covers a tuple projection — `List.choose (snd >> Option.ofObj)`, never
`List.choose (fun (_, m) -> Option.ofObj m)`; a property — `Array.exists (_.PropertyType >> go seen)`;
and a pipeline — `Result.bind (StoreMemberships.OfList >> Validation.field DeliverymanError.Stores)`,
never `fun ms -> StoreMemberships.OfList ms |> Validation.field DeliverymanError.Stores`.
Eta-reduce the same way (`Array.filter isError`), and reach for `fst`, `id`, `not` and `ignore`
wherever a hand-written lambda restates them.

Stop where the composition needs an operator section or a flip: `fun (_, n) -> n > 1` stays
a lambda, because `snd >> ((<) 1)` reads backwards and buries the comparison.

## No unused code

Nothing may be unused: no function (private or public), type, case, parameter or `open`. If something
has no caller, delete it rather than leaving it for a future caller that may not arrive. Write the
function when the thing that needs it exists.

FS1182 catches only locals and parameters. Unused `open`s need `tools/check-unused-opens.sh` (run before
a PR). Unused functions have no check at all — delete on sight.
