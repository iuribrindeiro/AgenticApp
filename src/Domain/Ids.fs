namespace AgenticApp.Domain

open System

/// Identity value objects. Separated from Primitives.fs because they are
/// domain nouns shared across models, not general-purpose primitives.
[<AutoOpen>]
module Ids =

    // ------------------------------------------------------------ StoreId

    [<RequireQualifiedAccess>]
    type StoreIdError =
        | Missing
        | Unrepresentable of Guid

    /// A store's identity. Guid.Empty is the framework default, never a real id.
    type StoreId =
        private
        | StoreId of Guid

        interface IValueObject<Guid> with
            member this.Wire = let (StoreId v) = this in v

        interface IPartialValueObject<StoreId, Nullable<Guid>, StoreIdError> with
            /// Boundary: takes the outside world's `Guid?` directly.
            static member Make(value: Nullable<Guid>) =
                if not value.HasValue then
                    Error StoreIdError.Missing
                elif value.Value = Guid.Empty then
                    Error(StoreIdError.Unrepresentable value.Value)
                else
                    Ok(StoreId value.Value)

            static member Explain(e) =
                match e with
                | StoreIdError.Missing -> "is required"
                | StoreIdError.Unrepresentable v -> $"holds an unrepresentable id (%O{v})"

    module StoreId =
        /// <summary>Validates a store id. Returns Error if absent or if it is the empty guid.</summary>
        /// <param name="value">The raw guid. Must not be empty.</param>
        let create (value: Nullable<Guid>) : Result<StoreId, StoreIdError> = make<StoreId, _, _> value

        /// <summary>Unwraps a validated store id.</summary>
        /// <param name="v">The validated store id.</param>
        let value (v: StoreId) : Guid = (v :> IValueObject<Guid>).Wire
        /// <summary>Renders a store-id failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describe (e: StoreIdError) = explain<StoreId, _, _> e

    // ------------------------------------------------------ DeliverymanId

    [<RequireQualifiedAccess>]
    type DeliverymanIdError =
        | Missing
        | NotPositive of int64

    /// A deliveryman's identity. Ids are assigned by the upstream system and
    /// are always positive, so 0 and negatives are corruption, not data.
    type DeliverymanId =
        private
        | DeliverymanId of int64

        interface IValueObject<int64> with
            member this.Wire = let (DeliverymanId v) = this in v

        interface IPartialValueObject<DeliverymanId, Nullable<int64>, DeliverymanIdError> with
            /// Boundary: takes the outside world's `long?` directly.
            static member Make(value: Nullable<int64>) =
                if not value.HasValue then
                    Error DeliverymanIdError.Missing
                elif value.Value <= 0L then
                    Error(DeliverymanIdError.NotPositive value.Value)
                else
                    Ok(DeliverymanId value.Value)

            static member Explain(e) =
                match e with
                | DeliverymanIdError.Missing -> "is required"
                | DeliverymanIdError.NotPositive v -> $"is not a positive id (%d{v})"

    module DeliverymanId =
        /// <summary>Validates a deliveryman id. Returns Error if absent or not positive.</summary>
        /// <param name="value">The raw id. Must be a positive number.</param>
        let create (value: Nullable<int64>) : Result<DeliverymanId, DeliverymanIdError> =
            make<DeliverymanId, _, _> value

        /// <summary>Unwraps a validated deliveryman id.</summary>
        /// <param name="v">The validated deliveryman id.</param>
        let value (v: DeliverymanId) : int64 = (v :> IValueObject<int64>).Wire
        /// <summary>Renders a deliveryman-id failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describe (e: DeliverymanIdError) = explain<DeliverymanId, _, _> e

    // ----------------------------------------------------- DeliverymanIds

    /// The "deliverymen are distinct" rule lives here, in a type, rather than
    /// procedurally in Store.create. That is what lets Store be a plain public
    /// record: every invariant it has is carried by one of its fields.
    [<RequireQualifiedAccess>]
    type DeliverymanIdsError =
        | InvalidAt of index: int * error: DeliverymanIdError
        | Duplicates of int64 list

    /// A distinct set of deliveryman ids. May be empty; may not repeat.
    type DeliverymanIds =
        private
        | DeliverymanIds of DeliverymanId list

        interface IValueObject<DeliverymanId list> with
            member this.Wire = let (DeliverymanIds v) = this in v

        interface IPartialValueObject<DeliverymanIds, (Nullable<int64> array | null), DeliverymanIdsError list> with
            /// Boundary: a null array is an empty set - having no deliverymen is legal.
            static member Make(value: Nullable<int64> array | null) =
                let incoming =
                    match value with
                    | null -> []
                    | ids -> List.ofArray ids

                let parsed =
                    incoming
                    |> List.mapi (fun i raw ->
                        make<DeliverymanId, _, _> raw
                        |> Result.mapError (fun e -> [ DeliverymanIdsError.InvalidAt(i, e) ]))

                let errors =
                    parsed
                    |> List.collect (function
                        | Error e -> e
                        | Ok _ -> [])

                if not errors.IsEmpty then
                    Error errors
                else
                    let ok =
                        parsed
                        |> List.choose (function
                            | Ok v -> Some v
                            | Error _ -> None)

                    let raws = ok |> List.map DeliverymanId.value

                    let duplicates =
                        raws |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst

                    if duplicates.IsEmpty then
                        Ok(DeliverymanIds ok)
                    else
                        Error [ DeliverymanIdsError.Duplicates duplicates ]

            static member Explain(errors) =
                errors
                |> List.map (fun e ->
                    match e with
                    | DeliverymanIdsError.InvalidAt(i, e) -> $"[%d{i}] %s{DeliverymanId.describe e}"
                    | DeliverymanIdsError.Duplicates ids ->
                        let listed = ids |> List.map string |> String.concat ", "
                        $"contains duplicates (%s{listed})")
                |> String.concat "; "

    module DeliverymanIds =
        /// <summary>Validates a set of deliveryman ids. Each must be positive and distinct; the set may be empty. Returns Error naming every bad entry.</summary>
        /// <param name="value">The raw ids. Each must be positive, with no repeats; may be empty or null.</param>
        let create (value: Nullable<int64> array | null) : Result<DeliverymanIds, DeliverymanIdsError list> =
            make<DeliverymanIds, _, _> value

        /// <summary>Unwraps the set as a list of validated deliveryman ids.</summary>
        /// <param name="v">The validated set.</param>
        let value (v: DeliverymanIds) : DeliverymanId list =
            (v :> IValueObject<DeliverymanId list>).Wire

        /// <summary>Renders a deliveryman-id-set failure as a sentence fragment.</summary>
        /// <param name="e">The failures to describe.</param>
        let describe (e: DeliverymanIdsError list) = explain<DeliverymanIds, _, _> e

        /// <summary>Unwraps the set as plain numbers, for the wire or for persistence.</summary>
        /// <param name="v">The validated set.</param>
        let toArray (v: DeliverymanIds) =
            value v |> List.map DeliverymanId.value |> Array.ofList

        /// <summary>Whether the set already holds this deliveryman.</summary>
        /// <param name="d">The deliveryman to look for.</param>
        /// <param name="v">The set to search.</param>
        let contains (d: DeliverymanId) (v: DeliverymanIds) = value v |> List.contains d

        /// <summary>Adds a deliveryman to the set. None when it is already present - this is where distinctness is enforced.</summary>
        /// <param name="d">The deliveryman to add.</param>
        /// <param name="v">The set to add to.</param>
        let tryAdd (d: DeliverymanId) (v: DeliverymanIds) : DeliverymanIds option =
            if contains d v then
                None
            else
                Some(DeliverymanIds(value v @ [ d ]))

        /// <summary>Removes a deliveryman from the set. None when it is not present.</summary>
        /// <param name="d">The deliveryman to remove.</param>
        /// <param name="v">The set to remove from.</param>
        let tryRemove (d: DeliverymanId) (v: DeliverymanIds) : DeliverymanIds option =
            if contains d v then
                Some(DeliverymanIds(value v |> List.filter (fun x -> x <> d)))
            else
                None
