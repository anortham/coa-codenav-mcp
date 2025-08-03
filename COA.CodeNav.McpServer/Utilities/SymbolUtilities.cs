using Microsoft.CodeAnalysis;

namespace COA.CodeNav.McpServer.Utilities;

/// <summary>
/// Shared utilities for working with Roslyn symbols
/// </summary>
public static class SymbolUtilities
{
    /// <summary>
    /// Gets a user-friendly string representation of a symbol's kind
    /// </summary>
    public static string GetFriendlySymbolKind(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            SymbolKind.Method => symbol is IMethodSymbol m && m.MethodKind == MethodKind.Constructor ? "constructor" : "method",
            SymbolKind.Property => "property",
            SymbolKind.Field => "field",
            SymbolKind.Event => "event",
            SymbolKind.NamedType => symbol is INamedTypeSymbol t ? t.TypeKind.ToString().ToLower() : "type",
            SymbolKind.Namespace => "namespace",
            SymbolKind.Parameter => "parameter",
            SymbolKind.Local => "local variable",
            _ => symbol.Kind.ToString().ToLower()
        };
    }

    /// <summary>
    /// Gets a display string for a symbol suitable for user presentation
    /// </summary>
    public static string GetSymbolDisplayString(ISymbol symbol, SymbolDisplayFormat? format = null)
    {
        format ??= new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType |
                          SymbolDisplayMemberOptions.IncludeParameters |
                          SymbolDisplayMemberOptions.IncludeType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType |
                             SymbolDisplayParameterOptions.IncludeName,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
        );

        return symbol.ToDisplayString(format);
    }

    /// <summary>
    /// Gets a minimal display string for a symbol (just name and minimal type info)
    /// </summary>
    public static string GetMinimalSymbolDisplayString(ISymbol symbol)
    {
        var format = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            memberOptions: SymbolDisplayMemberOptions.None,
            parameterOptions: SymbolDisplayParameterOptions.None,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
        );

        return symbol.ToDisplayString(format);
    }

    /// <summary>
    /// Gets the fully qualified name of a symbol including namespace
    /// </summary>
    public static string GetFullyQualifiedName(ISymbol symbol)
    {
        var format = new SymbolDisplayFormat(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
        );

        return symbol.ToDisplayString(format);
    }

    /// <summary>
    /// Checks if a symbol is accessible from a given location
    /// </summary>
    public static bool IsAccessible(ISymbol symbol, SemanticModel semanticModel, int position)
    {
        return semanticModel.IsAccessible(position, symbol);
    }

    /// <summary>
    /// Gets the XML documentation comment ID for a symbol
    /// </summary>
    public static string? GetDocumentationCommentId(ISymbol symbol)
    {
        return symbol.GetDocumentationCommentId();
    }

    /// <summary>
    /// Determines if a symbol represents a test method
    /// </summary>
    public static bool IsTestMethod(ISymbol symbol)
    {
        if (symbol is not IMethodSymbol method)
            return false;

        // Check for common test attributes
        var testAttributeNames = new[] { "Test", "Fact", "Theory", "TestMethod" };
        
        return method.GetAttributes().Any(attr =>
            attr.AttributeClass?.Name != null &&
            testAttributeNames.Contains(attr.AttributeClass.Name));
    }

    /// <summary>
    /// Gets the accessibility level as a string
    /// </summary>
    public static string GetAccessibilityString(ISymbol symbol)
    {
        return symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.ProtectedAndInternal => "private protected",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Checks if a symbol is static
    /// </summary>
    public static bool IsStatic(ISymbol symbol)
    {
        return symbol.IsStatic;
    }

    /// <summary>
    /// Gets the containing type name for a symbol
    /// </summary>
    public static string? GetContainingTypeName(ISymbol symbol)
    {
        return symbol.ContainingType?.Name;
    }

    /// <summary>
    /// Checks if a symbol is obsolete
    /// </summary>
    public static bool IsObsolete(ISymbol symbol)
    {
        return symbol.GetAttributes().Any(attr =>
            attr.AttributeClass?.Name == "ObsoleteAttribute" ||
            attr.AttributeClass?.Name == "Obsolete");
    }
}