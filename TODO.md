# TODO

Setup follow-ups from `/init`. Nothing here is started — each item is actioned only on request.

## 1. Scaffold the projects, then revisit the skills
- [ ] Create `src/Domain` (F# classlib, .NET 10), the host project, and the MCP server project
- [ ] Add each to `AgenticApp.slnx`
- [ ] Rewrite the five skills in `.claude/skills/` to name real files and real MCP registration points
      instead of "mirror the existing style"

**Why:** the skills currently describe an intended structure that has no code behind it.

## 2. Enforce MCP tool parity, don't just document it
- [ ] Add a test that reflects over REST endpoints, gRPC methods, SignalR hub methods, and Orleans grain
      methods and asserts a matching MCP tool exists for each
- [ ] Do the same for publicly exposed F# Domain functions

**Why:** the skills only prevent drift when they're used. A test makes drift impossible.

## 3. Add Fantomas for F# formatting
- [ ] `dotnet new tool-manifest` (if absent), then `dotnet tool install fantomas`
- [ ] Extend the `PostToolUse` hook in `.claude/settings.json` to run Fantomas on edited `.fs` files

**Why:** the current hook covers only C#. F# is the more important half of this codebase.

## 4. Set up the test project early
- [ ] Create the Domain test project and wire it into the solution
- [ ] Confirm `dotnet test AgenticApp.slnx` runs green

**Why:** items 1–3 all assume this works; until it does, nothing verifies its own changes.

## 5. Refine the skills with skill-creator
- [ ] `/plugin install skill-creator@claude-plugins-official`
- [ ] `/skill-creator add-rest-endpoint` (and the other four) to refine them against evals

## 6. Review available plugins
- [ ] Browse `/plugin` for bundles that fit this stack
      (dotnet-test, dotnet-aspnetcore, and mongodb are already active)
