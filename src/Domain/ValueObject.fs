namespace AgenticApp.Domain

/// <summary>The contract every value object implements.</summary>
/// <remarks>
/// This replaces looking up `create`/`value`/`describe` by name: a value object that does not implement
/// these fails to compile, rather than silently dropping out of serialization and MCP schema generation.
/// </remarks>
[<AutoOpen>]
module ValueObject =

    /// <summary>Non-generic markers.</summary>
    /// <remarks>
    /// The generic interfaces below carry the self-referential constraints that make `typedefof&lt;_&gt;`
    /// unusable, so detection keys off these - still compile-checked, since renaming a marker breaks every
    /// implementer.
    /// </remarks>
    type IValueObjectMarker =
        interface
        end

    type IPartialMarker =
        interface
        end

    type ITotalMarker =
        interface
        end

    /// Instance side: the underlying wire value. Its generic argument is also how the
    /// serializer learns what JSON shape the type maps to.
    type IValueObject<'Wire> =
        inherit IValueObjectMarker
        abstract Wire: 'Wire

    /// <summary>Static side for a value object that can reject its input.</summary>
    /// <remarks>
    /// 'In is the *inbound* shape - what the outside world sends - and is generally wider than
    /// IValueObject's 'Wire: `string | null` in, `string` out.
    /// </remarks>
    type IPartialValueObject<'Self, 'In, 'Err when 'Self :> IPartialValueObject<'Self, 'In, 'Err>> =
        inherit IPartialMarker
        static abstract Make: 'In -> Result<'Self, 'Err>
        static abstract Explain: 'Err -> string

    /// Static side for a value object that accepts every input (absence is legal).
    type ITotalValueObject<'Self, 'In when 'Self :> ITotalValueObject<'Self, 'In>> =
        inherit ITotalMarker
        static abstract Make: 'In -> 'Self

    /// Static abstract members are explicit implementations, so they are reachable only
    /// through a constrained call. These are that call.
    let inline make< 'Self, 'In, 'Err when 'Self :> IPartialValueObject<'Self, 'In, 'Err> >
        (input: 'In)
        : Result<'Self, 'Err> =
        'Self.Make input

    let inline explain< 'Self, 'In, 'Err when 'Self :> IPartialValueObject<'Self, 'In, 'Err> >
        (error: 'Err)
        : string =
        'Self.Explain error

    let inline makeTotal< 'Self, 'In when 'Self :> ITotalValueObject<'Self, 'In> > (input: 'In) : 'Self =
        'Self.Make input
