---
name: add-orleans-grain-method
description: Add an Orleans grain method that delegates to an F# Domain function, plus its matching MCP tool. Use when adding stateful or actor-based behavior.
disable-model-invocation: true
---

Add the Orleans grain method described in $ARGUMENTS.

1. **Find or create the Domain function** this grain method delegates to (`/add-domain-function` if missing).
2. **Declare the method on the grain interface** and implement it on the grain. Keep it thin: read grain
   state and/or repository data, call the Domain function, apply the returned state, persist, return.
   State transitions are decided by the Domain function, not by the grain.
3. **Orleans specifics**: arguments and return types must be serializable and marked accordingly; grain
   methods return `Task`/`ValueTask`; do not leak grain references into the Domain project.

**On the MCP tool.** The Domain function you delegate to is *already* an MCP tool — registration is by
convention, so nothing to add there (see `/add-domain-function`). What is **not** yet automated is the
adapter itself: `DomainTools.all()` scans only the Domain assembly, so a Orleans grain method that does something
beyond calling one Domain function has no tool. Until that is built, either keep the adapter a pure
pass-through (then the Domain tool already covers it) or raise the gap rather than hand-rolling a
one-off registration.

**Mapping errors out.** A Domain function returns `Validation<'T,'E>` or `Result<'T,'E>`. Render
failures with that model's `describe` — never let an error union escape as an exception or a raw
`{"Case":...}` payload.

Finish by reporting the grain interface + method name and running `dotnet build AgenticApp.slnx` plus
`dotnet run --project tools/McpAudit`. There are no tests in this repo yet, so do not add or run any.
