namespace COA.CodeNav.McpServer.Constants;

/// <summary>
/// Centralized constants for all tool names to ensure consistency across the codebase
/// </summary>
public static class ToolNames
{
    // Navigation Tools
    public const string GoToDefinition = "csharp_goto_definition";
    public const string FindAllReferences = "csharp_find_all_references";
    public const string FindImplementations = "csharp_find_implementations";
    public const string FindAllOverrides = "csharp_find_all_overrides";
    public const string CallHierarchy = "csharp_call_hierarchy";
    public const string TraceCallStack = "csharp_trace_call_stack";
    public const string TypeHierarchy = "csharp_type_hierarchy";
    
    // Symbol Tools
    public const string SymbolSearch = "csharp_symbol_search";
    public const string DocumentSymbols = "csharp_document_symbols";
    public const string GetTypeMembers = "csharp_get_type_members";
    public const string Hover = "csharp_hover";
    
    // Refactoring Tools
    public const string RenameSymbol = "csharp_rename_symbol";
    public const string ExtractMethod = "csharp_extract_method";
    public const string GenerateCode = "csharp_generate_code";
    public const string AddMissingUsings = "csharp_add_missing_usings";
    public const string FormatDocument = "csharp_format_document";
    
    // Analysis Tools
    public const string GetDiagnostics = "csharp_get_diagnostics";
    public const string ApplyCodeFix = "csharp_apply_code_fix";
    public const string CodeMetrics = "csharp_code_metrics";
    public const string FindUnusedCode = "csharp_find_unused_code";
    public const string CodeCloneDetection = "csharp_code_clone_detection";
    public const string DependencyAnalysis = "csharp_dependency_analysis";
    public const string SolutionWideFindReplace = "csharp_solution_wide_find_replace";
    
    // Workspace Tools
    public const string LoadSolution = "csharp_load_solution";
    public const string LoadProject = "csharp_load_project";
    public const string GetWorkspaceStatistics = "csharp_get_workspace_statistics";
}