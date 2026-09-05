namespace AgenticApp.Domain

open System

[<AutoOpen>]
module Primitives =

    // ------------------------------------------------------------- ReqStr

    /// Everything that can go wrong building a ReqStr. Nothing else can.
    [<RequireQualifiedAccess>]
    type ReqStrError = | Missing

    /// A string guaranteed non-null, non-whitespace, and trimmed.
    type ReqStr =
        private
        | ReqStr of string

        interface IValueObject<string> with
            member this.Wire = let (ReqStr v) = this in v

        interface IPartialValueObject<ReqStr, (string | null), ReqStrError> with
            /// Boundary: `value` may be null. Null and whitespace are both Missing.
            static member Make(value: string | null) =
                match value with
                | null -> Error ReqStrError.Missing
                | v when String.IsNullOrWhiteSpace v -> Error ReqStrError.Missing
                | v -> Ok(ReqStr(v.Trim()))

            static member Explain(e) =
                match e with
                | ReqStrError.Missing -> "is required"

    module ReqStr =
        /// <summary>Validates a required string. Returns Ok with the trimmed value, or Error if it is null, empty or whitespace.</summary>
        /// <param name="value">The raw string. Must be non-blank.</param>
        let create (value: string | null) : Result<ReqStr, ReqStrError> = make<ReqStr, _, _> value

        /// <summary>Unwraps a validated required string.</summary>
        /// <param name="v">The validated string.</param>
        let value (v: ReqStr) : string = (v :> IValueObject<string>).Wire
        /// <summary>Renders a ReqStr failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describe (e: ReqStrError) = explain<ReqStr, _, _> e

    // ------------------------------------------------------------- OptStr

    /// An optional string. Blank input *is* absence, so nothing can fail.
    type OptStr =
        private
        | OptStr of string option

        interface IValueObject<string option> with
            member this.Wire = let (OptStr v) = this in v

        interface ITotalValueObject<OptStr, string | null> with
            /// Boundary: `value` may be null. Null and whitespace are both absence.
            static member Make(value: string | null) =
                match value with
                | null -> OptStr None
                | v when String.IsNullOrWhiteSpace v -> OptStr None
                | v -> OptStr(Some(v.Trim()))

    module OptStr =
        /// <summary>Normalises an optional string. Null, empty or whitespace become absent; anything else is trimmed. Cannot fail.</summary>
        /// <param name="value">The raw string. May be null or blank.</param>
        let create (value: string | null) : OptStr = makeTotal<OptStr, _> value

        /// <summary>Unwraps an optional string as Some trimmed value, or None when absent.</summary>
        /// <param name="v">The optional string.</param>
        let value (v: OptStr) : string option = (v :> IValueObject<string option>).Wire

    // --------------------------------------------------- ReqDateTimeOffset

    [<RequireQualifiedAccess>]
    type ReqDateTimeOffsetError =
        | Missing
        | Unrepresentable of DateTimeOffset

    /// An instant guaranteed present and legally representable.
    type ReqDateTimeOffset =
        private
        | ReqDateTimeOffset of DateTimeOffset

        interface IValueObject<DateTimeOffset> with
            member this.Wire = let (ReqDateTimeOffset v) = this in v

        interface IPartialValueObject<ReqDateTimeOffset, Nullable<DateTimeOffset>, ReqDateTimeOffsetError> with
            /// Boundary: takes the outside world's `DateTimeOffset?` directly.
            static member Make(value: Nullable<DateTimeOffset>) =
                if not value.HasValue then
                    Error ReqDateTimeOffsetError.Missing
                elif value.Value = DateTimeOffset.MinValue then
                    Error(ReqDateTimeOffsetError.Unrepresentable value.Value)
                else
                    Ok(ReqDateTimeOffset value.Value)

            static member Explain(e) =
                match e with
                | ReqDateTimeOffsetError.Missing -> "is required"
                | ReqDateTimeOffsetError.Unrepresentable v -> $"holds an unrepresentable instant (%O{v})"

    module ReqDateTimeOffset =
        /// <summary>Validates a required instant. Returns Error if absent, or if it is the unset sentinel (DateTimeOffset.MinValue).</summary>
        /// <param name="value">The raw instant. Must be present and not MinValue.</param>
        let create (value: Nullable<DateTimeOffset>) : Result<ReqDateTimeOffset, ReqDateTimeOffsetError> =
            make<ReqDateTimeOffset, _, _> value

        /// <summary>Unwraps a validated required instant.</summary>
        /// <param name="v">The validated instant.</param>
        let value (v: ReqDateTimeOffset) : DateTimeOffset =
            (v :> IValueObject<DateTimeOffset>).Wire

        /// <summary>Renders a required-instant failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describe (e: ReqDateTimeOffsetError) = explain<ReqDateTimeOffset, _, _> e

    // --------------------------------------------------- OptDateTimeOffset

    /// Cannot be Missing - absence is legal. A *present* illegal instant is an error.
    [<RequireQualifiedAccess>]
    type OptDateTimeOffsetError = Unrepresentable of DateTimeOffset

    /// An optional instant. Absent, or present and legally representable.
    type OptDateTimeOffset =
        private
        | OptDateTimeOffset of DateTimeOffset option

        interface IValueObject<DateTimeOffset option> with
            member this.Wire = let (OptDateTimeOffset v) = this in v

        interface IPartialValueObject<OptDateTimeOffset, Nullable<DateTimeOffset>, OptDateTimeOffsetError> with
            /// Boundary: takes the outside world's `DateTimeOffset?` directly.
            static member Make(value: Nullable<DateTimeOffset>) =
                if not value.HasValue then
                    Ok(OptDateTimeOffset None)
                elif value.Value = DateTimeOffset.MinValue then
                    Error(OptDateTimeOffsetError.Unrepresentable value.Value)
                else
                    Ok(OptDateTimeOffset(Some value.Value))

            static member Explain(e) =
                match e with
                | OptDateTimeOffsetError.Unrepresentable v -> $"holds an unrepresentable instant (%O{v})"

    module OptDateTimeOffset =
        /// <summary>Validates an optional instant. Absence is allowed; a present MinValue is an error.</summary>
        /// <param name="value">The raw instant. May be absent, but must not be MinValue when present.</param>
        let create (value: Nullable<DateTimeOffset>) : Result<OptDateTimeOffset, OptDateTimeOffsetError> =
            make<OptDateTimeOffset, _, _> value

        /// <summary>Unwraps an optional instant as Some value, or None when absent.</summary>
        /// <param name="v">The optional instant.</param>
        let value (v: OptDateTimeOffset) : DateTimeOffset option =
            (v :> IValueObject<DateTimeOffset option>).Wire

        /// <summary>Renders an optional-instant failure as a sentence fragment.</summary>
        /// <param name="e">The failure to describe.</param>
        let describe (e: OptDateTimeOffsetError) = explain<OptDateTimeOffset, _, _> e
