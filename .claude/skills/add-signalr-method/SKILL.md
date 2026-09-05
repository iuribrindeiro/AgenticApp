---
name: add-signalr-method
description: Add a SignalR hub method that delegates to an F# Domain function, plus its matching MCP tool. Use when exposing real-time functionality over SignalR.
disable-model-invocation: true
---

Add the SignalR hub method described in $ARGUMENTS.

1. **Find or create the Domain function** this hub method delegates to (`/add-domain-function` if missing).
2. **Implement the hub method thin**: map arguments to Domain inputs, use repositories for any data needed,
   call the Domain function, then send results to the right clients/groups. No business logic in the hub.
3. **Respect the Redis backplane**: the app scales out, so a hub instance cannot assume it holds the
   connection. Address clients through groups or user identifiers rather than in-process connection state,
   and keep per-connection state out of static/in-memory fields.

**On the MCP tool.** The Domain function you delegate to is *already* an MCP tool — registration is by
convention, so nothing to add there (see `/add-domain-function`). What is **not** yet automated is the
adapter itself: `DomainTools.all()` scans only the Domain assembly, so a SignalR hub method that does something
beyond calling one Domain function has no tool. Until that is built, either keep the adapter a pure
pass-through (then the Domain tool already covers it) or raise the gap rather than hand-rolling a
one-off registration.

**Mapping errors out.** A Domain function returns `Validation<'T,'E>` or `Result<'T,'E>`. Render
failures with that model's `describe` — never let an error union escape as an exception or a raw
`{"Case":...}` payload.

Finish by reporting the hub + method name and running `dotnet build AgenticApp.slnx` plus
`dotnet run --project tools/McpAudit`. There are no tests in this repo yet, so do not add or run any.
