namespace AgenticApp.Mcp

open System
open System.Reflection
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection

/// Renders the domain's documented intent as prose, on request.
///
/// The tool list answers *what can be called*; this answers *what the domain allows*.
/// Both read the same `///` comments, and this is generated per call from the loaded
/// assembly, so it reloads with the domain and can never be stale.
module DomainKnowledge =

    /// Value objects are single-case DUs with a private representation, and F# reflection
    /// reports IsUnion = false for those unless non-public members are allowed.
    let private allMembers = BindingFlags.Public ||| BindingFlags.NonPublic

    let rec private typeNameImpl (t: Type) : string =
        if t.IsArray then
            match t.GetElementType() with
            | null -> t.Name
            | e -> typeNameImpl e + " array"
        elif t.IsGenericType then
            let def = t.GetGenericTypeDefinition().Name
            let args = t.GetGenericArguments() |> Array.map typeNameImpl

            if def.StartsWith "FSharpResult" then
                $"Result<%s{args[0]}, %s{args[1]}>"
            elif def.StartsWith "FSharpOption" then
                $"%s{args[0]} option"
            elif def.StartsWith "FSharpList" then
                $"%s{args[0]} list"
            elif def.StartsWith "Nullable" then
                $"Nullable<%s{args[0]}>"
            else
                let bare =
                    if def.Contains "`" then
                        def.Substring(0, def.IndexOf '`')
                    else
                        def

                let joined = String.Join(", ", args)
                $"%s{bare}<%s{joined}>"
        else
            match t.Name with
            | "String" -> "string"
            | "Int64" -> "int64"
            | "Int32" -> "int"
            | "Boolean" -> "bool"
            | "Double" -> "float"
            | "Unit" -> "unit"
            | "Object" -> "obj"
            | n -> n

    and private typeName (t: Type) : string = typeNameImpl t

    let private nullability = NullabilityInfoContext()

    /// `string` and `string | null` are different contracts - the second is what lets a
    /// caller send the null the domain exists to reject - so the rendering must say which.
    let private paramType (p: ParameterInfo) =
        let name = typeName p.ParameterType

        if
            not p.ParameterType.IsValueType
            && nullability.Create(p).WriteState = NullabilityState.Nullable
        then
            name + " | null"
        else
            name


    /// F# compiles a multi-case DU's cases as nested subtypes and IsUnion says yes to
    /// those too, so StoreError's `Id` case would be listed as a type of its own.
    let private isUnionCase (t: Type) =
        match t.BaseType with
        | null -> false
        | b -> FSharpType.IsUnion(b, allMembers)

    let private isDataType (t: Type) =
        not (isUnionCase t)
        && (FSharpType.IsUnion(t, allMembers) || FSharpType.IsRecord(t, allMembers))

    /// An unnamed DU field compiles to `Item`, `Item2`... Printing that documents the
    /// compiler rather than the domain.
    let private isPositional (name: string) =
        name = "Item"
        || (name.StartsWith "Item"
            && name.Length > 4
            && name.Substring 4 |> Seq.forall Char.IsDigit)

    /// A value object is a single-case DU repeating the type name; its case list would
    /// say `ReqStr of string`, which is noise. The wire type is what matters.
    let private wrappedWireType (t: Type) =
        if not (FSharpType.IsUnion(t, allMembers)) then
            None
        else
            let cases = FSharpType.GetUnionCases(t, allMembers)

            if cases.Length <> 1 || cases[0].Name <> t.Name then
                None
            else
                match cases[0].GetFields() with
                | [| f |] -> Some(typeName f.PropertyType)
                | _ -> None

    /// The repo keeps one top-level module per file, named after it with an optional
    /// `Model` suffix: `DeliverymanModel` is `Deliveryman.fs`.
    let private sourceOf (t: Type) =
        let full =
            (match t.FullName with
             | null -> t.Name
             | f -> f)
                .Replace('+', '.')

        let rest =
            if full.StartsWith "AgenticApp.Domain." then
                full.Substring "AgenticApp.Domain.".Length
            else
                full

        let head =
            match rest.IndexOf '.' with
            | -1 -> rest
            | i -> rest.Substring(0, i)

        if head.EndsWith "Model" then
            head.Substring(0, head.Length - 5)
        else
            head

    let private docOfType (docs: Map<string, DomainDocs.Doc>) (t: Type) =
        let key =
            "T:"
            + (match t.FullName with
               | null -> t.Name
               | f -> f)
                .Replace('+', '.')

        Map.tryFind key docs

    let private remarksOf (docs: Map<string, DomainDocs.Doc>) (t: Type) =
        match docOfType docs t with
        | Some d -> d.Remarks
        | None -> ""

    let private summaryOf (docs: Map<string, DomainDocs.Doc>) (t: Type) =
        let key =
            "T:"
            + (match t.FullName with
               | null -> t.Name
               | f -> f)
                .Replace('+', '.')

        match Map.tryFind key docs with
        | Some d -> d.Summary
        | None -> ""

    let private appendType (sb: StringBuilder) (t: Type) (summary: string) (remarks: string) =
        let kind =
            match wrappedWireType t with
            | Some wire -> $" — value object over `%s{wire}`"
            | None when FSharpType.IsRecord(t, allMembers) -> " — record"
            | None -> " — union"

        sb.AppendLine().Append($"**%s{t.Name}**%s{kind}") |> ignore

        sb.AppendLine(if summary = "" then "" else $". %s{summary}") |> ignore

        if remarks <> "" then
            sb.AppendLine($"  %s{remarks}") |> ignore

        match wrappedWireType t with
        | Some _ -> ()
        | None ->
            if FSharpType.IsUnion(t, allMembers) then
                for c in FSharpType.GetUnionCases(t, allMembers) do
                    let shape =
                        match c.GetFields() with
                        | [||] -> ""
                        | fs ->
                            let parts =
                                fs
                                |> Array.map (fun f ->
                                    if isPositional f.Name then
                                        typeName f.PropertyType
                                    else
                                        $"%s{f.Name}: %s{typeName f.PropertyType}")

                            " of " + String.Join(" * ", parts)

                    sb.AppendLine($"- `%s{c.Name}%s{shape}`") |> ignore
            elif FSharpType.IsRecord(t, allMembers) then
                for f in FSharpType.GetRecordFields(t, allMembers) do
                    sb.AppendLine($"- `%s{f.Name}: %s{typeName f.PropertyType}`") |> ignore

    let private appendFunction
        (sb: StringBuilder)
        (name: string)
        (m: MethodInfo)
        (doc: DomainDocs.Doc option)
        (direct: bool)
        =
        let ps = m.GetParameters()

        let signature =
            if ps.Length = 0 then
                "unit"
            else
                ps |> Array.map (fun p -> $"(%s{p.Name}: %s{paramType p})") |> String.concat " "

        sb.AppendLine().AppendLine($"### %s{name}") |> ignore
        sb.AppendLine($"`%s{signature} → %s{typeName m.ReturnType}`") |> ignore

        match doc with
        | Some d when d.Summary <> "" -> sb.AppendLine().AppendLine d.Summary |> ignore
        | _ -> ()

        // Same rule as for types: the reasoning is shown to whoever asked for this function
        // by name, not to a broad sweep that merely matched its prose.
        match doc with
        | Some d when direct && d.Remarks <> "" -> sb.AppendLine().AppendLine d.Remarks |> ignore
        | _ -> ()

        match doc with
        | Some d when not d.Parameters.IsEmpty ->
            sb.AppendLine() |> ignore

            for p in ps do
                match Map.tryFind p.Name d.Parameters with
                | Some text -> sb.AppendLine($"- `%s{p.Name}` — %s{text}") |> ignore
                | None -> ()
        | _ -> ()


    /// Blanks and nulls are dropped rather than rejected: an empty list means "everything",
    /// and a caller padding the array with nulls meant the same thing.
    let private normalise (topics: (string | null) array | null) =
        match topics with
        | null -> [||]
        | given ->
            given
            |> Array.choose (fun topic ->
                match topic with
                | null -> None
                | t when t.Trim() = "" -> None
                | t -> Some(t.Trim()))
            |> Array.distinct

    let private label (topics: string array) =
        topics |> Array.map (fun t -> $"\"%s{t}\"") |> String.concat ", "

    let private matchesAny (topics: string array) (text: string) =
        topics
        |> Array.exists (fun topic -> text.Contains(topic, StringComparison.OrdinalIgnoreCase))

    let private dataTypes (assembly: Assembly) =
        assembly.GetTypes()
        |> Array.filter (fun t -> (t.IsPublic || t.IsNestedPublic) && isDataType t)
        |> Array.sortBy _.Name

    let private exposedFunctions (assembly: Assembly) isExposable toolName =
        assembly.GetTypes()
        |> Array.filter (fun t -> t.IsPublic || t.IsNestedPublic)
        |> Array.collect (fun t ->
            t.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.DeclaredOnly))
        |> Array.filter isExposable
        |> Array.sortBy toolName

    /// Every rejection a transition can make is a case of its own error union: the function
    /// returns `Result<_, that union>`, so the compiler guarantees the list is complete.
    /// The *absence* of a case is therefore the absence of a precondition - a fact that can
    /// be computed rather than written down, and so can never go stale.
    let private rejections (assembly: Assembly) isExposable toolName =
        exposedFunctions assembly isExposable toolName
        |> Array.choose (fun m ->
            let returned = m.ReturnType

            if
                returned.IsGenericType
                && returned.GetGenericTypeDefinition().Name.StartsWith "FSharpResult"
            then
                let error = returned.GetGenericArguments()[1]

                let error =
                    if
                        error.IsGenericType
                        && error.GetGenericTypeDefinition().Name.StartsWith "FSharpList"
                    then
                        error.GetGenericArguments()[0]
                    else
                        error

                if FSharpType.IsUnion(error, allMembers) then
                    Some(toolName m, error.Name, FSharpType.GetUnionCases(error, allMembers) |> Array.map _.Name)
                else
                    None
            else
                None)

    /// What a function actually branches on, read from the quotation the compiler embedded
    /// for `[<ReflectedDefinition>]`. This is the one source for facts that live only in a
    /// `match` expression: reflection cannot see them, doc comments cannot be trusted to
    /// state them, and no naming convention can encode them - `Order.collect` refuses a
    /// Prepared order with `NotAssigned`, naming the state it wanted, not the one it got.
    ///
    /// `None` means the declaring module carries no attribute, so nothing may be concluded
    /// from silence. The audit requires every exposed function to have one, which is what
    /// makes an absence provable rather than merely unobserved.
    let private branchesOn (domain: Assembly) (m: MethodInfo) =
        match Expr.TryGetReflectedDefinition m with
        | None -> None
        | Some body ->
            let tests = System.Collections.Generic.HashSet<Type * string>()
            let errors = System.Collections.Generic.HashSet<string>()

            let ours (t: Type) = t.Assembly = domain

            let rec walk (e: Expr) =
                match e with
                | Patterns.UnionCaseTest(_, case) when ours case.DeclaringType ->
                    tests.Add(case.DeclaringType, case.Name) |> ignore
                | Patterns.NewUnionCase(case, _) when
                    ours case.DeclaringType && case.DeclaringType.Name.EndsWith "Error"
                    ->
                    errors.Add $"%s{case.DeclaringType.Name}.%s{case.Name}" |> ignore
                | _ -> ()

                match e with
                | ExprShape.ShapeVar _ -> ()
                | ExprShape.ShapeLambda(_, inner) -> walk inner
                | ExprShape.ShapeCombination(_, args) -> args |> List.iter walk

            walk body
            Some(List.ofSeq tests, List.ofSeq errors)

    /// A `<summary>` is the rule in a sentence; anything longer belongs in `<remarks>`.
    /// Exposed so the audit can hold the line: a summary is shown on every match and is
    /// also the MCP tool's description, so an essay there is paid for by every reader.
    let overLongSummaries (assemblyPath: string) (limit: int) =
        DomainDocs.load assemblyPath
        |> Map.toArray
        |> Array.choose (fun (key, doc) ->
            if doc.Summary.Length > limit then
                Some(key, doc.Summary.Length)
            else
                None)
        |> Array.sortByDescending snd

    /// Whether the compiler embedded this function's body. Exposed so the audit can require
    /// it of every tool: `Domain.types` claims completeness over all of them, and that claim
    /// is only true while none is missing.
    let hasQuotation (m: MethodInfo) =
        (Expr.TryGetReflectedDefinition m).IsSome

    /// Every exposed function paired with what it branches on, or None where the compiler
    /// embedded nothing.
    let private behaviour (assembly: Assembly) isExposable toolName =
        exposedFunctions assembly isExposable toolName
        |> Array.map (fun m -> toolName m, branchesOn assembly m)

    /// An error union is a transition's *output*, not a state of the domain, and there are
    /// more of them than there are model types. Rendering each like a model type buries the
    /// handful of types someone actually came to read, so they get one line apiece.
    let private appendErrorLine (sb: StringBuilder) (t: Type) (summary: string) =
        let cases =
            if FSharpType.IsUnion(t, allMembers) then
                FSharpType.GetUnionCases(t, allMembers)
                |> Array.map (fun c ->
                    match c.GetFields() with
                    | [||] -> $"`%s{c.Name}`"
                    | fs ->
                        let parts =
                            fs
                            |> Array.map (fun f ->
                                if isPositional f.Name then
                                    typeName f.PropertyType
                                else
                                    $"%s{f.Name}: %s{typeName f.PropertyType}")

                        let shape = String.Join(" * ", parts)
                        $"`%s{c.Name} of %s{shape}`")
                |> String.concat " · "
            else
                ""

        sb.AppendLine($"- **%s{t.Name}** — %s{cases}") |> ignore

        if summary <> "" then
            sb.AppendLine($"  %s{summary}") |> ignore

    /// The domain's types: its states, and the invariants that constrain them. This is what
    /// answers "what does the domain allow", and it is the cheaper of the two by far.
    let types
        (assembly: Assembly)
        (assemblyPath: string)
        (isExposable: MethodInfo -> bool)
        (toolName: MethodInfo -> string)
        (topics: (string | null) array | null)
        =
        let docs = DomainDocs.load assemblyPath
        let sb = StringBuilder()
        let topics = normalise topics
        let all = dataTypes assembly

        if Array.isEmpty topics then
            sb
                .AppendLine("# The AgenticApp domain — types")
                .AppendLine()
                .AppendLine(
                    "Every type the domain documents, which is where its rules live. Pass `topics` to narrow this, "
                    + "or call Domain.functions for signatures."
                )
            |> ignore

            for source, group in all |> Array.filter (fun t -> summaryOf docs t <> "") |> Array.groupBy sourceOf do
                sb.AppendLine().AppendLine($"## %s{source}.fs") |> ignore

                for t in group do
                    appendType sb t (summaryOf docs t) ""
        else
            // Asked about directly (its name or one of its cases) versus dragged in because
            // its prose happens to mention the topic. Only the first earns its `<remarks>`:
            // otherwise one broad word pays for every essay in the domain.
            let scored =
                all
                |> Array.choose (fun ty ->
                    let direct =
                        matchesAny topics ty.Name
                        || (FSharpType.IsUnion(ty, allMembers)
                            && FSharpType.GetUnionCases(ty, allMembers)
                               |> Array.exists (fun c -> matchesAny topics c.Name))

                    if direct then
                        Some(ty, true)
                    elif matchesAny topics (summaryOf docs ty) || matchesAny topics (remarksOf docs ty) then
                        Some(ty, false)
                    else
                        None)

            let matching = scored |> Array.map fst

            sb.AppendLine($"# The AgenticApp domain — types matching %s{label topics}")
            |> ignore

            if matching.Length = 0 then
                sb
                    .AppendLine()
                    .AppendLine(
                        "No type matched. Call with no topics for all of them, and search by domain words rather "
                        + "than a type name."
                    )
                |> ignore
            else
                let isError (ty: Type) = ty.Name.EndsWith "Error"

                for ty, direct in scored |> Array.filter (fun (ty, _) -> not (isError ty)) do
                    appendType sb ty (summaryOf docs ty) (if direct then remarksOf docs ty else "")

                match scored |> Array.filter (fun (ty, _) -> isError ty) with
                | [||] -> ()
                | errorTypes ->
                    sb.AppendLine().AppendLine "### Errors these can return" |> ignore

                    for ty, direct in errorTypes do
                        appendErrorLine sb ty (if direct then summaryOf docs ty else "")

                let rejecting =
                    rejections assembly isExposable toolName
                    |> Array.collect (fun (fn, union, cases) ->
                        cases |> Array.filter (matchesAny topics) |> Array.map (fun c -> fn, union, c))

                sb.AppendLine().AppendLine "## Rejections that name this" |> ignore

                if rejecting.Length = 0 then
                    sb.AppendLine($"No error case is named for %s{label topics}.") |> ignore
                else
                    for fn, union, case in rejecting do
                        sb.AppendLine($"- `%s{fn}` rejects with `%s{union}.%s{case}`") |> ignore

                // What actually branches on the matched types, read from the compiled
                // quotations rather than from names or doc comments. This is what makes an
                // absence provable: if no function tests a case, nothing gates on it.
                let analysed = behaviour assembly isExposable toolName
                let complete = analysed |> Array.forall (fun (_, b) -> Option.isSome b)

                // Only states, never error unions. A `describe` function pattern-matching its
                // own error union is noise, and error cases dragged in by a substring match on
                // the topics brought a dozen such lines with them.
                let branching =
                    matching
                    |> Array.filter (fun ty -> not (ty.Name.EndsWith "Error"))
                    |> Array.collect (fun ty ->
                        analysed
                        |> Array.choose (fun (fn, b) ->
                            match b with
                            | None -> None
                            | Some(tests, errors) ->
                                // Only the cases the caller asked about. Listing every case of a
                                // matched type repeated the same four Membership cases across
                                // eleven functions and tripled the section for no new fact.
                                match
                                    tests
                                    |> List.filter (fun (t, c) -> t = ty && matchesAny topics c)
                                    |> List.map snd
                                with
                                | [] -> None
                                | cases -> Some(ty.Name, fn, cases, errors)))

                if branching.Length > 0 || complete then
                    sb.AppendLine().AppendLine "## What branches on this" |> ignore

                for owner, fn, cases, errors in branching |> Array.sortBy (fun (o, f, _, _) -> o, f) do
                    let tested = cases |> List.map (fun c -> $"`%s{owner}.%s{c}`") |> String.concat ", "

                    let mayReturn =
                        if errors.IsEmpty then
                            "cannot fail"
                        else
                            "may return "
                            + (errors |> List.sort |> List.map (fun e -> $"`%s{e}`") |> String.concat ", ")

                    sb.AppendLine($"- `%s{fn}` tests %s{tested} — %s{mayReturn}") |> ignore

                if complete then
                    let names =
                        matching
                        |> Array.filter (fun ty -> not (ty.Name.EndsWith "Error"))
                        |> Array.map _.Name
                        |> Array.distinct
                        |> String.concat ", "

                    sb
                        .AppendLine()
                        .AppendLine(
                            $"**Complete**: read from the compiled quotation of every one of the %d{analysed.Length} "
                            + $"exposed functions, so any function absent above does not test a %s{label topics} case of "
                            + $"%s{names} at all. No grep needed, and it cannot go stale."
                        )
                    |> ignore
                else
                    sb
                        .AppendLine()
                        .AppendLine(
                            "Some functions carry no quotation, so absence cannot be concluded here - add "
                            + "`[<ReflectedDefinition>]` to their module (the audit reports which)."
                        )
                    |> ignore

        sb.ToString()

    /// The domain's functions: signature, intent and parameters. Ask for this when you are
    /// about to call or write code against one, not to settle what the domain allows.
    let functions
        (assembly: Assembly)
        (assemblyPath: string)
        (isExposable: MethodInfo -> bool)
        (toolName: MethodInfo -> string)
        (topics: (string | null) array | null)
        =
        let docs = DomainDocs.load assemblyPath
        let sb = StringBuilder()
        let topics = normalise topics
        let all = exposedFunctions assembly isExposable toolName

        let matching =
            if Array.isEmpty topics then
                all
            else
                all
                |> Array.filter (fun m ->
                    matchesAny topics (toolName m)
                    || (match DomainDocs.docFor docs m with
                        | Some d -> matchesAny topics d.Summary
                        | None -> false))

        if Array.isEmpty topics then
            sb
                .AppendLine("# The AgenticApp domain — functions")
                .AppendLine()
                .AppendLine(
                    $"All %d{all.Length}, each callable as an MCP tool of the same name. Pass `topics` to narrow this."
                )
            |> ignore
        else
            sb.AppendLine($"# The AgenticApp domain — functions matching %s{label topics}")
            |> ignore

        if matching.Length = 0 then
            sb
                .AppendLine()
                .AppendLine("No function matched. Call with no topics for all of them, or Domain.types for the rules.")
            |> ignore
        else
            for m in matching do
                appendFunction sb (toolName m) m (DomainDocs.docFor docs m) (matchesAny topics (toolName m))

        sb.ToString()

/// Carries the reflection entry point as an instance method so the MCP schema generator
/// picks up the real parameter name. The doc text is set on the tool rather than read
/// from XML: this lives in the server, not the Domain, so Domain.xml never describes it.
type DomainExplainer
    (assembly: Assembly, assemblyPath: string, isExposable: MethodInfo -> bool, toolName: MethodInfo -> string) =

    /// `topics` is optional so calling with no arguments is legal: the binder treats a
    /// parameter without a default as required and throws before the tool ever runs.
    member _.Types
        ([<Optional; DefaultParameterValue(null: (string | null) array | null)>] topics: (string | null) array | null)
        : string =
        DomainKnowledge.types assembly assemblyPath isExposable toolName topics

    member _.Functions
        ([<Optional; DefaultParameterValue(null: (string | null) array | null)>] topics: (string | null) array | null)
        : string =
        DomainKnowledge.functions assembly assemblyPath isExposable toolName topics
