module AgenticApp.Mcp.Program

open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

[<EntryPoint>]
let main _ =
    let builder = Host.CreateApplicationBuilder()

    // stdio carries the protocol, so every log line must go to stderr - anything
    // written to stdout corrupts the JSON-RPC stream.
    builder.Logging.ClearProviders() |> ignore

    builder.Logging.AddConsole(fun options -> options.LogToStandardErrorThreshold <- LogLevel.Trace)
    |> ignore

    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools(DomainTools.all ())
    |> ignore

    builder.Build().Run()
    0
