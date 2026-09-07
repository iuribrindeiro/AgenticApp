namespace AgenticApp.Mcp

open AgenticApp.Domain

/// The Domain type names this project resolves by reflection.
///
/// Reflection is required because the Domain assembly is loaded into its own
/// AssemblyLoadContext so it can be swapped at runtime, which makes its types distinct
/// from the statically referenced ones. Names alone would drift silently on a rename -
/// producing zero tools with no error - so each one is checked against the compiler
/// here, and used as a string everywhere else.
module ValueObjectNames =

    let private nameOf (t: System.Type) =
        match t.FullName with
        | null -> failwith $"%s{t.Name} has no full name"
        | name -> name

    /// Fails the build if the marker interfaces are renamed or moved.
    let valueObjectMarker = nameOf typeof<ValueObject.IValueObjectMarker>
    let partialMarker = nameOf typeof<ValueObject.IPartialMarker>
    let totalMarker = nameOf typeof<ValueObject.ITotalMarker>
