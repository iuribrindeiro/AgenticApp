namespace AgenticApp.Mcp

open AgenticApp.Domain

open System
open System.Reflection
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.FSharp.Reflection
open ModelContextProtocol

/// Serialization for value objects.
///
/// A value object has a private constructor, so a deserializer must not be able to
/// build one directly - that would forge a value past validation and void the type's
/// guarantee. These converters route every read through the type's `Make`, which makes
/// deserialization *be* validation.
///
/// Discovery is by interface, not by method name: a type that does not implement
/// IValueObject / IPartialValueObject fails to compile rather than silently dropping
/// out of serialization and schema generation.
module ValueObjectJson =

    /// The generic interface behind a marker, e.g. IValueObject<string> behind IValueObjectMarker.
    let private behind (marker: Type) (t: Type) =
        t.GetInterfaces()
        |> Array.tryFind (fun i -> i.IsGenericType && marker.IsAssignableFrom i)

    let wireInterface (t: Type) = behind typeof<IValueObjectMarker> t
    let partialInterface (t: Type) = behind typeof<IPartialMarker> t
    let totalInterface (t: Type) = behind typeof<ITotalMarker> t

    let isValueObject (t: Type) =
        (wireInterface t).IsSome
        && ((partialInterface t).IsSome || (totalInterface t).IsSome)

    /// The static member behind an explicit interface implementation.
    let staticImpl (t: Type) (iface: Type) (name: string) =
        let map = t.GetInterfaceMap iface
        let i = map.InterfaceMethods |> Array.findIndex (fun m -> m.Name = name)
        map.TargetMethods.[i]

    /// Reflection returns nullable handles; a missing one is a bug in this file, not a
    /// runtime condition, so fail loudly rather than propagating null.
    let private propertyNamed (t: Type) (name: string) : PropertyInfo =
        match t.GetProperty name with
        | null -> failwith $"%s{t.Name} has no property '%s{name}'"
        | p -> p

    /// Reads a Result's discriminant and payload without unsafe downcasts.
    let tagOf (rt: Type) (result: obj) =
        match (propertyNamed rt "Tag").GetValue result with
        | :? int as tag -> tag
        | _ -> failwith "Result.Tag was not an int"

    let fieldOf (rt: Type) (result: obj) (name: string) = (propertyNamed rt name).GetValue result

    /// A type the wire carries directly, with no converter or transform.
    let rec isWireShape (t: Type) =
        let t =
            match Nullable.GetUnderlyingType t with
            | null -> t
            | u -> u

        if t.IsArray then
            match t.GetElementType() with
            | null -> false
            | e -> isWireShape e
        else
            t.IsPrimitive
            || t.IsEnum
            || t = typeof<string>
            || t = typeof<decimal>
            || t = typeof<Guid>
            || t = typeof<DateTime>
            || t = typeof<DateTimeOffset>
            || t = typeof<TimeSpan>

    let private flags = BindingFlags.Public ||| BindingFlags.NonPublic

    let isRecord (t: Type) = FSharpType.IsRecord(t, flags)
    let recordFields (t: Type) = FSharpType.GetRecordFields(t, flags)
    let unionCases (t: Type) = FSharpType.GetUnionCases(t, flags)

    /// Any union, including value objects and containers - unlike `isUnion`, which
    /// excludes them because they get their own schema treatment.
    let isUnionType (t: Type) = FSharpType.IsUnion(t, flags)

    /// F# list and option are unions, but they are *containers*: their cases are
    /// self-referential (Cons carries another list), so they must be judged by their
    /// element type or the walk never terminates.
    let private elementOf (t: Type) =
        if t.IsArray then
            match t.GetElementType() with
            | null -> None
            | e -> Some e
        elif t.IsGenericType then
            let d = t.GetGenericTypeDefinition()

            if d = typedefof<_ list> || d = typedefof<_ option> || d = typedefof<seq<_>> then
                Some(t.GetGenericArguments().[0])
            else
                None
        else
            None

    /// The element type of a container (array, F# list, option, seq), if it is one.
    let containerElementOf (t: Type) = elementOf t

    let isUnion (t: Type) =
        FSharpType.IsUnion(t, flags) && not (isValueObject t) && (elementOf t).IsNone

    /// Can a deserializer produce a value of this type that the domain would have
    /// rejected? Value objects validate in `Make`; a record of safe fields has no
    /// illegal combination; a union's every case is legal by construction.
    let isForgeable (t: Type) =
        let rec go (seen: Set<string>) (t: Type) =
            let key = t.AssemblyQualifiedName |> Option.ofObj |> Option.defaultValue t.Name

            if seen.Contains key then
                false
            else
                let seen = seen.Add key

                if isWireShape t || isValueObject t then
                    false
                else
                    match elementOf t with
                    | Some e -> go seen e
                    | None ->
                        if isRecord t then
                            recordFields t |> Array.exists (fun f -> go seen f.PropertyType)
                        elif isUnion t then
                            false
                        else
                            true

        go Set.empty t

    /// Can this type be given a schema a model can actually fill? `FSharp.SystemTextJson`
    /// claims records and unions, so the exporter emits a bare `true` (match anything)
    /// for them - each shape needs a transform in DomainTools before it is usable.
    let isSchematisable (t: Type) =
        let rec go (seen: Set<string>) (t: Type) =
            let key = t.AssemblyQualifiedName |> Option.ofObj |> Option.defaultValue t.Name

            if seen.Contains key then
                true
            else
                let seen = seen.Add key

                if isWireShape t || isValueObject t then
                    true
                else
                    match elementOf t with
                    | Some e -> go seen e
                    | None ->
                        if isRecord t then
                            recordFields t |> Array.forall (fun f -> go seen f.PropertyType)
                        elif isUnion t then
                            FSharpType.GetUnionCases(t, flags)
                            |> Array.forall (fun c -> c.GetFields() |> Array.forall (fun f -> go seen f.PropertyType))
                        else
                            false

        go Set.empty t

    /// A parameter is usable when it is both unforgeable and describable.
    let isSafeParameter (t: Type) =
        not (isForgeable t) && isSchematisable t

    /// The JSON shape a value object maps to: whatever its `Make` accepts.
    let wireTypeOf (t: Type) =
        match partialInterface t, totalInterface t with
        | Some i, _ -> Some(i.GetGenericArguments().[1])
        | _, Some i -> Some(i.GetGenericArguments().[1])
        | None, None -> None

type ValueObjectConverter<'T>(make: MethodInfo, explain: MethodInfo option, wire: MethodInfo) =
    inherit JsonConverter<'T>()

    // Without this, System.Text.Json short-circuits a JSON null for reference types
    // and never calls Read - so null would bypass validation entirely.
    override _.HandleNull = true

    override _.Read(reader, _, options) =
        let wireType = make.GetParameters().[0].ParameterType
        let raw = JsonSerializer.Deserialize(&reader, wireType, options)

        match make.Invoke(null, [| raw |]) with
        | null -> failwith $"%s{typeof<'T>.Name}.Make returned null"
        | result ->
            let rt = result.GetType()

            if rt.IsGenericType && rt.GetGenericTypeDefinition() = typedefof<Result<_, _>> then
                match ValueObjectJson.tagOf rt result with
                | 0 ->
                    match ValueObjectJson.fieldOf rt result "ResultValue" with
                    | :? 'T as ok -> ok
                    | _ -> failwith $"%s{typeof<'T>.Name}.Make produced an unexpected Ok payload"
                | _ ->
                    let err = ValueObjectJson.fieldOf rt result "ErrorValue"

                    let reason =
                        match explain with
                        | Some e ->
                            match e.Invoke(null, [| err |]) with
                            | :? string as text -> text
                            | _ -> "is invalid"
                        | None -> "is invalid"

                    // McpException surfaces its message to the client; a JsonException
                    // raised during argument binding is reported only as a generic
                    // "An error occurred invoking '<tool>'".
                    raise (McpException($"%s{typeof<'T>.Name} %s{reason}"))
            else
                // A total constructor - nothing to fail.
                match result with
                | :? 'T as total -> total
                | _ -> failwith $"%s{typeof<'T>.Name}.Make produced an unexpected value"

    override _.Write(writer, value, options) =
        JsonSerializer.Serialize(writer, wire.Invoke(value, [||]), wire.ReturnType, options)

/// Applies to every value object in the assembly, including ones added later.
type ValueObjectConverterFactory() =
    inherit JsonConverterFactory()

    override _.CanConvert(t) = ValueObjectJson.isValueObject t

    override _.CreateConverter(t, _) =
        let wireIface = (ValueObjectJson.wireInterface t).Value
        let wire = ValueObjectJson.staticImpl t wireIface "get_Wire"

        let make, explain =
            match ValueObjectJson.partialInterface t with
            | Some i -> ValueObjectJson.staticImpl t i "Make", Some(ValueObjectJson.staticImpl t i "Explain")
            | None ->
                let i = (ValueObjectJson.totalInterface t).Value
                ValueObjectJson.staticImpl t i "Make", None

        let converterType = typedefof<ValueObjectConverter<_>>.MakeGenericType t

        match Activator.CreateInstance(converterType, [| box make; box explain; box wire |]) with
        | :? JsonConverter as converter -> converter
        | _ -> failwith $"could not construct a converter for %s{t.Name}"
