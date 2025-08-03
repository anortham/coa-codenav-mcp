using Microsoft.Extensions.Logging;

namespace COA.CodeNav.McpServer.Utilities;

/// <summary>
/// Utility for finding solution files in a directory hierarchy
/// </summary>
public class SolutionFinder
{
    private readonly ILogger<SolutionFinder> _logger;

    public SolutionFinder(ILogger<SolutionFinder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Finds a solution file starting from the current directory and searching upward
    /// </summary>
    /// <param name="startDirectory">Directory to start searching from (defaults to current directory)</param>
    /// <param name="maxDepth">Maximum levels to search upward</param>
    /// <param name="preferredName">Preferred solution name if multiple are found</param>
    /// <returns>Path to the solution file, or null if not found</returns>
    public string? FindSolution(string? startDirectory = null, int maxDepth = 5, string? preferredName = null)
    {
        var currentDir = string.IsNullOrEmpty(startDirectory) 
            ? Directory.GetCurrentDirectory() 
            : Path.GetFullPath(startDirectory);

        _logger.LogDebug("Searching for solution files starting from: {Directory}", currentDir);

        for (int depth = 0; depth <= maxDepth; depth++)
        {
            if (!Directory.Exists(currentDir))
            {
                _logger.LogWarning("Directory does not exist: {Directory}", currentDir);
                return null;
            }

            var slnFiles = Directory.GetFiles(currentDir, "*.sln", SearchOption.TopDirectoryOnly);
            
            if (slnFiles.Length > 0)
            {
                _logger.LogInformation("Found {Count} solution file(s) at depth {Depth} in {Directory}", 
                    slnFiles.Length, depth, currentDir);

                // If there's a preferred name, try to find it
                if (!string.IsNullOrEmpty(preferredName))
                {
                    var preferred = slnFiles.FirstOrDefault(f => 
                        Path.GetFileName(f).Equals(preferredName, StringComparison.OrdinalIgnoreCase));
                    
                    if (preferred != null)
                    {
                        _logger.LogInformation("Found preferred solution: {Solution}", preferred);
                        return preferred;
                    }
                }

                // If only one solution, use it
                if (slnFiles.Length == 1)
                {
                    _logger.LogInformation("Found solution: {Solution}", slnFiles[0]);
                    return slnFiles[0];
                }

                // Multiple solutions found, log them and pick the first
                _logger.LogWarning("Multiple solutions found, using first one:");
                foreach (var sln in slnFiles)
                {
                    _logger.LogWarning("  - {Solution}", Path.GetFileName(sln));
                }
                
                return slnFiles[0];
            }

            // Move up one directory
            var parent = Directory.GetParent(currentDir);
            if (parent == null)
            {
                _logger.LogDebug("Reached root directory, no solution found");
                break;
            }

            currentDir = parent.FullName;
        }

        _logger.LogWarning("No solution file found within {MaxDepth} levels", maxDepth);
        return null;
    }

    /// <summary>
    /// Finds all solution files in a directory hierarchy
    /// </summary>
    public List<string> FindAllSolutions(string? startDirectory = null, int maxDepth = 5)
    {
        var solutions = new List<string>();
        var currentDir = string.IsNullOrEmpty(startDirectory) 
            ? Directory.GetCurrentDirectory() 
            : Path.GetFullPath(startDirectory);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int depth = 0; depth <= maxDepth; depth++)
        {
            if (!Directory.Exists(currentDir) || visited.Contains(currentDir))
                break;

            visited.Add(currentDir);
            
            var slnFiles = Directory.GetFiles(currentDir, "*.sln", SearchOption.TopDirectoryOnly);
            solutions.AddRange(slnFiles);

            var parent = Directory.GetParent(currentDir);
            if (parent == null)
                break;

            currentDir = parent.FullName;
        }

        return solutions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}