namespace AgenticApp.Domain

open System

[<AutoOpen>]
module OrderModel =

    /// <summary>Who saw an order through to completion.</summary>
    /// <remarks>
    /// An order completed out of Assigned or Collected was in a deliveryman's hands, and records him; one
    /// completed while still Preparing or Prepared was in nobody's, so there is no deliveryman to record.
    /// This is a state rather than an optional field precisely because the difference is real: "delivered
    /// by someone" and "closed with no deliveryman" are not the same order.
    /// </remarks>
    [<RequireQualifiedAccess>]
    type CompletedBy =
        | Deliveryman of deliveryman: DeliverymanId
        | NoDeliveryman

    /// <summary>
    /// An order's lifecycle. The deliveryman lives *inside* the states that have one, so an order with a
    /// deliveryman but no assignment - or an assignment with no deliveryman - is unrepresentable.
    /// </summary>
    /// <remarks>
    /// Movement between states is deliberately loose: Preparing, Prepared and Assigned are reachable from
    /// every state but Completed, so an order can be walked *backwards* - an assigned or collected order
    /// put back to prepared or preparing - whenever someone needs to undo a step. Only two rules constrain
    /// it, and both are enforced by the transitions rather than by the shape of this type: - Collected is
    /// reachable only from Assigned, and only by the deliveryman the order is assigned to - who must also
    /// be online at the order's own store and unblocked there, and in its collect area unless the store is
    /// collecting on his behalf. - Completed is terminal. It is reachable from every other state, and no
    /// transition leads out of it. Only Assigned and Collected put the order in a deliveryman's hands;
    /// Preparing and Prepared have no deliveryman at all, and a Completed order has moved out of his hands
    /// even when it records that he delivered it.
    /// </remarks>
    [<RequireQualifiedAccess>]
    type OrderStatus =
        | Preparing
        | Prepared
        | Assigned of deliveryman: DeliverymanId
        | Collected of deliveryman: DeliverymanId
        | Completed of completedBy: CompletedBy

    [<RequireQualifiedAccess>]
    type OrderError =
        | Id of OrderIdError
        | Client of ClientIdError
        | Store of StoreIdError

    /// Public: every field is a value object or a state DU, so no combination of them
    /// is illegal and there is no invariant left for a private constructor to guard.
    type Order =
        { Id: OrderId
          Client: ClientId
          Store: StoreId
          Status: OrderStatus }

    /// Moving back to Preparing fails only where every backward move fails: the order
    /// is already there, or it is completed.
    [<RequireQualifiedAccess>]
    type MarkPreparingError =
        | AlreadyPreparing
        | AlreadyCompleted

    [<RequireQualifiedAccess>]
    type MarkPreparedError =
        | AlreadyPrepared
        | AlreadyCompleted

    /// There is no "not prepared yet" case: an order can be assigned out of any state
    /// but Completed, so the only failures are the no-op and the terminal state.
    [<RequireQualifiedAccess>]
    type AssignError =
        | AlreadyAssignedTo of deliverymanId: int64
        | AlreadyCompleted

    /// <summary>Who is performing a collection.</summary>
    /// <remarks>
    /// The difference is a rule, not bookkeeping: a deliveryman collecting an order himself must be
    /// standing in his store's collect area, while the store collecting on his behalf may do so wherever he
    /// is - that case exists precisely for when he cannot be there. What neither can do is collect for a
    /// deliveryman who is offline or blocked.
    /// </remarks>
    [<RequireQualifiedAccess>]
    type Collector =
        | Deliveryman
        | Store

    /// <summary>The one transition with a real precondition, so the one with cases naming it.</summary>
    /// <remarks>
    /// Two of its cases are about the order - it must be Assigned, to this deliveryman - and three are
    /// about the deliveryman himself: he must be online at a store, unblocked at the order's store, and,
    /// when he is the one collecting, inside that store's collect area. Being online at some *other* store
    /// is NotOnlineAt too - he can only be online at one, and it has to be this one.
    /// </remarks>
    [<RequireQualifiedAccess>]
    type CollectError =
        | NotAssigned
        | AssignedToAnother of deliverymanId: int64
        | AlreadyCollected of deliverymanId: int64
        | AlreadyCompleted
        | NotOnlineAt of storeId: Guid
        | BlockedAt of storeId: Guid
        | NotInCollectArea

    /// Completion has exactly one failure: every other state permits it, so there is
    /// no "not ready yet" case to report.
    [<RequireQualifiedAccess>]
    type CompleteError = AlreadyCompleted

    [<ReflectedDefinition>]
    module Order =

        /// <summary>Creates an order from raw input. It starts Preparing, with no deliveryman. Returns Ok, or Error listing every invalid field at once.</summary>
        /// <param name="orderId">The order's id. Must not be the empty guid.</param>
        /// <param name="clientId">The client who placed it. Must not be the empty guid.</param>
        /// <param name="storeId">The store it was placed at. Must not be the empty guid.</param>
        let create orderId clientId storeId =

            let build i c s =
                { Id = i
                  Client = c
                  Store = s
                  Status = OrderStatus.Preparing }

            build
            <!> Validation.field OrderError.Id (OrderId.create orderId)
            <*> Validation.field OrderError.Client (ClientId.create clientId)
            <*> Validation.field OrderError.Store (StoreId.create storeId)

        /// <summary>The order's id.</summary>
        /// <param name="order">The order to read.</param>
        let id order = OrderId.value order.Id

        /// <summary>The id of the client who placed the order.</summary>
        /// <param name="order">The order to read.</param>
        let client order = ClientId.value order.Client

        /// <summary>The id of the store the order was placed at. Fixed for the order's whole life: it is the store whose collect area a collection happens in, and the store a deliveryman must be online at to collect it.</summary>
        /// <param name="order">The order to read.</param>
        let store order = StoreId.value order.Store

        /// <summary>Where the order is in its lifecycle.</summary>
        /// <param name="order">The order to read.</param>
        let status order = order.Status

        /// <summary>The id of the deliveryman the order passed through, or None when none ever had it - it is still preparing or prepared, or it was completed without a deliveryman.</summary>
        /// <param name="order">The order to read.</param>
        let deliveryman order =
            match order.Status with
            | OrderStatus.Preparing
            | OrderStatus.Prepared
            | OrderStatus.Completed CompletedBy.NoDeliveryman -> None
            | OrderStatus.Assigned d
            | OrderStatus.Collected d
            | OrderStatus.Completed(CompletedBy.Deliveryman d) -> Some(DeliverymanId.value d)

        /// <summary>Whether the order is in this deliveryman's hands right now.</summary>
        /// <remarks>
        /// True only while it is assigned to him or collected by him: a preparing or prepared order is in
        /// nobody's hands, and a completed one has moved out of his.
        /// </remarks>
        /// <param name="deliveryman">The deliveryman to test.</param>
        /// <param name="order">The order to read.</param>
        let isHeldBy deliveryman order =
            match order.Status with
            | OrderStatus.Assigned d
            | OrderStatus.Collected d -> d = deliveryman
            | OrderStatus.Preparing
            | OrderStatus.Prepared
            | OrderStatus.Completed _ -> false

        /// <summary>The orders in this deliveryman's hands, out of the ones given.</summary>
        /// <remarks>
        /// He may hold any number of them at once, assigned and collected mixed freely; preparing, prepared
        /// and completed orders are never among them.
        /// </remarks>
        /// <param name="deliveryman">The deliveryman to collect orders for.</param>
        /// <param name="orders">The orders to search.</param>
        let heldBy deliveryman (orders: Order array | null) =
            match orders with
            | null -> [||]
            | os -> os |> Array.filter (isHeldBy deliveryman)

        /// <summary>Puts the order back to preparing, from any state but Completed.</summary>
        /// <remarks>
        /// This is the undo of every forward step: an assigned or collected order can be walked back to
        /// preparing, dropping the deliveryman it was with. Returns Error only if it is already preparing,
        /// or is completed.
        /// </remarks>
        /// <param name="order">The order to change.</param>
        let markPreparing order =
            match order.Status with
            | OrderStatus.Prepared
            | OrderStatus.Assigned _
            | OrderStatus.Collected _ ->
                Ok
                    { order with
                        Status = OrderStatus.Preparing }
            | OrderStatus.Preparing -> Error MarkPreparingError.AlreadyPreparing
            | OrderStatus.Completed _ -> Error MarkPreparingError.AlreadyCompleted

        /// <summary>
        /// Marks the order prepared, from any state but Completed. Reachable forwards from preparing and
        /// backwards from assigned or collected, which drops the deliveryman it was with.
        /// </summary>
        /// <remarks>Returns Error only if it is already prepared, or is completed.</remarks>
        /// <param name="order">The order to change.</param>
        let markPrepared order =
            match order.Status with
            | OrderStatus.Preparing
            | OrderStatus.Assigned _
            | OrderStatus.Collected _ ->
                Ok
                    { order with
                        Status = OrderStatus.Prepared }
            | OrderStatus.Prepared -> Error MarkPreparedError.AlreadyPrepared
            | OrderStatus.Completed _ -> Error MarkPreparedError.AlreadyCompleted

        /// <summary>Assigns the order to a deliveryman, from any state but Completed.</summary>
        /// <remarks>
        /// There is no need for it to be prepared first, and an order already assigned or collected can be
        /// reassigned to someone else - that is how a handover is recorded, and it drops the previous
        /// deliveryman. Returns Error only if it is already assigned to this same deliveryman, or is
        /// completed.
        /// </remarks>
        /// <param name="deliveryman">The deliveryman to assign it to.</param>
        /// <param name="order">The order to change.</param>
        let assign deliveryman order =
            match order.Status with
            | OrderStatus.Assigned d when d = deliveryman -> Error(AssignError.AlreadyAssignedTo(DeliverymanId.value d))
            | OrderStatus.Preparing
            | OrderStatus.Prepared
            | OrderStatus.Assigned _
            | OrderStatus.Collected _ ->
                Ok
                    { order with
                        Status = OrderStatus.Assigned deliveryman }
            | OrderStatus.Completed _ -> Error AssignError.AlreadyCompleted

        /// <summary>The deliveryman side of collection, which the order's own state cannot answer.</summary>
        /// <remarks>
        /// Being online at the order's own store and unblocked there is asked of both collectors; being
        /// inside that store's collect area is asked only of the deliveryman collecting for himself, since
        /// the store collecting on his behalf is the case where he is not standing there.
        /// </remarks>
        let private eligibleToCollect collector deliveryman order =
            let storeId = StoreId.value order.Store

            match StoreMemberships.tryFind order.Store deliveryman.Stores with
            | Some(Membership.Online(_, Standing.Active, presence)) ->
                match collector with
                | Collector.Store -> Ok()
                | Collector.Deliveryman ->
                    match presence.CollectArea with
                    | AreaPresence.Inside _ -> Ok()
                    | AreaPresence.Outside -> Error CollectError.NotInCollectArea
            | Some(Membership.Online(_, Standing.Blocked, _)) -> Error(CollectError.BlockedAt storeId)
            | Some(Membership.Invited _)
            | Some(Membership.Declined _)
            | Some(Membership.Offline _)
            | Some(Membership.Removed _)
            | None -> Error(CollectError.NotOnlineAt storeId)

        /// <summary>Records an order being collected.</summary>
        /// <remarks>
        /// This is the one transition with a real precondition, and it spans both aggregates: the order
        /// must be Assigned to this deliveryman, and he must be online at the store the order was placed
        /// at, and not blocked there. A deliveryman collecting an order himself must also be inside that
        /// store's collect area; the store collecting on his behalf need not be, which is the whole point
        /// of the distinction. An offline or blocked deliveryman can have an order collected neither way.
        /// </remarks>
        /// <param name="collector">Who is collecting it - the deliveryman himself, or the store on his behalf.</param>
        /// <param name="deliveryman">The deliveryman the order is assigned to.</param>
        /// <param name="order">The order to change.</param>
        // Annotated because `.Id` alone resolves to Order, the nearest record with that field.
        let collect collector (deliveryman: Deliveryman) order =
            match order.Status with
            | OrderStatus.Assigned d when d = deliveryman.Id ->
                eligibleToCollect collector deliveryman order
                |> Result.map (fun () ->
                    { order with
                        Status = OrderStatus.Collected d })
            | OrderStatus.Assigned d -> Error(CollectError.AssignedToAnother(DeliverymanId.value d))
            | OrderStatus.Preparing
            | OrderStatus.Prepared -> Error CollectError.NotAssigned
            | OrderStatus.Collected d -> Error(CollectError.AlreadyCollected(DeliverymanId.value d))
            | OrderStatus.Completed _ -> Error CollectError.AlreadyCompleted

        /// <summary>Completes an order from any state but Completed.</summary>
        /// <remarks>
        /// It takes no deliveryman of its own: one completed out of assignment or collection records the
        /// deliveryman who had it, and one completed while still preparing or prepared records that nobody
        /// did. Returns Error only if it is already completed - Completed is terminal.
        /// </remarks>
        /// <param name="order">The order to change.</param>
        let complete order =
            // Pinned because `Ok` alone leaves this generic in the error type, and a
            // quotation cannot contain an inner generic function (FS1230).
            let completed completedBy : Result<Order, CompleteError> =
                Ok
                    { order with
                        Status = OrderStatus.Completed completedBy }

            match order.Status with
            | OrderStatus.Assigned d
            | OrderStatus.Collected d -> completed (CompletedBy.Deliveryman d)
            | OrderStatus.Preparing
            | OrderStatus.Prepared -> completed CompletedBy.NoDeliveryman
            | OrderStatus.Completed _ -> Error CompleteError.AlreadyCompleted

        /// <summary>Renders an order-creation failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describe error =
            match error with
            | OrderError.Id error -> $"orderId %s{OrderId.describe error}"
            | OrderError.Client error -> $"clientId %s{ClientId.describe error}"
            | OrderError.Store error -> $"storeId %s{StoreId.describe error}"

        /// <summary>Renders a move-back-to-preparing failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeMarkPreparing error =
            match error with
            | MarkPreparingError.AlreadyPreparing -> "it is already preparing"
            | MarkPreparingError.AlreadyCompleted -> "it is already completed"

        /// <summary>Renders a preparation failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeMarkPrepared error =
            match error with
            | MarkPreparedError.AlreadyPrepared -> "it is already prepared"
            | MarkPreparedError.AlreadyCompleted -> "it is already completed"

        /// <summary>Renders an assignment failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeAssign error =
            match error with
            | AssignError.AlreadyAssignedTo d -> $"it is already assigned to deliveryman %d{d}"
            | AssignError.AlreadyCompleted -> "it is already completed"

        /// <summary>Renders a collection failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeCollect error =
            match error with
            | CollectError.NotAssigned -> "it is not assigned to a deliveryman"
            | CollectError.AssignedToAnother d -> $"it is assigned to deliveryman %d{d}"
            | CollectError.AlreadyCollected d -> $"it was already collected by deliveryman %d{d}"
            | CollectError.AlreadyCompleted -> "it is already completed"
            | CollectError.NotOnlineAt s -> $"he is not online at store %O{s}, where the order was placed"
            | CollectError.BlockedAt s -> $"he is blocked at store %O{s}"
            | CollectError.NotInCollectArea -> "he is not in the store's collect area"

        /// <summary>Renders a completion failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeComplete error =
            match error with
            | CompleteError.AlreadyCompleted -> "it is already completed"
