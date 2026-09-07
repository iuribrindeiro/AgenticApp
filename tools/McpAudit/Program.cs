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

// Registration is by convention (DomainTools.all), so there is no attribute to look for:
// a function is a tool when `DomainTools.isExposable` accepts it, and it is known by
// `DomainTools.toolName`. Both are the server's own definitions, imported here rather
// than restated, so the audit cannot drift from what actually ships.
static string ToolName(MethodInfo m) => AgenticApp.Mcp.DomainTools.toolName(m);

// Audit exactly what the host registers, not a re-derived approximation.
var tools = AgenticApp.Mcp.DomainTools.all().ToList();
var registered = tools.Select(t => t.ProtocolTool.Name).ToHashSet();

// Every authored public static function, keyed by the name it would be exposed under.
// Not filtered by `isExposable`: the point is to find functions that are missing, and to
// re-check the parameters of ones that are not.
var authored = assembly.GetTypes()
    .Where(ty => ty.IsPublic || ty.IsNestedPublic)
    .SelectMany(ty => ty.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
    .Where(IsAuthoredFunction)
    .ToList();

// ---- 1. Coverage: a function that could be a tool must be one ----
foreach (var m in authored.Where(AgenticApp.Mcp.DomainTools.isExposable))
{
    if (!registered.Contains(ToolName(m)))
    {
        problems.Add($"NOT A TOOL     {ToolName(m)} — every parameter is safe, so it should be exposed, but DomainTools.all() did not register it");
    }
}

// Tool names are code paths, so two modules sharing a short name would silently collide
// and the client would see one tool where the domain has two.
foreach (var g in authored.GroupBy(ToolName).Where(g => g.Count() > 1))
{
    var owners = string.Join(", ", g.Select(m => m.DeclaringType!.FullName));
    problems.Add($"NAME COLLISION {g.Key} — declared by {owners}");
}

// Dropping a type annotation can silently generalise a function, and `isExposable` rejects
// generic methods - so the tool vanishes from the list with no error anywhere. ValueObject's
// constrained helpers are the only generics this repo intends to have.
foreach (var m in authored.Where(m => m.IsGenericMethod && m.DeclaringType!.Name != "ValueObject"))
{
    problems.Add($"GENERIC        {ToolName(m)} — inferred as generic, so it is silently excluded from the tool list; annotate a parameter to pin its type");
}

// ---- 2. Labelling and schema health of the tools that exist ----
var authoredByToolName = authored
    .GroupBy(ToolName)
    .ToDictionary(g => g.Key, g => g.First());

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
    if (authoredByToolName.TryGetValue(n, out var method))
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
        // `string | null` is redundant to F# - a string reference holds null either way - but
        // it is NOT redundant to the tool: without it the schema narrows from
        // ["string","null"] to "string", and a client then stops sending the null the domain
        // exists to reject, making the Missing error unreachable over MCP. The Domain never
        // takes a bare non-null string (internally it takes ReqStr), so every string
        // parameter here is a boundary one and must advertise null.
        if (authoredByToolName.TryGetValue(n, out var owner)
            && owner.GetParameters().FirstOrDefault(x => x.Name == p.Name)?.ParameterType == typeof(string)
            && p.Value.TryGetProperty("type", out var st)
            && st.ValueKind == JsonValueKind.String
            && st.GetString() == "string")
        {
            problems.Add($"NON-NULL STRING {n}.{p.Name} — annotate it `string | null`; without that the schema drops \"null\" and callers can no longer send the absence the domain validates");
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

// Domain.types claims its "what branches on this" list is complete over every exposed
// function, which is only true while every one of them carries a compiled quotation. One
// module missing [<ReflectedDefinition>] turns that guarantee into a confident lie, and
// nothing else would notice - the build succeeds and the tool still answers.
foreach (var m in authored.Where(AgenticApp.Mcp.DomainTools.isExposable))
{
    if (!AgenticApp.Mcp.DomainKnowledge.hasQuotation(m))
    {
        problems.Add($"NO QUOTATION   {ToolName(m)} — add [<ReflectedDefinition>] to its module; without it Domain.types cannot prove what does *not* branch on a state");
    }
}

// A <summary> is shown on every match and doubles as the MCP tool description, so a
// paragraph there is paid for by every reader of every broad question. Detail belongs in
// <remarks>, which Domain.types shows only to whoever asked about that thing by name.
const int summaryLimit = 200;

foreach (var (member, length) in AgenticApp.Mcp.DomainKnowledge.overLongSummaries(
             typeof(AgenticApp.Domain.StoreModel.Store).Assembly.Location, summaryLimit))
{
    problems.Add($"LONG SUMMARY   {member[2..]} — {length} chars; keep <summary> to one sentence (under {summaryLimit}) and move the reasoning into <remarks>");
}

// ---- 3. The docs must not name a tool that does not exist ----
// CLAUDE.md and the skills tell the agent which tool to reach for first. When one is
// renamed and a doc is not, the agent searches for a tool that isn't there and silently
// falls back to grepping source - which is the whole thing these tools exist to avoid.
// Only `Domain.*` is checked: those are the server's own tools, and unlike code paths
// such as `DomainTools.all` they are only ever written down as tool names. `Domain.dll`
// and friends share the shape, so file extensions are excluded.
string[] notToolNames = ["dll", "fsproj", "csproj", "fs", "fsx", "fsi", "xml", "md", "json", "slnx", "targets"];
static string DocsRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgenticApp.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}

var docsRoot = DocsRoot();
var docFiles = new List<string>();
var claudeMd = Path.Combine(docsRoot, "CLAUDE.md");
if (File.Exists(claudeMd))
{
    docFiles.Add(claudeMd);
}

var skillsDir = Path.Combine(docsRoot, ".claude", "skills");
if (Directory.Exists(skillsDir))
{
    docFiles.AddRange(Directory.GetFiles(skillsDir, "SKILL.md", SearchOption.AllDirectories));
}

foreach (var file in docFiles)
{
    var text = File.ReadAllText(file);
    foreach (var referenced in Regex.Matches(text, @"`(Domain\.[a-z][A-Za-z0-9]*)`")
                 .Select(m => m.Groups[1].Value)
                 .Distinct())
    {
        if (notToolNames.Contains(referenced[(referenced.IndexOf('.') + 1)..], StringComparer.Ordinal))
        {
            continue;
        }

        if (!registered.Contains(referenced))
        {
            problems.Add($"STALE DOC TOOL {referenced} — named in {Path.GetRelativePath(docsRoot, file)} but no such tool is registered; the agent will search for it, fail, and grep source instead");
        }
    }
}

// ---- 4. Inventory: every public function, and why it is or is not a tool ----
if (args.Contains("--report"))
{
    // Why a function is not exposed, in the order DomainTools.isExposable rejects it.
    static string WhyNotATool(MethodInfo m)
    {
        if (m.IsGenericMethod)
        {
            return "is generic — a schema cannot be generated for it";
        }
        if (m.GetParameters().Length == 0)
        {
            return "takes no parameters — there would be nothing to invoke it with";
        }

        var blocking = m.GetParameters().Where(p => !IsSafeParameter(p.ParameterType)).ToList();
        return blocking.Count == 0
            ? "no blocking parameter — SHOULD BE A TOOL"
            : "blocked by " + string.Join(", ", blocking.Select(p => $"{p.Name}: {p.ParameterType.Name}"));
    }

    Console.WriteLine("Public functions in the Domain:\n");
    foreach (var m in authored.OrderBy(ToolName, StringComparer.Ordinal))
    {
        Console.WriteLine(registered.Contains(ToolName(m))
            ? $"  TOOL      {ToolName(m)}"
            : $"  not tool  {ToolName(m),-34} {WhyNotATool(m)}");
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
