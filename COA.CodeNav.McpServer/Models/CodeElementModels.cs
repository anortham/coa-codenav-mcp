using System.Text.Json.Serialization;

namespace COA.CodeNav.McpServer.Models;

// This file contains domain models for representing code elements like locations, symbols, 
// diagnostics, etc. These are used across multiple tools in the CodeNav MCP server.

/// <summary>
/// Location information with file path and position
/// </summary>
public class LocationInfo
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }
    
    [JsonPropertyName("line")]
    public int Line { get; set; }
    
    [JsonPropertyName("column")]
    public int Column { get; set; }
    
    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }
    
    [JsonPropertyName("endColumn")]
    public int EndColumn { get; set; }
}

/// <summary>
/// Reference location with additional context
/// </summary>
public class ReferenceLocation : LocationInfo
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>
/// Symbol information
/// </summary>
public class SymbolInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }
    
    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("containerName")]
    public string? ContainerName { get; set; }
    
    [JsonPropertyName("containerType")]
    public string? ContainerType { get; set; }

    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }

    [JsonPropertyName("accessibility")]
    public string? Accessibility { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }
    
    [JsonPropertyName("projectName")]
    public string? ProjectName { get; set; }
    
    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }
    
    [JsonPropertyName("isAbstract")]
    public bool IsAbstract { get; set; }
    
    [JsonPropertyName("isSealed")]
    public bool IsSealed { get; set; }
    
    [JsonPropertyName("isVirtual")]
    public bool IsVirtual { get; set; }
    
    [JsonPropertyName("isOverride")]
    public bool IsOverride { get; set; }

    [JsonPropertyName("parentName")]
    public string? ParentName { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("modifiers")]
    public List<string>? Modifiers { get; set; }
}

/// <summary>
/// Document symbol information with hierarchical support
/// </summary>
public class DocumentSymbol : SymbolInfo
{
    [JsonPropertyName("modifiers")]
    public new List<string>? Modifiers { get; set; }

    [JsonPropertyName("typeParameters")]
    public List<string>? TypeParameters { get; set; }

    [JsonPropertyName("parameters")]
    public List<string>? Parameters { get; set; }

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }

    [JsonPropertyName("children")]
    public List<DocumentSymbol> Children { get; set; } = new();
}

/// <summary>
/// Hover information
/// </summary>
public class HoverInfo
{
    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
    
    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }
    
    [JsonPropertyName("typeInfo")]
    public TypeInfoDetails? TypeInfo { get; set; }
    
    [JsonPropertyName("declarationInfo")]
    public DeclarationInfo? DeclarationInfo { get; set; }
    
    [JsonPropertyName("parameters")]
    public List<ParameterInfo>? Parameters { get; set; }
    
    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }
    
    [JsonPropertyName("propertyType")]
    public string? PropertyType { get; set; }
    
    [JsonPropertyName("isReadOnly")]
    public bool? IsReadOnly { get; set; }
    
    [JsonPropertyName("isWriteOnly")]
    public bool? IsWriteOnly { get; set; }
    
    [JsonPropertyName("fieldType")]
    public string? FieldType { get; set; }
    
    [JsonPropertyName("isConst")]
    public bool? IsConst { get; set; }
    
    [JsonPropertyName("constValue")]
    public string? ConstValue { get; set; }
}

/// <summary>
/// Type information details
/// </summary>
public class TypeInfoDetails
{
    [JsonPropertyName("isClass")]
    public bool IsClass { get; set; }
    
    [JsonPropertyName("isInterface")]
    public bool IsInterface { get; set; }
    
    [JsonPropertyName("isStruct")]
    public bool IsStruct { get; set; }
    
    [JsonPropertyName("isEnum")]
    public bool IsEnum { get; set; }
    
    [JsonPropertyName("isDelegate")]
    public bool IsDelegate { get; set; }
    
    [JsonPropertyName("isGeneric")]
    public bool IsGeneric { get; set; }
    
    [JsonPropertyName("baseType")]
    public string? BaseType { get; set; }
    
    [JsonPropertyName("interfaces")]
    public List<string>? Interfaces { get; set; }
}

/// <summary>
/// Declaration information
/// </summary>
public class DeclarationInfo
{
    [JsonPropertyName("accessibility")]
    public string? Accessibility { get; set; }
    
    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }
    
    [JsonPropertyName("isAbstract")]
    public bool IsAbstract { get; set; }
    
    [JsonPropertyName("isVirtual")]
    public bool IsVirtual { get; set; }
    
    [JsonPropertyName("isOverride")]
    public bool IsOverride { get; set; }
    
    [JsonPropertyName("isSealed")]
    public bool IsSealed { get; set; }
    
    [JsonPropertyName("isExtern")]
    public bool IsExtern { get; set; }
    
    [JsonPropertyName("containingType")]
    public string? ContainingType { get; set; }
    
    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }
}

/// <summary>
/// Symbol details
/// </summary>
public class SymbolDetails
{
    [JsonPropertyName("fullName")]
    public string? FullName { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("typeInfo")]
    public string? TypeInfo { get; set; }

    [JsonPropertyName("parameters")]
    public List<ParameterInfo>? Parameters { get; set; }

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }

    [JsonPropertyName("modifiers")]
    public List<string>? Modifiers { get; set; }
}

/// <summary>
/// Parameter information
/// </summary>
public class ParameterInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("isOptional")]
    public bool IsOptional { get; set; }
    
    [JsonPropertyName("hasDefaultValue")]
    public bool HasDefaultValue { get; set; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }
}

/// <summary>
/// File change information
/// </summary>
public class FileChange
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; set; }

    [JsonPropertyName("changes")]
    public List<TextChange> Changes { get; set; } = new();
}

/// <summary>
/// Text change
/// </summary>
public class TextChange
{
    [JsonPropertyName("span")]
    public required TextSpan Span { get; set; }

    [JsonPropertyName("newText")]
    public required string NewText { get; set; }
}

/// <summary>
/// Text span
/// </summary>
public class TextSpan
{
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("column")]
    public int Column { get; set; }
}

/// <summary>
/// Rename conflict
/// </summary>
public class RenameConflict
{
    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }

    [JsonPropertyName("conflictingSymbol")]
    public string? ConflictingSymbol { get; set; }
}

/// <summary>
/// Implementation information for find implementations
/// </summary>
public class ImplementationInfo
{
    [JsonPropertyName("implementingType")]
    public string? ImplementingType { get; set; }

    [JsonPropertyName("implementingMember")]
    public string? ImplementingMember { get; set; }

    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }

    [JsonPropertyName("isDirectImplementation")]
    public bool IsDirectImplementation { get; set; }

    [JsonPropertyName("isExplicitImplementation")]
    public bool IsExplicitImplementation { get; set; }

    [JsonPropertyName("implementationType")]
    public string? ImplementationType { get; set; }
}

/// <summary>
/// Diagnostic information
/// </summary>
public class DiagnosticInfo
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("severity")]
    public required string Severity { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("category")]
    public required string Category { get; set; }

    [JsonPropertyName("source")]
    public required string Source { get; set; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }

    [JsonPropertyName("isSuppressed")]
    public bool IsSuppressed { get; set; }

    [JsonPropertyName("isWarningAsError")]
    public bool IsWarningAsError { get; set; }

    [JsonPropertyName("hasCodeFix")]
    public bool HasCodeFix { get; set; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("properties")]
    public Dictionary<string, string?>? Properties { get; set; }

    [JsonPropertyName("suggestedFixes")]
    public List<string>? SuggestedFixes { get; set; }
}