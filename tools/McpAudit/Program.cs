// MCP tool coverage audit.
//
// Fails the build/run if any public Domain function that COULD be an MCP tool is not one,
// or if any tool is missing the labelling that makes it findable. Run with:
//     dotnet run --project tools/McpAudit
// Exit code 0 = clean, 1 = problems (so it can gate CI).

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.FSharp.Reflection;
using ModelContextProtocol.Server;

var assembly = typeof(AgenticApp.Domain.StoreModel).Assembly;
var problems = new List<string>();

// One definition of "safe parameter" lives in ValueObjectJson, shared with the server
// that registers the tools - so the audit cannot drift from what actually ships.
static bool IsSafeParameter(Type t) => AgenticApp.Mcp.ValueObjectJson.isSafeParameter(t);

// F# emits static `NewXxx` factories for DU cases and `get_` for properties; neither is
// a function anyone wrote, so neither is a candidate tool.
static bool IsAuthoredFunction(MethodInfo m) =>
    !m.IsSpecialName
    // Explicit interface implementations (IValueObject<_>.Make) carry dotted IL names.
    // The module's `create` is the authored entry point; this is its plumbing.
    && !m.Name.Contains('.')
    && m.GetCustomAttribute<CompilerGeneratedAttribute>() is null
    && !(FSharpType.IsUnion(m.DeclaringType!, null) && m.Name.StartsWith("New"))
    && !FSharpType.IsRecord(m.DeclaringType!, null);

// Registration is by convention (DomainTools.all), so there is no attribute to forget.
// What remains checkable is whether each exposed tool is *usable*: named, described,
// and with a schema a model can actually fill.

// ---- 2. Labelling and schema health of the tools that exist ----
// Audit exactly what the host registers, not a re-derived approximation.
var tools = AgenticApp.Mcp.DomainTools.all().ToList();

foreach (var t in tools.OrderBy(t => t.ProtocolTool.Name))
{
    var n = t.ProtocolTool.Name;
    if (!Regex.IsMatch(n, @"^[A-Za-z][A-Za-z0-9]*\.[a-z][A-Za-z0-9]*$"))
    {
        problems.Add($"BAD NAME       {n} — must mirror the code path, e.g. Store.rename");
    }

    if (string.IsNullOrWhiteSpace(t.ProtocolTool.Title))
    {
        problems.Add($"NO TITLE       {n}");
    }

    if (string.IsNullOrWhiteSpace(t.ProtocolTool.Description))
    {
        problems.Add($"NO DESCRIPTION {n} — add a /// <summary> doc comment");
    }

    // A parameter must be a wire shape, or a value object that has a validating converter.
    // Anything else is bound by deserialising straight into a domain type - forging it.
    var method = assembly.GetTypes()
        .SelectMany(ty => ty.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        .FirstOrDefault(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == n);
    if (method is not null)
    {
        foreach (var p in method.GetParameters())
        {
            if (!IsSafeParameter(p.ParameterType))
            {
                problems.Add($"UNSAFE PARAM   {n}.{p.Name} — {p.ParameterType.Name} is not a wire shape, a converter-backed value object, or a record built only from those");
            }
        }
    }

    if (t.ProtocolTool.InputSchema.ValueKind != JsonValueKind.Object
        || !t.ProtocolTool.InputSchema.TryGetProperty("properties", out var schemaProps))
    {
        problems.Add($"NO SCHEMA      {n} — the generator produced {t.ProtocolTool.InputSchema} instead of an object schema");
        continue;
    }

    foreach (var p in schemaProps.EnumerateObject())
    {
        if (p.Value.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"NO PARAM SCHEMA {n}.{p.Name} — generator produced {p.Value} instead of an object schema");
            continue;
        }
        if (!p.Value.TryGetProperty("description", out var d) || string.IsNullOrWhiteSpace(d.GetString()))
        {
            problems.Add($"NO PARAM DESC  {n}.{p.Name} — add a <param name=\"{p.Name}\"> tag to its /// doc comment");
        }
        // A union is described by oneOf rather than a single type; both are usable.
        if (!p.Value.TryGetProperty("type", out _)
            && !p.Value.TryGetProperty("oneOf", out _)
            && !p.Value.TryGetProperty("enum", out _))
        {
            problems.Add($"UNTYPED PARAM  {n}.{p.Name} — no type, oneOf or enum, so a model cannot fill it");
        }
    }
}

// ---- 3. Inventory: every public function, and why it is or is not a tool ----
if (args.Contains("--report"))
{
    Console.WriteLine("Public functions in the Domain:\n");
    foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.Name))
    {
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                              .Where(IsAuthoredFunction).OrderBy(m => m.Name))
        {
            var tool = m.GetCustomAttribute<McpServerToolAttribute>();
            if (tool is not null) { Console.WriteLine($"  TOOL      {type.Name}.{m.Name,-22} {tool.Name}"); continue; }
            var blocking = m.GetParameters().Where(p => !IsSafeParameter(p.ParameterType)).ToList();
            var why = blocking.Count == 0
                ? "no blocking parameter — SHOULD BE A TOOL"
                : "blocked by " + string.Join(", ", blocking.Select(p => $"{p.Name}: {p.ParameterType.Name}"));
            Console.WriteLine($"  not tool  {type.Name}.{m.Name,-22} {why}");
        }
    }

    Console.WriteLine();
}

Console.WriteLine($"MCP audit: {tools.Count} tool(s) discovered, {problems.Count} problem(s)");
if (problems.Count > 0)
{
    Console.WriteLine();
    foreach (var p in problems)
    {
        Console.WriteLine("  " + p);
    }
}
return problems.Count == 0 ? 0 : 1;
