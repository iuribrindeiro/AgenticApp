namespace AgenticApp.Domain

open System

[<AutoOpen>]
module DeliverymanModel =

    /// A deliveryman's standing at one store. Orthogonal to presence: he can be
    /// blocked whether he is online or offline, and blocking never moves him.
    [<RequireQualifiedAccess>]
    type Standing =
        | Active
        | Blocked

    /// Whether he is inside one of a store's areas. The entry date exists only where
    /// he is actually inside, so there is no date for an area he never entered.
    [<RequireQualifiedAccess>]
    type AreaPresence =
        | Outside
        | Inside of enteredAt: ReqDateTimeOffset

    /// Where he is within the store he is online at. The two areas are independent -
    /// he can be in either, both, or neither.
    type Presence =
        { QueueArea: AreaPresence
          CollectArea: AreaPresence }

    /// <summary>A deliveryman's relationship with one store.</summary>
    /// <remarks>
    /// Presence lives *inside* Online, so being in an area while offline, invited, declined or removed is
    /// unrepresentable - and so is being online at a store he is not a member of.
    /// </remarks>
    [<RequireQualifiedAccess>]
    type Membership =
        | Invited of store: StoreId
        | Declined of store: StoreId
        | Offline of store: StoreId * standing: Standing
        | Online of store: StoreId * standing: Standing * presence: Presence
        | Removed of store: StoreId

    [<ReflectedDefinition>]
    module Membership =
        /// The store a membership belongs to, whatever state it is in.
        let internal storeId membership =
            match membership with
            | Membership.Invited s
            | Membership.Declined s
            | Membership.Removed s -> s
            | Membership.Offline(s, _) -> s
            | Membership.Online(s, _, _) -> s

    /// <summary>
    /// The two rules that span the whole list live here, in a type, rather than procedurally in
    /// Deliveryman.create: one membership per store, and at most one of them Online.
    /// </summary>
    /// <remarks>That is what lets Deliveryman be a plain public record.</remarks>
    [<RequireQualifiedAccess>]
    type StoreMembershipsError =
        | MissingAt of index: int
        | DuplicateStores of Guid list
        | OnlineAtMultiple of Guid list

    /// A deliveryman's memberships. May be empty; may not name a store twice, and may
    /// not hold two Online memberships.
    type StoreMemberships =
        private
        | StoreMemberships of Membership list

        /// The single place both list-wide invariants are checked. Internal: callers
        /// reach it through `create` from the wire, or through the module's transitions.
        static member internal OfList(memberships: Membership list) =
            let duplicates =
                memberships
                |> List.map (Membership.storeId >> StoreId.value)
                |> List.countBy id
                |> List.filter (fun (_, n) -> n > 1)
                |> List.map fst

            let onlineStores =
                memberships
                |> List.choose (fun m ->
                    match m with
                    | Membership.Online(s, _, _) -> Some(StoreId.value s)
                    | Membership.Invited _
                    | Membership.Declined _
                    | Membership.Offline _
                    | Membership.Removed _ -> None)

            let errors =
                [ if not duplicates.IsEmpty then
                      StoreMembershipsError.DuplicateStores duplicates
                  if onlineStores.Length > 1 then
                      StoreMembershipsError.OnlineAtMultiple onlineStores ]

            if errors.IsEmpty then
                Ok(StoreMemberships memberships)
            else
                Error errors

        interface IValueObject<Membership list> with
            member this.Wire = let (StoreMemberships v) = this in v

        interface IPartialValueObject<StoreMemberships, ((Membership | null) array | null), StoreMembershipsError list> with
            /// Boundary: a null array is an empty set - having no stores is legal. A null
            /// *entry* is not a membership at all, so it is reported rather than dropped.
            static member Make(value: (Membership | null) array | null) =
                let indexed =
                    match value with
                    | null -> []
                    | ms -> ms |> List.ofArray |> List.indexed

                let missing =
                    indexed
                    |> List.choose (fun (i, m) ->
                        match m with
                        | null -> Some(StoreMembershipsError.MissingAt i)
                        | _ -> None)

                if not missing.IsEmpty then
                    Error missing
                else
                    indexed |> List.choose (fun (_, m) -> Option.ofObj m) |> StoreMemberships.OfList

            static member Explain(errors) =
                errors
                |> List.map (fun e ->
                    match e with
                    | StoreMembershipsError.MissingAt i -> $"[%d{i}] is missing"
                    | StoreMembershipsError.DuplicateStores ids ->
                        let listed = ids |> List.map string |> String.concat ", "
                        $"names the same store twice (%s{listed})"
                    | StoreMembershipsError.OnlineAtMultiple ids ->
                        let listed = ids |> List.map string |> String.concat ", "
                        $"is online at more than one store (%s{listed})")
                |> String.concat "; "

    [<ReflectedDefinition>]
    module StoreMemberships =
        /// <summary>Validates a set of store memberships. Each store may appear once, and at most one membership may be Online. The set may be empty.</summary>
        /// <param name="value">The raw memberships. One per store, at most one Online; may be empty or null.</param>
        let create value = make<StoreMemberships, _, _> value

        /// <summary>Unwraps the set as a list of memberships.</summary>
        /// <param name="storeMemberships">The validated set.</param>
        let value (storeMemberships: StoreMemberships) =
            (storeMemberships :> IValueObject<Membership list>).Wire

        /// <summary>Renders a membership-set failure as a sentence fragment.</summary>
        /// <param name="errors">The failures to describe.</param>
        let describe errors = explain<StoreMemberships, _, _> errors

        /// <summary>Unwraps the set as an array, for the wire or for persistence.</summary>
        /// <param name="storeMemberships">The validated set.</param>
        let toArray storeMemberships = value storeMemberships |> Array.ofList

        /// <summary>The membership for one store, or None when he has never been invited to it.</summary>
        /// <param name="store">The store to look for.</param>
        /// <param name="storeMemberships">The set to search.</param>
        let tryFind store storeMemberships =
            value storeMemberships |> List.tryFind (fun m -> Membership.storeId m = store)

        /// <summary>The store he is currently online at, or None when he is offline everywhere.</summary>
        /// <param name="storeMemberships">The set to search.</param>
        let onlineStore storeMemberships =
            value storeMemberships
            |> List.tryPick (fun m ->
                match m with
                | Membership.Online(s, _, _) -> Some s
                | Membership.Invited _
                | Membership.Declined _
                | Membership.Offline _
                | Membership.Removed _ -> None)

        /// <summary>Where he is within the store he is online at, or None when he is offline everywhere.</summary>
        /// <param name="storeMemberships">The set to search.</param>
        let onlinePresence storeMemberships =
            value storeMemberships
            |> List.tryPick (fun m ->
                match m with
                | Membership.Online(_, _, p) -> Some p
                | Membership.Invited _
                | Membership.Declined _
                | Membership.Offline _
                | Membership.Removed _ -> None)

        /// <summary>Adds a membership for a store he has none for. None when that store is already in the set - this is where one-membership-per-store is enforced.</summary>
        /// <param name="membership">The membership to add.</param>
        /// <param name="storeMemberships">The set to add to.</param>
        let tryAdd membership storeMemberships =
            if (tryFind (Membership.storeId membership) storeMemberships).IsSome then
                None
            else
                StoreMemberships.OfList(value storeMemberships @ [ membership ])
                |> Result.toOption

        /// <summary>Replaces the membership for a store. None when that store is not in the set, or when the replacement would make him online at a second store.</summary>
        /// <param name="membership">The replacement membership.</param>
        /// <param name="storeMemberships">The set to change.</param>
        let tryReplace membership storeMemberships =
            if (tryFind (Membership.storeId membership) storeMemberships).IsNone then
                None
            else
                value storeMemberships
                |> List.map (fun x ->
                    if Membership.storeId x = Membership.storeId membership then
                        membership
                    else
                        x)
                |> StoreMemberships.OfList
                |> Result.toOption

        /// <summary>Replaces the presence of the store he is online at. None when he is offline everywhere.</summary>
        /// <param name="presence">The new presence.</param>
        /// <param name="storeMemberships">The set to change.</param>
        let tryReplaceOnline presence storeMemberships =
            value storeMemberships
            |> List.tryPick (fun m ->
                match m with
                | Membership.Online(s, standing, _) -> Some(Membership.Online(s, standing, presence))
                | Membership.Invited _
                | Membership.Declined _
                | Membership.Offline _
                | Membership.Removed _ -> None)
            |> Option.bind (fun m -> tryReplace m storeMemberships)

        /// <summary>Takes the store he is online at back offline, keeping his standing there. None when he is offline everywhere.</summary>
        /// <param name="storeMemberships">The set to change.</param>
        let tryGoOffline storeMemberships =
            value storeMemberships
            |> List.tryPick (fun m ->
                match m with
                | Membership.Online(s, standing, _) -> Some(Membership.Offline(s, standing))
                | Membership.Invited _
                | Membership.Declined _
                | Membership.Offline _
                | Membership.Removed _ -> None)
            |> Option.bind (fun m -> tryReplace m storeMemberships)

    [<RequireQualifiedAccess>]
    type DeliverymanError =
        | Id of DeliverymanIdError
        | Name of ReqStrError
        | StoreAt of index: int * error: StoreIdError
        | Stores of StoreMembershipsError list

    /// Public: every field is a value object, so no combination of them is illegal and
    /// there is no invariant left for a private constructor to guard.
    type Deliveryman =
        { Id: DeliverymanId
          Name: ReqStr
          Stores: StoreMemberships }

    [<RequireQualifiedAccess>]
    type InviteError =
        | AlreadyInvited of storeId: Guid
        | AlreadyMember of storeId: Guid

    [<RequireQualifiedAccess>]
    type AcceptInviteError =
        | NotAMember of storeId: Guid
        | NotInvited of storeId: Guid

    [<RequireQualifiedAccess>]
    type DeclineInviteError =
        | NotAMember of storeId: Guid
        | NotInvited of storeId: Guid

    [<RequireQualifiedAccess>]
    type RemoveFromStoreError =
        | NotAMember of storeId: Guid
        | AlreadyRemoved of storeId: Guid

    [<RequireQualifiedAccess>]
    type BlockError =
        | NotAMember of storeId: Guid
        | NotJoined of storeId: Guid
        | AlreadyBlocked of storeId: Guid

    [<RequireQualifiedAccess>]
    type UnblockError =
        | NotAMember of storeId: Guid
        | NotJoined of storeId: Guid
        | NotBlocked of storeId: Guid

    [<RequireQualifiedAccess>]
    type GoOnlineError =
        | NotAMember of storeId: Guid
        | NotJoined of storeId: Guid
        | AlreadyOnline of storeId: Guid

    [<RequireQualifiedAccess>]
    type GoOfflineError = NotOnline

    [<RequireQualifiedAccess>]
    type EnterAreaError =
        | NotOnline
        | AlreadyInside of since: DateTimeOffset

    [<RequireQualifiedAccess>]
    type LeaveAreaError =
        | NotOnline
        | NotInside

    [<ReflectedDefinition>]
    module Deliveryman =

        /// Every store-scoped transition ends the same way: swap one membership and
        /// rebuild, or report that the store is gone. The set decides; this only lifts.
        let private replace membership (onMissing: 'e) deliveryman =
            match StoreMemberships.tryReplace membership deliveryman.Stores with
            | Some ms -> Ok { deliveryman with Stores = ms }
            | None -> Error onMissing

        let private replaceOnline presence (onMissing: 'e) deliveryman =
            match StoreMemberships.tryReplaceOnline presence deliveryman.Stores with
            | Some ms -> Ok { deliveryman with Stores = ms }
            | None -> Error onMissing

        /// <summary>Creates a deliveryman from raw input. He starts invited to each of the given stores and online nowhere. Returns Ok, or Error listing every invalid field at once.</summary>
        /// <param name="deliverymanId">The deliveryman's id. Must be a positive number.</param>
        /// <param name="name">Display name. Required, non-blank.</param>
        /// <param name="storeIds">The stores to invite him to. Each must be a non-empty guid, with no repeats; may be empty or null.</param>
        let create deliverymanId (name: string | null) (storeIds: Nullable<Guid> array | null) =

            let memberships =
                (match storeIds with
                 | null -> []
                 | ids -> List.ofArray ids)
                |> List.indexed
                |> Validation.traverse (fun (i, raw) ->
                    StoreId.create raw |> Validation.field (fun e -> DeliverymanError.StoreAt(i, e)))
                |> Result.map (List.map Membership.Invited)
                |> Result.bind (fun ms -> StoreMemberships.OfList ms |> Validation.field DeliverymanError.Stores)

            let build i n s = { Id = i; Name = n; Stores = s }

            build
            <!> Validation.field DeliverymanError.Id (DeliverymanId.create deliverymanId)
            <*> Validation.field DeliverymanError.Name (ReqStr.create name)
            <*> memberships

        /// <summary>The deliveryman's id.</summary>
        /// <param name="deliveryman">The deliveryman to read.</param>
        let id deliveryman = DeliverymanId.value deliveryman.Id

        /// <summary>The deliveryman's display name.</summary>
        /// <param name="deliveryman">The deliveryman to read.</param>
        let name deliveryman = ReqStr.value deliveryman.Name

        /// <summary>Every store membership he holds, in whatever state each is in.</summary>
        /// <param name="deliveryman">The deliveryman to read.</param>
        let memberships deliveryman =
            StoreMemberships.toArray deliveryman.Stores

        /// <summary>The id of the store he is currently online at, or None when he is offline everywhere.</summary>
        /// <param name="deliveryman">The deliveryman to read.</param>
        let onlineStore deliveryman =
            StoreMemberships.onlineStore deliveryman.Stores |> Option.map StoreId.value

        /// <summary>Invites him to a store. A store he declined or was removed from can be invited again; one he is already invited to or working at cannot.</summary>
        /// <param name="store">The store to invite him to.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let inviteToStore store deliveryman =
            let invited = Membership.Invited store
            let storeId = StoreId.value store

            match StoreMemberships.tryFind store deliveryman.Stores with
            | None ->
                match StoreMemberships.tryAdd invited deliveryman.Stores with
                | Some ms -> Ok { deliveryman with Stores = ms }
                | None -> Error(InviteError.AlreadyMember storeId)
            | Some m ->
                match m with
                | Membership.Declined _
                | Membership.Removed _ -> replace invited (InviteError.AlreadyMember storeId) deliveryman
                | Membership.Invited _ -> Error(InviteError.AlreadyInvited storeId)
                | Membership.Offline _
                | Membership.Online _ -> Error(InviteError.AlreadyMember storeId)

        /// <summary>Accepts a store's invitation. He joins Active and offline. Returns Error if he was never invited there, or already answered.</summary>
        /// <param name="store">The store whose invitation he accepts.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let acceptInvite store deliveryman =
            let storeId = StoreId.value store

            match StoreMemberships.tryFind store deliveryman.Stores with
            | None -> Error(AcceptInviteError.NotAMember storeId)
            | Some m ->
                match m with
                | Membership.Invited _ ->
                    replace
                        (Membership.Offline(store, Standing.Active))
                        (AcceptInviteError.NotAMember storeId)
                        deliveryman
                | Membership.Declined _
                | Membership.Offline _
                | Membership.Online _
                | Membership.Removed _ -> Error(AcceptInviteError.NotInvited storeId)

        /// <summary>Declines a store's invitation. Returns Error if he was never invited there, or already answered.</summary>
        /// <param name="store">The store whose invitation he declines.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let declineInvite store deliveryman =
            let storeId = StoreId.value store

            match StoreMemberships.tryFind store deliveryman.Stores with
            | None -> Error(DeclineInviteError.NotAMember storeId)
            | Some m ->
                match m with
                | Membership.Invited _ ->
                    replace (Membership.Declined store) (DeclineInviteError.NotAMember storeId) deliveryman
                | Membership.Declined _
                | Membership.Offline _
                | Membership.Online _
                | Membership.Removed _ -> Error(DeclineInviteError.NotInvited storeId)

        /// <summary>Removes him from a store. He can never be online there again, and removing him while he is online there takes him offline as part of the same step.</summary>
        /// <param name="store">The store to remove him from.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let removeFromStore store deliveryman =
            let storeId = StoreId.value store

            match StoreMemberships.tryFind store deliveryman.Stores with
            | None -> Error(RemoveFromStoreError.NotAMember storeId)
            | Some m ->
                match m with
                | Membership.Removed _ -> Error(RemoveFromStoreError.AlreadyRemoved storeId)
                | Membership.Invited _
                | Membership.Declined _
                | Membership.Offline _
                | Membership.Online _ ->
                    replace (Membership.Removed store) (RemoveFromStoreError.NotAMember storeId) deliveryman

        /// <summary>Blocks him at one store. Legal whether he is online or offline there, and it leaves him where he is.</summary>
        /// <param name="store">The store to block him at.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let block store deliveryman =
            let storeId = StoreId.value store

            match StoreMemberships.tryFind store deliveryman.Stores with
            | None -> Error(BlockError.NotAMember storeId)
            | Some m ->
                match m with
                | Membership.Offline(s, Standing.Active) ->
                    replace (Membership.Offline(s, Standing.Blocked)) (BlockError.NotAMember storeId) deliveryman
                | Membership.Online(s, Standing.Active, p) ->
                    replace (Membership.Online(s, Standing.Blocked, p)) (BlockError.NotAMember storeId) deliveryman
                | Membership.Offline(_, Standing.Blocked)
                | Membership.Online(_, Standing.Blocked, _) -> Error(BlockError.AlreadyBlocked storeId)
                | Membership.Invited _
                | Membership.Declined _
                | Membership.Removed _ -> Error(BlockError.NotJoined storeId)

        /// <summary>Unblocks him at one store. Legal whether he is online or offline there, and it leaves him where he is.</summary>
        /// <param name="store">The store to unblock him at.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let unblock store deliveryman =
            let storeId = StoreId.value store

            match StoreMemberships.tryFind store deliveryman.Stores with
            | None -> Error(UnblockError.NotAMember storeId)
            | Some m ->
                match m with
                | Membership.Offline(s, Standing.Blocked) ->
                    replace (Membership.Offline(s, Standing.Active)) (UnblockError.NotAMember storeId) deliveryman
                | Membership.Online(s, Standing.Blocked, p) ->
                    replace (Membership.Online(s, Standing.Active, p)) (UnblockError.NotAMember storeId) deliveryman
                | Membership.Offline(_, Standing.Active)
                | Membership.Online(_, Standing.Active, _) -> Error(UnblockError.NotBlocked storeId)
                | Membership.Invited _
                | Membership.Declined _
                | Membership.Removed _ -> Error(UnblockError.NotJoined storeId)

        /// <summary>Takes him online at one store, in neither of its areas. Returns Error if he is already online anywhere, or has not joined that store.</summary>
        /// <param name="store">The store to go online at.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let goOnline store deliveryman =
            let storeId = StoreId.value store

            match StoreMemberships.onlineStore deliveryman.Stores with
            | Some s -> Error(GoOnlineError.AlreadyOnline(StoreId.value s))
            | None ->
                match StoreMemberships.tryFind store deliveryman.Stores with
                | None -> Error(GoOnlineError.NotAMember storeId)
                | Some m ->
                    match m with
                    | Membership.Offline(s, standing) ->
                        let presence =
                            { QueueArea = AreaPresence.Outside
                              CollectArea = AreaPresence.Outside }

                        replace
                            (Membership.Online(s, standing, presence))
                            (GoOnlineError.NotAMember storeId)
                            deliveryman
                    | Membership.Online _ -> Error(GoOnlineError.AlreadyOnline storeId)
                    | Membership.Invited _
                    | Membership.Declined _
                    | Membership.Removed _ -> Error(GoOnlineError.NotJoined storeId)

        /// <summary>Takes him offline, keeping his standing at that store. He leaves both areas. Returns Error if he is not online anywhere.</summary>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let goOffline deliveryman =
            match StoreMemberships.tryGoOffline deliveryman.Stores with
            | Some ms -> Ok { deliveryman with Stores = ms }
            | None -> Error GoOfflineError.NotOnline

        /// <summary>Records him entering the delivery-order queue area of the store he is online at.</summary>
        /// <param name="at">When he entered.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let enterQueueArea at deliveryman =
            match StoreMemberships.onlinePresence deliveryman.Stores with
            | None -> Error EnterAreaError.NotOnline
            | Some p ->
                match p.QueueArea with
                | AreaPresence.Inside since -> Error(EnterAreaError.AlreadyInside(ReqDateTimeOffset.value since))
                | AreaPresence.Outside ->
                    replaceOnline
                        { p with
                            QueueArea = AreaPresence.Inside at }
                        EnterAreaError.NotOnline
                        deliveryman

        /// <summary>Records him leaving the delivery-order queue area, discarding when he entered it.</summary>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let leaveQueueArea deliveryman =
            match StoreMemberships.onlinePresence deliveryman.Stores with
            | None -> Error LeaveAreaError.NotOnline
            | Some p ->
                match p.QueueArea with
                | AreaPresence.Outside -> Error LeaveAreaError.NotInside
                | AreaPresence.Inside _ ->
                    replaceOnline
                        { p with
                            QueueArea = AreaPresence.Outside }
                        LeaveAreaError.NotOnline
                        deliveryman

        /// <summary>Records him entering the collect area of the store he is online at. Independent of the queue area - he may be in both.</summary>
        /// <param name="at">When he entered.</param>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let enterCollectArea at deliveryman =
            match StoreMemberships.onlinePresence deliveryman.Stores with
            | None -> Error EnterAreaError.NotOnline
            | Some p ->
                match p.CollectArea with
                | AreaPresence.Inside since -> Error(EnterAreaError.AlreadyInside(ReqDateTimeOffset.value since))
                | AreaPresence.Outside ->
                    replaceOnline
                        { p with
                            CollectArea = AreaPresence.Inside at }
                        EnterAreaError.NotOnline
                        deliveryman

        /// <summary>Records him leaving the collect area, discarding when he entered it.</summary>
        /// <param name="deliveryman">The deliveryman to change.</param>
        let leaveCollectArea deliveryman =
            match StoreMemberships.onlinePresence deliveryman.Stores with
            | None -> Error LeaveAreaError.NotOnline
            | Some p ->
                match p.CollectArea with
                | AreaPresence.Outside -> Error LeaveAreaError.NotInside
                | AreaPresence.Inside _ ->
                    replaceOnline
                        { p with
                            CollectArea = AreaPresence.Outside }
                        LeaveAreaError.NotOnline
                        deliveryman

        /// <summary>Renders a deliveryman-creation failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describe error =
            match error with
            | DeliverymanError.Id error -> $"deliverymanId %s{DeliverymanId.describe error}"
            | DeliverymanError.Name error -> $"name %s{ReqStr.describe error}"
            | DeliverymanError.StoreAt(i, error) -> $"storeIds[%d{i}] %s{StoreId.describe error}"
            | DeliverymanError.Stores error -> $"storeIds %s{StoreMemberships.describe error}"

        /// <summary>Renders an invitation failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeInvite error =
            match error with
            | InviteError.AlreadyInvited s -> $"he is already invited to store %O{s}"
            | InviteError.AlreadyMember s -> $"he is already a member of store %O{s}"

        /// <summary>Renders an invitation-acceptance failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeAccept error =
            match error with
            | AcceptInviteError.NotAMember s -> $"he has no membership for store %O{s}"
            | AcceptInviteError.NotInvited s -> $"he has no open invitation to store %O{s}"

        /// <summary>Renders an invitation-decline failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeDecline error =
            match error with
            | DeclineInviteError.NotAMember s -> $"he has no membership for store %O{s}"
            | DeclineInviteError.NotInvited s -> $"he has no open invitation to store %O{s}"

        /// <summary>Renders a store-removal failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeRemove error =
            match error with
            | RemoveFromStoreError.NotAMember s -> $"he has no membership for store %O{s}"
            | RemoveFromStoreError.AlreadyRemoved s -> $"he is already removed from store %O{s}"

        /// <summary>Renders a blocking failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeBlock error =
            match error with
            | BlockError.NotAMember s -> $"he has no membership for store %O{s}"
            | BlockError.NotJoined s -> $"he has not joined store %O{s}"
            | BlockError.AlreadyBlocked s -> $"he is already blocked at store %O{s}"

        /// <summary>Renders an unblocking failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeUnblock error =
            match error with
            | UnblockError.NotAMember s -> $"he has no membership for store %O{s}"
            | UnblockError.NotJoined s -> $"he has not joined store %O{s}"
            | UnblockError.NotBlocked s -> $"he is not blocked at store %O{s}"

        /// <summary>Renders a go-online failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeGoOnline error =
            match error with
            | GoOnlineError.NotAMember s -> $"he has no membership for store %O{s}"
            | GoOnlineError.NotJoined s -> $"he has not joined store %O{s}"
            | GoOnlineError.AlreadyOnline s -> $"he is already online at store %O{s}"

        /// <summary>Renders a go-offline failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeGoOffline error =
            match error with
            | GoOfflineError.NotOnline -> "he is not online at any store"

        /// <summary>Renders an area-entry failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeEnterArea error =
            match error with
            | EnterAreaError.NotOnline -> "he is not online at any store"
            | EnterAreaError.AlreadyInside since -> $"he has been in that area since %O{since}"

        /// <summary>Renders an area-exit failure as a sentence fragment.</summary>
        /// <param name="error">The failure to describe.</param>
        let describeLeaveArea error =
            match error with
            | LeaveAreaError.NotOnline -> "he is not online at any store"
            | LeaveAreaError.NotInside -> "he is not in that area"
