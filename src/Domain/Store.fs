namespace AgenticApp.Domain

open System

[<AutoOpen>]
module StoreModel =

    [<RequireQualifiedAccess>]
    type StoreError =
        | Id of StoreIdError
        | Name of ReqStrError
        | Deliverymen of DeliverymanIdsError list

    /// A store has no lifecycle states, so there is no status DU - inventing one
    /// would model a distinction the domain does not have. Add one only when a
    /// real state appears (see the add-domain-model skill).
    type Store =
        { Id: StoreId
          Name: ReqStr
          Deliverymen: DeliverymanIds }

    [<RequireQualifiedAccess>]
    type AssignDeliverymanError = AlreadyAssigned of deliverymanId: int64

    [<RequireQualifiedAccess>]
    type UnassignDeliverymanError = NotAssigned of deliverymanId: int64

    module Store =

        /// <summary>Creates a store from raw input. Returns Ok with the store, or Error listing every invalid field at once.</summary>
        /// <param name="storeId">The store's id. Must not be the empty guid.</param>
        /// <param name="name">Display name. Required, non-blank.</param>
        /// <param name="deliverymanIds">Deliveryman ids. Each must be positive and distinct; may be empty or null.</param>
        let create
            (storeId: Nullable<Guid>)
            (name: string | null)
            (deliverymanIds: Nullable<int64> array | null)
            : Validation<Store, StoreError> =

            let build i n d = { Id = i; Name = n; Deliverymen = d }

            build <!> Validation.field StoreError.Id (StoreId.create storeId)
            <*> Validation.field StoreError.Name (ReqStr.create name)
            <*> Validation.field StoreError.Deliverymen (DeliverymanIds.create deliverymanIds)

        /// <summary>The store's id.</summary>
        /// <param name="s">The store to read.</param>
        let id (s: Store) : Guid = StoreId.value s.Id
        /// <summary>The store's display name.</summary>
        /// <param name="s">The store to read.</param>
        let name (s: Store) : string = ReqStr.value s.Name
        /// <summary>The ids of the deliverymen assigned to this store.</summary>
        /// <param name="s">The store to read.</param>
        let deliverymen (s: Store) : int64 array = DeliverymanIds.toArray s.Deliverymen

        /// <summary>Renames a store. Cannot fail: the new name is already validated.</summary>
        /// <param name="newName">The new name.</param>
        /// <param name="s">The store to rename.</param>
        let rename (newName: ReqStr) (s: Store) : Store = { s with Name = newName }

        /// <summary>Assigns a deliveryman to a store. Returns Error if that deliveryman is already assigned.</summary>
        /// <param name="deliveryman">The deliveryman to assign.</param>
        /// <param name="s">The store to change.</param>
        let assignDeliveryman (deliveryman: DeliverymanId) (s: Store) : Result<Store, AssignDeliverymanError> =
            match DeliverymanIds.tryAdd deliveryman s.Deliverymen with
            | Some ids -> Ok { s with Deliverymen = ids }
            | None -> Error(AssignDeliverymanError.AlreadyAssigned(DeliverymanId.value deliveryman))

        /// <summary>Removes a deliveryman from a store. Returns Error if that deliveryman is not assigned.</summary>
        /// <param name="deliveryman">The deliveryman to remove.</param>
        /// <param name="s">The store to change.</param>
        let unassignDeliveryman (deliveryman: DeliverymanId) (s: Store) : Result<Store, UnassignDeliverymanError> =
            match DeliverymanIds.tryRemove deliveryman s.Deliverymen with
            | Some ids -> Ok { s with Deliverymen = ids }
            | None -> Error(UnassignDeliverymanError.NotAssigned(DeliverymanId.value deliveryman))

        /// <summary>Renders a store-creation failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describe (e: StoreError) =
            match e with
            | StoreError.Id e -> $"id %s{StoreId.describe e}"
            | StoreError.Name e -> $"name %s{ReqStr.describe e}"
            | StoreError.Deliverymen e -> $"deliverymanIds %s{DeliverymanIds.describe e}"

        /// <summary>Renders an assignment failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describeAssign (e: AssignDeliverymanError) =
            match e with
            | AssignDeliverymanError.AlreadyAssigned d -> $"deliveryman %d{d} is already assigned to this store"

        /// <summary>Renders an unassignment failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describeUnassign (e: UnassignDeliverymanError) =
            match e with
            | UnassignDeliverymanError.NotAssigned d -> $"deliveryman %d{d} is not assigned to this store"
