// SymbolKind.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// Use subject to the MIT License.

namespace DocRAG.Core.Enums;

/// <summary>
///     Kind classification for an identifier symbol extracted from documentation.
///     Drives the per-kind list tools (list_classes, list_enums, list_functions,
///     list_parameters) and downstream filtering.
/// </summary>
public enum SymbolKind
{
    /// <summary>
    ///     Could not classify or no signal available.
    /// </summary>
    Unknown,

    /// <summary>
    ///     A class, struct, interface, or record type.
    /// </summary>
    Type,

    /// <summary>
    ///     An enum type.
    /// </summary>
    Enum,

    /// <summary>
    ///     A function, method, or callable.
    /// </summary>
    Function,

    /// <summary>
    ///     A property or field on a type.
    /// </summary>
    Property,

    /// <summary>
    ///     A configurable parameter or setting (common in motion control / hardware docs).
    /// </summary>
    Parameter,

    /// <summary>
    ///     A namespace, module, or package.
    /// </summary>
    Namespace
}
