namespace AgenticApp.Mcp

open System.IO
open System.Reflection
open System.Xml.Linq

/// Reads the compiler-generated XML documentation beside an assembly, so a `///`
/// comment on a domain function becomes that tool's description. One place to write
/// intent: the code, where it is also useful to a human reading the source.
module DomainDocs =

    let private normalise (text: string) =
        text.Split('\n')
        |> Array.map (fun l -> l.Trim())
        |> Array.filter (fun l -> l <> "")
        |> String.concat " "

    /// A function's documented intent: what it does, why, and what each parameter means.
    ///
    /// `Summary` is the rule in a sentence and is always shown; `Remarks` carries the
    /// reasoning and the edge cases, and is shown only where the reader asked about this
    /// thing directly. Splitting them is what keeps a broad question from paying for
    /// several essays.
    type Doc =
        { Summary: string
          Remarks: string
          Parameters: Map<string, string> }

    /// member-name -> doc, keyed the way the compiler writes it: M:Type.Method(args).
    ///
    /// Takes the path the assembly was loaded *from*, not `assembly.Location`: the server
    /// loads Domain out of a byte array so the file stays unlocked for rebuilds, and that
    /// leaves Location empty - which silently cost every served tool its description.
    let load (assemblyPath: string) : Map<string, Doc> =
        let xmlPath =
            match Path.ChangeExtension(assemblyPath, ".xml") with
            | null -> ""
            | path -> path

        if not (File.Exists xmlPath) then
            Map.empty
        else
            XDocument.Load(xmlPath).Descendants(XName.Get "member")
            |> Seq.choose (fun m ->
                match m.Attribute(XName.Get "name") with
                | null -> None
                | name ->
                    let summary =
                        match m.Element(XName.Get "summary") with
                        | null -> ""
                        | s -> normalise s.Value

                    let remarks =
                        match m.Element(XName.Get "remarks") with
                        | null -> ""
                        | r -> normalise r.Value

                    let parameters =
                        m.Elements(XName.Get "param")
                        |> Seq.choose (fun p ->
                            match p.Attribute(XName.Get "name") with
                            | null -> None
                            | n -> Some(n.Value, normalise p.Value))
                        |> Map.ofSeq

                    if summary = "" && remarks = "" && parameters.IsEmpty then
                        None
                    else
                        Some(
                            name.Value,
                            { Summary = summary
                              Remarks = remarks
                              Parameters = parameters }
                        ))
            |> Map.ofSeq

    /// The compiler keys methods as `M:Type.Method(paramTypes)`; match on the prefix so
    /// we do not have to reproduce its parameter-type encoding.
    let docFor (docs: Map<string, Doc>) (m: MethodInfo) =
        let declaring =
            match m.DeclaringType with
            | null -> ""
            | t ->
                match t.FullName with
                | null -> ""
                // Reflection writes nested types as Outer+Inner; XML docs use Outer.Inner.
                | name -> name.Replace("+", ".")

        let prefix = $"M:%s{declaring}.%s{m.Name}"

        docs
        |> Map.tryPick (fun key value ->
            if key = prefix || key.StartsWith(prefix + "(") then
                Some value
            else
                None)
