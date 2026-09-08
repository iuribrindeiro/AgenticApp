namespace AgenticApp.Mcp

open AgenticApp.Domain

open System
open System.Reflection
open System.Text.Json
open System.Text.Json.Schema
open System.Text.Json.Serialization
open System.Text.Json.Serialization.Metadata
open Microsoft.Extensions.AI

open System.Runtime.CompilerServices

open ModelContextProtocol.Server

/// Builds the MCP tools this assembly exposes.
///
/// Assembly scanning via `WithToolsFromAssembly` cannot be used, because it accepts
/// serializer options but not schema options - and value-object parameters need both:
/// the converter to validate them, and a schema transform to give them a type.
module DomainTools =

    /// The converter factory must come first: it claims value objects before the
    /// general F# union converter can flatten them without validating.
    let serializerOptions () =
        let o = JsonSerializerOptions(TypeInfoResolver = DefaultJsonTypeInfoResolver())
        o.Converters.Add(ValueObjectConverterFactory())
        o.Converters.Add(JsonFSharpConverter())
        o

    /// A value object's parameter is typed as whatever its `create` accepts, so the
    /// model sees `string` rather than an opaque blob.
    let private schemaOptions () =
        let plain = JsonSerializerOptions(TypeInfoResolver = DefaultJsonTypeInfoResolver())

        // FSharp.SystemTextJson claims records, unions and F# lists, so the schema
        // exporter cannot see into them and emits a bare `true` (match anything).
        // These describe each shape so a model can actually construct a value.
        let rec schemaFor (t: Type) : Nodes.JsonNode =
            match ValueObjectJson.wireTypeOf t with
            | Some wire -> JsonSchemaExporter.GetJsonSchemaAsNode(plain, wire)
            | None ->

                match ValueObjectJson.containerElementOf t with
                | Some element ->
                    let arr = Nodes.JsonObject()
                    arr["type"] <- Nodes.JsonValue.Create "array"
                    arr["items"] <- schemaFor element
                    arr :> Nodes.JsonNode
                | None ->

                    if ValueObjectJson.isUnion t then
                        // The encoding is {"Case": "Name", "Fields": [...]}, so pin Case per branch.
                        let cases = Nodes.JsonArray()

                        for case in ValueObjectJson.unionCases t do
                            let props = Nodes.JsonObject()
                            let name = Nodes.JsonObject()
                            name["type"] <- Nodes.JsonValue.Create "string"
                            name["const"] <- Nodes.JsonValue.Create case.Name
                            props["Case"] <- name

                            let required = Nodes.JsonArray()
                            required.Add(Nodes.JsonValue.Create "Case")

                            if case.GetFields().Length > 0 then
                                let fields = Nodes.JsonArray()

                                for f in case.GetFields() do
                                    fields.Add(schemaFor f.PropertyType)

                                let fieldsSchema = Nodes.JsonObject()
                                fieldsSchema["type"] <- Nodes.JsonValue.Create "array"
                                fieldsSchema["prefixItems"] <- fields
                                props["Fields"] <- fieldsSchema
                                required.Add(Nodes.JsonValue.Create "Fields")

                            let caseSchema = Nodes.JsonObject()
                            caseSchema["type"] <- Nodes.JsonValue.Create "object"
                            caseSchema["properties"] <- props
                            caseSchema["required"] <- required
                            cases.Add caseSchema

                        let obj = Nodes.JsonObject()
                        obj["oneOf"] <- cases
                        obj :> Nodes.JsonNode

                    elif ValueObjectJson.isRecord t then
                        let props = Nodes.JsonObject()
                        let required = Nodes.JsonArray()

                        for f in ValueObjectJson.recordFields t do
                            props[f.Name] <- schemaFor f.PropertyType
                            required.Add(Nodes.JsonValue.Create f.Name)

                        let obj = Nodes.JsonObject()
                        obj["type"] <- Nodes.JsonValue.Create "object"
                        obj["properties"] <- props
                        obj["required"] <- required
                        obj :> Nodes.JsonNode

                    else
                        JsonSchemaExporter.GetJsonSchemaAsNode(plain, t)

        AIJsonSchemaCreateOptions(
            TransformSchemaNode =
                Func<AIJsonSchemaCreateContext, Nodes.JsonNode, Nodes.JsonNode>(fun ctx node ->
                    let t = ctx.TypeInfo.Type

                    // Only intervene where the exporter cannot describe the type itself.
                    if
                        (ValueObjectJson.wireTypeOf t).IsSome
                        || ValueObjectJson.isUnion t
                        || (ValueObjectJson.containerElementOf t).IsSome
                        || ValueObjectJson.isRecord t
                    then
                        let described = schemaFor t

                        // Keep anything the caller already put on the node (the description).
                        match described, node with
                        | (:? Nodes.JsonObject as target), (:? Nodes.JsonObject as original) ->
                            for prop in original do
                                if not (target.ContainsKey prop.Key) then
                                    match prop.Value with
                                    | null -> ()
                                    | existing -> target[prop.Key] <- existing.DeepClone()

                            target :> Nodes.JsonNode
                        | described, _ -> described
                    else
                        node)
        )

    /// The name a function is known by, matching how it is written in code:
    /// `AgenticApp.Domain.StoreModel.StoreModule.rename` becomes `Store.rename`.
    let toolName (m: MethodInfo) =
        let owner =
            match m.DeclaringType with
            | null -> ""
            | t ->
                let last = t.Name

                if last.EndsWith "Module" && last.Length > 6 then
                    last.Substring(0, last.Length - 6)
                else
                    last

        $"%s{owner}.%s{m.Name}"

    /// A function is exposed when the binder cannot produce an illegal argument for it
    /// and every parameter can be given a schema. Registration is by convention, so there
    /// is no attribute to forget - and none to add.
    let isExposable (m: MethodInfo) =
        not m.IsSpecialName
        && isNull (box (m.GetCustomAttribute<CompilerGeneratedAttribute>()))
        && not (m.Name.Contains ".")
        && not m.IsGenericMethod
        && (match m.DeclaringType with
            | null -> false
            | t -> not (ValueObjectJson.isUnionType t && m.Name.StartsWith "New"))
        && m.GetParameters().Length > 0
        && m.GetParameters()
           |> Array.forall (fun p -> ValueObjectJson.isSafeParameter p.ParameterType)

    /// Writes each parameter's documented meaning onto the finished schema. The schema
    /// generator does not surface ParameterInfo, so this cannot be done during
    /// generation; the property is settable, so it is done here instead.
    let private withParameterDocs (parameters: Map<string, string>) (tool: McpServerTool) =
        match parameters with
        | parameters when not parameters.IsEmpty ->
            match Nodes.JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) with
            | :? Nodes.JsonObject as root ->
                match root["properties"] with
                | :? Nodes.JsonObject as props ->
                    for name, text in Map.toSeq parameters do
                        match props[name] with
                        | :? Nodes.JsonObject as prop -> prop["description"] <- Nodes.JsonValue.Create text
                        | _ -> ()

                    tool.ProtocolTool.InputSchema <- JsonDocument.Parse(root.ToJsonString()).RootElement.Clone()
                | _ -> ()
            | _ -> ()
        | _ -> ()

        tool

    /// Answers questions about the domain without anyone reading source. Two tools rather
    /// than one because the jobs differ in cost: settling what the domain *allows* needs
    /// only the types, while signatures are several times larger and are wanted only when
    /// you are about to call or write something.
    let private knowledgeTools (assembly: Assembly) (assemblyPath: string) : McpServerTool list =
        let explainer = DomainExplainer(assembly, assemblyPath, isExposable, toolName)

        let build name description paramDoc =
            match explainer.GetType().GetMethod(name: string) with
            | null -> []
            | m ->
                let createOpts =
                    McpServerToolCreateOptions(
                        Name = $"Domain.%s{name.ToLowerInvariant()}",
                        Title = $"Domain.%s{name.ToLowerInvariant()}",
                        Description = description,
                        ReadOnly = true,
                        OpenWorld = false,
                        SerializerOptions = serializerOptions (),
                        SchemaCreateOptions = schemaOptions ()
                    )

                [ McpServerTool.Create(m, box explainer, createOpts)
                  |> withParameterDocs (Map.ofList [ "topics", paramDoc ]) ]

        build
            "Types"
            ("What the domain allows: every documented type, its states, and the invariants that constrain them, "
             + "read from the same doc comments as every other tool here. Start here for any question about "
             + "behaviour - prefer it over reading src/Domain. Call with no topics for the whole domain.")
            ("Domain words to narrow the answer to, e.g. [\"online\"] or [\"blocked\", \"assign\"]. Ask about "
             + "several at once rather than calling again - the fixed cost is paid per call. Omit for every "
             + "documented type.")
        @ build
            "Functions"
            ("Signature, intent and parameters for the domain's public functions, each of which is also an MCP tool "
             + "of the same name. Ask for this when you are about to call one or write code against it; for what the "
             + "domain allows, use Domain.types instead - it is much smaller.")
            ("Domain words to narrow the answer to, e.g. [\"invite\"] or [\"online\", \"order\"]. Ask about "
             + "several at once rather than calling again. Omit for every function.")

    /// Every exposable public function in the given Domain assembly.
    ///
    /// The assembly is a parameter rather than `typeof<_>.Assembly` so the server can
    /// serve a hot-reloaded copy living in its own AssemblyLoadContext.
    let forAssembly (assembly: Assembly) (assemblyPath: string) : McpServerTool list =
        let opts = serializerOptions ()
        let docs = DomainDocs.load assemblyPath

        knowledgeTools assembly assemblyPath
        @ (assembly.GetTypes()
           |> Seq.filter (fun t -> t.IsPublic || t.IsNestedPublic)
           |> Seq.collect (fun t ->
               t.GetMethods(
                   BindingFlags.Public
                   ||| BindingFlags.Static
                   ||| BindingFlags.DeclaredOnly
               ))
           |> Seq.filter isExposable
           |> Seq.map (fun m ->
               let createOpts =
                   McpServerToolCreateOptions(
                       Name = toolName m,
                       Title = toolName m,
                       Description =
                           (DomainDocs.docFor docs m
                            |> Option.map _.Summary
                            |> Option.defaultValue ""),
                       ReadOnly = true,
                       OpenWorld = false,
                       SerializerOptions = opts,
                       SchemaCreateOptions = schemaOptions ()
                   )

               McpServerTool.Create(m, (null: obj | null), createOpts)
               |> withParameterDocs (
                   DomainDocs.docFor docs m
                   |> Option.map _.Parameters
                   |> Option.defaultValue Map.empty
               ))
           |> List.ofSeq)

    /// The statically referenced Domain - used by the audit, which has no reason to
    /// reload anything.
    let all () : McpServerTool list =
        let assembly = typeof<StoreModel.Store>.Assembly
        forAssembly assembly assembly.Location
