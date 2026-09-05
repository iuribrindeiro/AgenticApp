namespace AgenticApp.Domain

/// Accumulates every error instead of stopping at the first.
/// Generic in 'E: each aggregate supplies its own error type.
/// The alias is public because it appears in public signatures; the combinators
/// below are internal - adapters pattern-match the Result, they never compose it.
type Validation<'T, 'E> = Result<'T, 'E list>

module internal Validation =
    /// Lift a value from a total constructor (one that cannot fail).
    let ok (v: 'T) : Validation<'T, 'E> = Ok v

    /// Lift a partial constructor's Result, tagging its error as one of the aggregate's cases.
    let field (wrap: 'E1 -> 'E2) (r: Result<'T, 'E1>) : Validation<'T, 'E2> =
        r |> Result.mapError (fun e -> [ wrap e ])

    let apply (f: Validation<'a -> 'b, 'E>) (x: Validation<'a, 'E>) : Validation<'b, 'E> =
        match f, x with
        | Ok f, Ok x -> Ok(f x)
        | Error e1, Error e2 -> Error(e1 @ e2)
        | Error e, _
        | _, Error e -> Error e

    let map (f: 'a -> 'b) (x: Validation<'a, 'E>) : Validation<'b, 'E> = apply (Ok f) x

    /// Validate every element of a list, accumulating errors from all of them.
    let traverse (f: 'a -> Validation<'b, 'E>) (items: 'a list) : Validation<'b list, 'E> =
        let folder item acc =
            match f item, acc with
            | Ok v, Ok vs -> Ok(v :: vs)
            | Error e, Ok _ -> Error e
            | Ok _, Error es -> Error es
            | Error e, Error es -> Error(e @ es)

        List.foldBack folder items (Ok [])

    /// Check a cross-field invariant after all fields are individually valid.
    let check (predicate: 'T -> bool) (onFail: 'T -> 'E) (v: Validation<'T, 'E>) : Validation<'T, 'E> =
        match v with
        | Ok value when not (predicate value) -> Error [ onFail value ]
        | other -> other

[<AutoOpen>]
module internal ValidationOperators =
    let inline (<!>) f x = Validation.map f x
    let inline (<*>) f x = Validation.apply f x
