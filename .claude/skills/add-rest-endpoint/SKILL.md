---
name: add-rest-endpoint
description: Add a REST API endpoint that delegates to an F# Domain function, plus its matching MCP tool. Use when exposing functionality over HTTP.
disable-model-invocation: true
---

Add the REST endpoint described in $ARGUMENTS.

1. **Find or create the Domain function** this endpoint delegates to. Business logic belongs in the F#
   Domain project — if it does not exist yet, use `/add-domain-function` first and come back.
2. **Write the endpoint thin**: bind the request, call repositories for any data the Domain function needs,
   call the Domain function, map its `Result` to an HTTP status + response body. No rules, branching on
   business conditions, or calculations in the endpoint itself.
3. Use correct HTTP semantics (verb, status codes, route shape) and add OpenAPI metadata consistent with
   the endpoints already present.

**On the MCP tool.** The Domain function you delegate to is *already* an MCP tool — registration is by
convention, so nothing to add there (see `/add-domain-function`). What is **not** yet automated is the
adapter itself: `DomainTools.all()` scans only the Domain assembly, so a REST endpoint that does something
beyond calling one Domain function has no tool. Until that is built, either keep the adapter a pure
pass-through (then the Domain tool already covers it) or raise the gap rather than hand-rolling a
one-off registration.

**Mapping errors out.** A Domain function returns `Validation<'T,'E>` or `Result<'T,'E>`. Render
failures with that model's `describe` — never let an error union escape as an exception or a raw
`{"Case":...}` payload.

Finish by reporting the route and running `dotnet build AgenticApp.slnx` plus
`dotnet run --project tools/McpAudit`. There are no tests in this repo yet, so do not add or run any.
