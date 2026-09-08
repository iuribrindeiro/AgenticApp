namespace AgenticApp.Mcp

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open System.Threading

/// Loads the Domain assembly into a swappable context and watches it for rebuilds.
///
/// The MCP server otherwise serves whatever the Domain looked like when it started, so a
/// function added mid-session is invisible until the client reconnects. A collectible
/// AssemblyLoadContext lets the assembly be dropped and reloaded in place; the tool list
/// is then rebuilt and the SDK notifies the client.
module DomainLoader =

    /// Resolves the Domain's own dependencies (FSharp.Core) from beside it, otherwise the
    /// context falls back to the default one and types cross contexts inconsistently.
    type private DomainContext(path: string) as this =
        inherit AssemblyLoadContext(name = $"domain-%d{DateTime.UtcNow.Ticks}", isCollectible = true)

        let resolver = AssemblyDependencyResolver(path)
        do this.add_Resolving (fun _ name -> this.ResolveFrom name)

        member private _.ResolveFrom(name: AssemblyName) : Assembly | null =
            match resolver.ResolveAssemblyToPath name with
            | null -> null
            | resolved -> this.LoadFromAssemblyPath resolved

    /// Read the bytes rather than mapping the file, so a rebuild is never blocked by the
    /// server holding a lock on the DLL it is watching.
    let private loadFrom (context: AssemblyLoadContext) (path: string) =
        use dll = new MemoryStream(File.ReadAllBytes path)
        context.LoadFromStream dll

    /// A loaded Domain plus the context holding it, so the caller can drop both.
    type Loaded =
        { Assembly: Assembly
          Context: AssemblyLoadContext
          Path: string }

        member this.Unload() = this.Context.Unload()

    let load (path: string) : Loaded =
        let context = DomainContext path :> AssemblyLoadContext

        { Assembly = loadFrom context path
          Context = context
          Path = path }

    /// Calls `onChanged` after the assembly settles. A build writes the file more than
    /// once, so changes are debounced; without that the reload races the compiler and
    /// loads a half-written image.
    let watch (path: string) (debounce: TimeSpan) (onChanged: unit -> unit) : IDisposable =
        let directory = Path.GetDirectoryName path

        match directory with
        | null ->
            { new IDisposable with
                member _.Dispose() = () }
        | directory ->
            let fileName =
                match Path.GetFileName path with
                | null -> "*.dll"
                | name -> name

            let watcher = new FileSystemWatcher(directory, fileName)
            let pending = new Timer(fun _ -> onChanged ())

            let reschedule _ =
                pending.Change(debounce, Timeout.InfiniteTimeSpan) |> ignore

            watcher.Changed.Add reschedule
            watcher.Created.Add reschedule
            watcher.Renamed.Add reschedule

            watcher.NotifyFilter <-
                NotifyFilters.LastWrite
                ||| NotifyFilters.FileName
                ||| NotifyFilters.Size

            watcher.EnableRaisingEvents <- true

            { new IDisposable with
                member _.Dispose() =
                    watcher.Dispose()
                    pending.Dispose() }
