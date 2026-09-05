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

    /// A function's documented intent: what it does, and what each parameter means.
    type Doc =
        { Summary: string
          Parameters: Map<string, string> }

    /// member-name -> doc, keyed the way the compiler writes it: M:Type.Method(args).
    let load (assembly: Assembly) : Map<string, Doc> =
        let xmlPath =
            match Path.ChangeExtension(assembly.Location, ".xml") with
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

                    let parameters =
                        m.Elements(XName.Get "param")
                        |> Seq.choose (fun p ->
                            match p.Attribute(XName.Get "name") with
                            | null -> None
                            | n -> Some(n.Value, normalise p.Value))
                        |> Map.ofSeq

                    if summary = "" && parameters.IsEmpty then
                        None
                    else
                        Some(
                            name.Value,
                            { Summary = summary
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
