---
name: add-grpc-method
description: Add a gRPC service method that delegates to an F# Domain function, plus its matching MCP tool. Use when exposing functionality over gRPC.
disable-model-invocation: true
---

Add the gRPC method described in $ARGUMENTS.

1. **Find or create the Domain function** this method delegates to (`/add-domain-function` if missing).
2. **Update the `.proto`**: add the rpc and its request/response messages. Keep field numbering stable —
   never renumber or reuse existing field numbers. Rebuild so the generated types are available.
3. **Implement the service method thin**: map the request message to Domain inputs, fetch what the Domain
   function needs via repositories, call it, map the result out. Translate domain errors to appropriate
   `RpcException` status codes. No business logic in the service class.

**On the MCP tool.** The Domain function you delegate to is *already* an MCP tool — registration is by
convention, so nothing to add there (see `/add-domain-function`). What is **not** yet automated is the
adapter itself: `DomainTools.all()` scans only the Domain assembly, so a gRPC method that does something
beyond calling one Domain function has no tool. Until that is built, either keep the adapter a pure
pass-through (then the Domain tool already covers it) or raise the gap rather than hand-rolling a
one-off registration.

**Mapping errors out.** A Domain function returns `Validation<'T,'E>` or `Result<'T,'E>`. Render
failures with that model's `describe` — never let an error union escape as an exception or a raw
`{"Case":...}` payload.

Finish by reporting the service + rpc name and running `dotnet build AgenticApp.slnx` plus
`dotnet run --project tools/McpAudit`. There are no tests in this repo yet, so do not add or run any.
