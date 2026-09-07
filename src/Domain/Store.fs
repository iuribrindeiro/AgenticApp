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

    [<ReflectedDefinition>]
    module Store =

        /// <summary>Creates a store from raw input. Returns Ok with the store, or Error listing every invalid field at once.</summary>
        /// <param name="storeId">The store's id. Must not be the empty guid.</param>
        /// <param name="name">Display name. Required, non-blank.</param>
        /// <param name="deliverymanIds">Deliveryman ids. Each must be positive and distinct; may be empty or null.</param>
        let create storeId (name: string | null) (deliverymanIds: Nullable<int64> array | null) =
            let build id name deliverymen =
                { Id = id
                  Name = name
                  Deliverymen = deliverymen }

            build <!> Validation.field StoreError.Id (StoreId.create storeId)
            <*> Validation.field StoreError.Name (ReqStr.create name)
            <*> Validation.field StoreError.Deliverymen (DeliverymanIds.create deliverymanIds)

        /// <summary>The store's id.</summary>
        /// <param name="store">The store to read.</param>
        let id store = StoreId.value store.Id

        /// <summary>The store's display name.</summary>
        /// <param name="store">The store to read.</param>
        let name store = ReqStr.value store.Name

        /// <summary>The ids of the deliverymen assigned to this store.</summary>
        /// <param name="store">The store to read.</param>
        let deliverymen store =
            DeliverymanIds.toArray store.Deliverymen

        /// <summary>Renames a store. Cannot fail: the new name is already validated.</summary>
        /// <param name="newName">The new name.</param>
        /// <param name="store">The store to rename.</param>
        let rename newName store = { store with Name = newName }

        /// <summary>Assigns a deliveryman to a store. Returns Error if that deliveryman is already assigned.</summary>
        /// <param name="deliveryman">The deliveryman to assign.</param>
        /// <param name="store">The store to change.</param>
        let assignDeliveryman deliveryman store =
            match DeliverymanIds.tryAdd deliveryman store.Deliverymen with
            | Some ids -> Ok { store with Deliverymen = ids }
            | None -> Error(AssignDeliverymanError.AlreadyAssigned(DeliverymanId.value deliveryman))

        /// <summary>Removes a deliveryman from a store. Returns Error if that deliveryman is not assigned.</summary>
        /// <param name="deliveryman">The deliveryman to remove.</param>
        /// <param name="store">The store to change.</param>
        let unassignDeliveryman deliveryman store =
            match DeliverymanIds.tryRemove deliveryman store.Deliverymen with
            | Some ids -> Ok { store with Deliverymen = ids }
            | None -> Error(UnassignDeliverymanError.NotAssigned(DeliverymanId.value deliveryman))

        /// <summary>Renders a store-creation failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describe error =
            match error with
            | StoreError.Id idError -> $"id %s{StoreId.describe idError}"
            | StoreError.Name nameError -> $"name %s{ReqStr.describe nameError}"
            | StoreError.Deliverymen idsError -> $"deliverymanIds %s{DeliverymanIds.describe idsError}"

        /// <summary>Renders an assignment failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeAssign error =
            match error with
            | AssignDeliverymanError.AlreadyAssigned deliverymanId ->
                $"deliveryman %d{deliverymanId} is already assigned to this store"

        /// <summary>Renders an unassignment failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeUnassign error =
            match error with
            | UnassignDeliverymanError.NotAssigned deliverymanId ->
                $"deliveryman %d{deliverymanId} is not assigned to this store"
