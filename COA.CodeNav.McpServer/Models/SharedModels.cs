using System.Text.Json.Serialization;
using COA.Mcp.Framework.Models;

// Alias for compatibility
using NextAction = COA.Mcp.Framework.Models.AIAction;

namespace COA.CodeNav.McpServer.Models;

// Most model types are now imported from COA.Mcp.Framework.Models
// Only extend the SymbolSummary to add documentation field

/// <summary>
/// Extended symbol summary with documentation
/// </summary>
public class ExtendedSymbolSummary : SymbolSummary
{
    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }
}

// NextAction is now just AIAction from the Framework
// We provide extension methods to help with the migration
public static class NextActionExtensions
{
    /// <summary>
    /// Creates an AIAction configured as a NextAction for compatibility
    /// </summary>
    public static AIAction CreateNextAction(string id, string description, string toolName, object? parameters = null, string priority = "medium")
    {
        return new AIAction
        {
            Action = toolName,
            Description = description,
            Category = id, // Store ID in Category for now
            Parameters = parameters as Dictionary<string, object> ?? ConvertToDictionary(parameters),
            Priority = priority switch
            {
                "high" => 80,
                "medium" => 50,
                "low" => 20,
                _ => 50
            }
        };
    }

    private static Dictionary<string, object>? ConvertToDictionary(object? parameters)
    {
        if (parameters == null) return null;
        
        var dict = new Dictionary<string, object>();
        foreach (var prop in parameters.GetType().GetProperties())
        {
            var value = prop.GetValue(parameters);
            if (value != null)
            {
                dict[prop.Name] = value;
            }
        }
        return dict;
    }
}

/// <summary>
/// Information about a type member
/// </summary>
public class TypeMemberInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("accessibility")]
    public required string Accessibility { get; set; }

    [JsonPropertyName("location")]
    public LocationInfo? Location { get; set; }

    [JsonPropertyName("documentation")]
    public string? Documentation { get; set; }

    [JsonPropertyName("returnType")]
    public string? ReturnType { get; set; }

    [JsonPropertyName("parameters")]
    public List<MemberParameterInfo>? Parameters { get; set; }

    [JsonPropertyName("typeParameters")]
    public List<string>? TypeParameters { get; set; }

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

    [JsonPropertyName("isReadOnly")]
    public bool IsReadOnly { get; set; }

    [JsonPropertyName("isWriteOnly")]
    public bool IsWriteOnly { get; set; }

    [JsonPropertyName("isConst")]
    public bool IsConst { get; set; }

    [JsonPropertyName("constantValue")]
    public string? ConstantValue { get; set; }

    [JsonPropertyName("hasGetter")]
    public bool HasGetter { get; set; }

    [JsonPropertyName("hasSetter")]
    public bool HasSetter { get; set; }

    [JsonPropertyName("isInherited")]
    public bool IsInherited { get; set; }

    [JsonPropertyName("declaringType")]
    public string? DeclaringType { get; set; }

    [JsonPropertyName("isInterfaceMember")]
    public bool IsInterfaceMember { get; set; }
}

/// <summary>
/// Information about a member parameter
/// </summary>
public class MemberParameterInfo
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