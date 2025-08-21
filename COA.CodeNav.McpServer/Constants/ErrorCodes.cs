namespace COA.CodeNav.McpServer.Constants;

/// <summary>
/// Standard error codes for CodeNav tools
/// </summary>
public static class ErrorCodes
{
    // Workspace errors
    public const string WORKSPACE_NOT_LOADED = "WORKSPACE_NOT_LOADED";
    public const string DOCUMENT_NOT_FOUND = "DOCUMENT_NOT_FOUND";
    public const string PROJECT_NOT_FOUND = "PROJECT_NOT_FOUND";
    public const string SOLUTION_NOT_FOUND = "SOLUTION_NOT_FOUND";
    
    // Semantic errors
    public const string SEMANTIC_MODEL_UNAVAILABLE = "SEMANTIC_MODEL_UNAVAILABLE";
    public const string NO_SYMBOL_AT_POSITION = "NO_SYMBOL_AT_POSITION";
    public const string NO_DEFINITION_FOUND = "NO_DEFINITION_FOUND";
    public const string SYMBOL_NOT_FOUND = "SYMBOL_NOT_FOUND";
    public const string COMPILATION_ERROR = "COMPILATION_ERROR";
    public const string ANALYSIS_FAILED = "ANALYSIS_FAILED";
    
    // Operation errors
    public const string RENAME_CONFLICT = "RENAME_CONFLICT";
    public const string INVALID_OPERATION = "INVALID_OPERATION";
    public const string OPERATION_FAILED = "OPERATION_FAILED";
    
    // Validation errors
    public const string VALIDATION_ERROR = "VALIDATION_ERROR";
    public const string INVALID_PARAMETERS = "INVALID_PARAMETERS";
    public const string INVALID_SELECTION = "INVALID_SELECTION";
    public const string NO_TYPE_AT_POSITION = "NO_TYPE_AT_POSITION";
    
    // Diagnostic errors
    public const string DIAGNOSTIC_NOT_FOUND = "DIAGNOSTIC_NOT_FOUND";
    public const string NO_CODE_FIXES_AVAILABLE = "NO_CODE_FIXES_AVAILABLE";
    public const string FIX_NOT_FOUND = "FIX_NOT_FOUND";
    
    // System errors
    public const string INTERNAL_ERROR = "INTERNAL_ERROR";
    public const string NOT_IMPLEMENTED = "NOT_IMPLEMENTED";
    
    // TypeScript errors
    public const string TYPESCRIPT_NOT_INSTALLED = "TYPESCRIPT_NOT_INSTALLED";
    public const string TSCONFIG_NOT_FOUND = "TSCONFIG_NOT_FOUND";
    public const string TS_COMPILATION_ERROR = "TS_COMPILATION_ERROR";
    public const string TS_SERVER_NOT_RUNNING = "TS_SERVER_NOT_RUNNING";
    public const string TS_PROJECT_NOT_LOADED = "TS_PROJECT_NOT_LOADED";
    public const string TS_SERVER_START_FAILED = "TS_SERVER_START_FAILED";
    public const string TS_INVALID_FILE = "TS_INVALID_FILE";
    
    // Refactoring errors
    public const string RENAME_NOT_ALLOWED = "RENAME_NOT_ALLOWED";
    public const string INVALID_PARAMETER = "INVALID_PARAMETER";
    public const string ORGANIZE_IMPORTS_FAILED = "ORGANIZE_IMPORTS_FAILED";
    public const string ADD_IMPORTS_FAILED = "ADD_IMPORTS_FAILED";
    public const string QUICK_FIX_FAILED = "QUICK_FIX_FAILED";
}