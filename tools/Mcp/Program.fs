module AgenticApp.Mcp.Program

open System
open System.IO

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open ModelContextProtocol.Server

/// Where the Domain assembly is: this server's *own* output copy, not the one under
/// src/Domain/bin. MSBuild refreshes this copy when the referencing project builds, so
/// `dotnet build AgenticApp.slnx` triggers a reload while building `Domain.fsproj` alone
/// does not - it updates a file the server never reads. The path is logged at startup so
/// a reload that never fires can be diagnosed without reading this comment.
let private domainPath () =
    let here = AppContext.BaseDirectory
    Path.GetFullPath(Path.Combine(here, "Domain.dll"))

/// Replaces the tool list in place. `DeferChangedEvents` batches the clear and the adds
/// into a single notifications/tools/list_changed rather than one per tool.
let private publish (tools: McpServerPrimitiveCollection<McpServerTool> | null) (rebuilt: McpServerTool list) =
    match tools with
    | null -> ()
    | tools ->

        use _batch = tools.DeferChangedEvents()
        tools.Clear()

        for tool in rebuilt do
            tools.Add tool

[<EntryPoint>]
let main _ =
    let builder = Host.CreateApplicationBuilder()

    // stdio carries the protocol, so every log line must go to stderr - anything
    // written to stdout corrupts the JSON-RPC stream.
    builder.Logging.ClearProviders() |> ignore

    builder.Logging.AddConsole(fun options -> options.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    let path = domainPath ()
    let loaded = ref (DomainLoader.load path)

    let initialTools = DomainTools.forAssembly loaded.Value.Assembly loaded.Value.Path

    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools initialTools
    |> ignore

    let host = builder.Build()

    // Mutate the collection the server actually serves from. Holding our own collection
    // and passing it to WithTools does not work: the builder copies the tools into
    // McpServerOptions.ToolCollection, so later edits to ours are invisible to clients.
    let tools =
        host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection

    let log =
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger "DomainReload"

    log.LogInformation("Watching {Path} for Domain rebuilds", path)

    // Descriptions are the whole point of the tool list - they are how the domain is read
    // without opening source. Losing them is silent (every tool still works, it is just
    // unlabelled), and it happened once already: loading the assembly from a byte array
    // leaves Location empty, so Domain.xml was never found. Say so rather than serve them.
    if
        not initialTools.IsEmpty
        && initialTools
           |> List.forall (fun t -> String.IsNullOrWhiteSpace t.ProtocolTool.Description)
    then
        log.LogError(
            "Every tool is missing its description: no XML docs were found next to {Path}. Check GenerateDocumentationFile.",
            path
        )

    // Rebuilding the Domain swaps the tool list without a client reconnect. A failed
    // reload keeps the previous list: a half-written assembly must not empty the server.
    let reloadLock = obj ()

    use _watcher =
        DomainLoader.watch path (TimeSpan.FromMilliseconds 500.0) (fun () ->
            lock reloadLock (fun () ->
                try
                    let previous = loaded.Value
                    let next = DomainLoader.load path
                    publish tools (DomainTools.forAssembly next.Assembly next.Path)
                    loaded.Value <- next
                    previous.Unload()

                    log.LogInformation(
                        "Domain reloaded: {Count} tools",
                        List.length (DomainTools.forAssembly next.Assembly next.Path)
                    )
                with ex ->
                    log.LogWarning(ex, "Domain reload failed; keeping the previous tool list")))

    host.Run()
    0
