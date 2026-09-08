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

    [<ReflectedDefinition>]
    module StoreId =
        /// <summary>Validates a store id. Returns Error if absent or if it is the empty guid.</summary>
        /// <param name="value">The raw guid. Must not be empty.</param>
        let create value = make<StoreId, _, _> value

        /// <summary>Unwraps a validated store id.</summary>
        /// <param name="storeId">The validated store id.</param>
        let value (storeId: StoreId) = (storeId :> IValueObject<Guid>).Wire
        /// <summary>Renders a store-id failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describe error = explain<StoreId, _, _> error

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

    [<ReflectedDefinition>]
    module DeliverymanId =
        /// <summary>Validates a deliveryman id. Returns Error if absent or not positive.</summary>
        /// <param name="value">The raw id. Must be a positive number.</param>
        let create value = make<DeliverymanId, _, _> value

        /// <summary>Unwraps a validated deliveryman id.</summary>
        /// <param name="deliverymanId">The validated deliveryman id.</param>
        let value (deliverymanId: DeliverymanId) =
            (deliverymanId :> IValueObject<int64>).Wire

        /// <summary>Renders a deliveryman-id failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describe error = explain<DeliverymanId, _, _> error

    // ----------------------------------------------------- DeliverymanIds

    /// <summary>The "deliverymen are distinct" rule lives here, in a type, rather than procedurally in Store.create.</summary>
    /// <remarks>
    /// That is what lets Store be a plain public record: every invariant it has is carried by one of its
    /// fields.
    /// </remarks>
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
                        raws
                        |> List.countBy id
                        |> List.filter (fun (_, n) -> n > 1)
                        |> List.map fst

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

    [<ReflectedDefinition>]
    module DeliverymanIds =
        /// <summary>Validates a set of deliveryman ids. Each must be positive and distinct; the set may be empty. Returns Error naming every bad entry.</summary>
        /// <param name="value">The raw ids. Each must be positive, with no repeats; may be empty or null.</param>
        let create value = make<DeliverymanIds, _, _> value

        /// <summary>Unwraps the set as a list of validated deliveryman ids.</summary>
        /// <param name="deliverymanIds">The validated set.</param>
        let value (deliverymanIds: DeliverymanIds) =
            (deliverymanIds :> IValueObject<DeliverymanId list>).Wire

        /// <summary>Renders a deliveryman-id-set failure as a sentence fragment.</summary>
        /// <param name="errors">The failures to describe.</param>
        let describe errors = explain<DeliverymanIds, _, _> errors

        /// <summary>Unwraps the set as plain numbers, for the wire or for persistence.</summary>
        /// <param name="deliverymanIds">The validated set.</param>
        let toArray deliverymanIds =
            value deliverymanIds
            |> List.map DeliverymanId.value
            |> Array.ofList

        /// <summary>Whether the set already holds this deliveryman.</summary>
        /// <param name="deliverymanId">The deliveryman to look for.</param>
        /// <param name="deliverymanIds">The set to search.</param>
        let contains deliverymanId deliverymanIds =
            value deliverymanIds |> List.contains deliverymanId

        /// <summary>Adds a deliveryman to the set. None when it is already present - this is where distinctness is enforced.</summary>
        /// <param name="deliverymanId">The deliveryman to add.</param>
        /// <param name="deliverymanIds">The set to add to.</param>
        let tryAdd deliverymanId deliverymanIds =
            if contains deliverymanId deliverymanIds then
                None
            else
                Some(DeliverymanIds(value deliverymanIds @ [ deliverymanId ]))

        /// <summary>Removes a deliveryman from the set. None when it is not present.</summary>
        /// <param name="deliverymanId">The deliveryman to remove.</param>
        /// <param name="deliverymanIds">The set to remove from.</param>
        let tryRemove deliverymanId deliverymanIds =
            if contains deliverymanId deliverymanIds then
                Some(
                    DeliverymanIds(
                        value deliverymanIds
                        |> List.filter (fun x -> x <> deliverymanId)
                    )
                )
            else
                None

    // ------------------------------------------------------------ OrderId

    [<RequireQualifiedAccess>]
    type OrderIdError =
        | Missing
        | Unrepresentable of Guid

    /// An order's identity. Guid.Empty is the framework default, never a real id.
    type OrderId =
        private
        | OrderId of Guid

        interface IValueObject<Guid> with
            member this.Wire = let (OrderId v) = this in v

        interface IPartialValueObject<OrderId, Nullable<Guid>, OrderIdError> with
            /// Boundary: takes the outside world's `Guid?` directly.
            static member Make(value: Nullable<Guid>) =
                if not value.HasValue then
                    Error OrderIdError.Missing
                elif value.Value = Guid.Empty then
                    Error(OrderIdError.Unrepresentable value.Value)
                else
                    Ok(OrderId value.Value)

            static member Explain(e) =
                match e with
                | OrderIdError.Missing -> "is required"
                | OrderIdError.Unrepresentable v -> $"holds an unrepresentable id (%O{v})"

    [<ReflectedDefinition>]
    module OrderId =
        /// <summary>Validates an order id. Returns Error if absent or if it is the empty guid.</summary>
        /// <param name="value">The raw guid. Must not be empty.</param>
        let create value = make<OrderId, _, _> value

        /// <summary>Unwraps a validated order id.</summary>
        /// <param name="orderId">The validated order id.</param>
        let value (orderId: OrderId) = (orderId :> IValueObject<Guid>).Wire
        /// <summary>Renders an order-id failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describe error = explain<OrderId, _, _> error

    // ----------------------------------------------------------- ClientId

    [<RequireQualifiedAccess>]
    type ClientIdError =
        | Missing
        | Unrepresentable of Guid

    /// The identity of the client an order was placed by.
    type ClientId =
        private
        | ClientId of Guid

        interface IValueObject<Guid> with
            member this.Wire = let (ClientId v) = this in v

        interface IPartialValueObject<ClientId, Nullable<Guid>, ClientIdError> with
            /// Boundary: takes the outside world's `Guid?` directly.
            static member Make(value: Nullable<Guid>) =
                if not value.HasValue then
                    Error ClientIdError.Missing
                elif value.Value = Guid.Empty then
                    Error(ClientIdError.Unrepresentable value.Value)
                else
                    Ok(ClientId value.Value)

            static member Explain(e) =
                match e with
                | ClientIdError.Missing -> "is required"
                | ClientIdError.Unrepresentable v -> $"holds an unrepresentable id (%O{v})"

    [<ReflectedDefinition>]
    module ClientId =
        /// <summary>Validates a client id. Returns Error if absent or if it is the empty guid.</summary>
        /// <param name="value">The raw guid. Must not be empty.</param>
        let create value = make<ClientId, _, _> value

        /// <summary>Unwraps a validated client id.</summary>
        /// <param name="clientId">The validated client id.</param>
        let value (clientId: ClientId) = (clientId :> IValueObject<Guid>).Wire
        /// <summary>Renders a client-id failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describe error = explain<ClientId, _, _> error
