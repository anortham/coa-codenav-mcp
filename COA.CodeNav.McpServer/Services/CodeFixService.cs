using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Reflection;

namespace COA.CodeNav.McpServer.Services;

/// <summary>
/// Service for discovering and applying code fixes using MEF
/// </summary>
public class CodeFixService
{
    private readonly ILogger<CodeFixService> _logger;
    private readonly CompositionHost _compositionHost;
    private readonly ImmutableDictionary<string, ImmutableArray<CodeFixProvider>> _fixersByDiagnosticId;

    public CodeFixService(ILogger<CodeFixService> logger)
    {
        _logger = logger;
        _compositionHost = CreateCompositionHost();
        _fixersByDiagnosticId = LoadCodeFixProviders();
    }

    private CompositionHost CreateCompositionHost()
    {
        try
        {
            var assemblies = new[]
            {
                // Core Roslyn assemblies
                typeof(CodeFixProvider).Assembly, // Microsoft.CodeAnalysis.Features
                typeof(Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree).Assembly, // Microsoft.CodeAnalysis.CSharp
                
                // Load the CSharp Features assembly that contains the actual code fix providers
                Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"),
                Assembly.Load("Microsoft.CodeAnalysis.Features"),
                Assembly.Load("Microsoft.CodeAnalysis.Workspaces")
            };

            var configuration = new ContainerConfiguration()
                .WithAssemblies(assemblies);

            return configuration.CreateContainer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create MEF composition host");
            // Return a minimal host so we don't crash
            return new ContainerConfiguration().CreateContainer();
        }
    }

    private ImmutableDictionary<string, ImmutableArray<CodeFixProvider>> LoadCodeFixProviders()
    {
        var fixersByDiagnostic = new Dictionary<string, List<CodeFixProvider>>();

        try
        {
            // Get all exported code fix providers
            var providers = _compositionHost.GetExports<CodeFixProvider>();
            
            _logger.LogInformation("Found {Count} code fix providers via MEF", providers.Count());

            foreach (var provider in providers)
            {
                try
                {
                    var fixableDiagnosticIds = provider.FixableDiagnosticIds;
                    _logger.LogDebug("Code fix provider {Type} handles: {Diagnostics}", 
                        provider.GetType().Name, 
                        string.Join(", ", fixableDiagnosticIds));

                    foreach (var diagnosticId in fixableDiagnosticIds)
                    {
                        if (!fixersByDiagnostic.TryGetValue(diagnosticId, out var fixers))
                        {
                            fixers = new List<CodeFixProvider>();
                            fixersByDiagnostic[diagnosticId] = fixers;
                        }
                        fixers.Add(provider);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get fixable diagnostics from provider {Type}", 
                        provider.GetType().Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load code fix providers");
        }

        // Also try to load providers without MEF as a fallback
        LoadNonMefProviders(fixersByDiagnostic);

        _logger.LogInformation("Loaded code fix providers for {Count} diagnostic IDs", fixersByDiagnostic.Count);

        return fixersByDiagnostic.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Distinct().ToImmutableArray());
    }

    private void LoadNonMefProviders(Dictionary<string, List<CodeFixProvider>> fixersByDiagnostic)
    {
        // Some providers might not be MEF exported, so we'll try to instantiate them directly
        var knownProviderTypes = new[]
        {
            // Add using directive fixes
            "Microsoft.CodeAnalysis.CSharp.CodeFixes.AddUsing.CSharpAddUsingCodeFixProvider",
            
            // Remove unnecessary usings
            "Microsoft.CodeAnalysis.CSharp.RemoveUnnecessaryImports.CSharpRemoveUnnecessaryImportsCodeFixProvider",
            
            // Make member static
            "Microsoft.CodeAnalysis.CSharp.MakeStaticCodeFixProvider",
            
            // Remove unused variable
            "Microsoft.CodeAnalysis.CSharp.RemoveUnusedVariable.CSharpRemoveUnusedVariableCodeFixProvider",
            
            // Generate constructor
            "Microsoft.CodeAnalysis.CSharp.GenerateConstructor.GenerateConstructorCodeFixProvider",
            
            // Implement interface
            "Microsoft.CodeAnalysis.CSharp.ImplementInterface.CSharpImplementInterfaceCodeFixProvider",
            
            // Add missing modifier
            "Microsoft.CodeAnalysis.CSharp.CodeFixes.AddModifier.CSharpAddModifierCodeFixProvider"
        };

        var csharpFeaturesAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.CodeAnalysis.CSharp.Features");

        if (csharpFeaturesAssembly == null)
        {
            try
            {
                csharpFeaturesAssembly = Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load Microsoft.CodeAnalysis.CSharp.Features assembly");
                return;
            }
        }

        foreach (var typeName in knownProviderTypes)
        {
            try
            {
                var type = csharpFeaturesAssembly.GetType(typeName);
                if (type != null && !type.IsAbstract)
                {
                    var provider = Activator.CreateInstance(type) as CodeFixProvider;
                    if (provider != null)
                    {
                        foreach (var diagnosticId in provider.FixableDiagnosticIds)
                        {
                            if (!fixersByDiagnostic.TryGetValue(diagnosticId, out var fixers))
                            {
                                fixers = new List<CodeFixProvider>();
                                fixersByDiagnostic[diagnosticId] = fixers;
                            }
                            
                            // Only add if not already present
                            if (!fixers.Any(f => f.GetType() == type))
                            {
                                fixers.Add(provider);
                                _logger.LogDebug("Loaded non-MEF provider: {Type} for {DiagnosticId}", 
                                    type.Name, diagnosticId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load provider type: {TypeName}", typeName);
            }
        }
    }

    public async Task<List<(CodeAction action, string diagnosticId)>> GetCodeFixesAsync(
        Document document,
        IEnumerable<Diagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var fixes = new List<(CodeAction action, string diagnosticId)>();
        
        // Get providers from both built-in and analyzer references
        var allProviders = new Dictionary<string, List<CodeFixProvider>>(_fixersByDiagnosticId
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToList()));
        
        // Load code fix providers from analyzer references
        await LoadAnalyzerCodeFixProvidersAsync(document.Project, allProviders, cancellationToken);

        foreach (var diagnostic in diagnostics)
        {
            if (!allProviders.TryGetValue(diagnostic.Id, out var providers))
            {
                _logger.LogDebug("No code fix providers found for diagnostic: {DiagnosticId}", diagnostic.Id);
                continue;
            }

            foreach (var provider in providers)
            {
                var context = new CodeFixContext(
                    document,
                    diagnostic,
                    (action, _) => fixes.Add((action, diagnostic.Id)),
                    cancellationToken);

                try
                {
                    await provider.RegisterCodeFixesAsync(context);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Code fix provider {Provider} threw exception for diagnostic {DiagnosticId}",
                        provider.GetType().Name, diagnostic.Id);
                }
            }
        }

        _logger.LogDebug("Found {Count} code fixes for {DiagnosticCount} diagnostics", 
            fixes.Count, diagnostics.Count());

        return fixes;
    }
    
    private async Task LoadAnalyzerCodeFixProvidersAsync(
        Project project,
        Dictionary<string, List<CodeFixProvider>> fixersByDiagnostic,
        CancellationToken cancellationToken)
    {
        try
        {
            // Load analyzer assemblies and find code fix providers
            var loadedAssemblies = new HashSet<string>();
            
            foreach (var analyzerRef in project.AnalyzerReferences)
            {
                if (analyzerRef is Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference fileRef)
                {
                    var assemblyPath = fileRef.FullPath;
                    if (loadedAssemblies.Contains(assemblyPath))
                        continue;
                        
                    loadedAssemblies.Add(assemblyPath);
                    
                    try
                    {
                        var assembly = Assembly.LoadFrom(assemblyPath);
                        
                        // Find all types that derive from CodeFixProvider
                        var codeFixProviderTypes = assembly.GetTypes()
                            .Where(t => !t.IsAbstract && 
                                       t.IsSubclassOf(typeof(CodeFixProvider)) &&
                                       t.GetConstructor(Type.EmptyTypes) != null);
                        
                        foreach (var providerType in codeFixProviderTypes)
                        {
                            try
                            {
                                var provider = Activator.CreateInstance(providerType) as CodeFixProvider;
                                if (provider != null)
                                {
                                    var fixableDiagnosticIds = provider.FixableDiagnosticIds;
                                    _logger.LogDebug("Analyzer code fix provider {Type} handles: {Diagnostics}", 
                                        provider.GetType().Name, 
                                        string.Join(", ", fixableDiagnosticIds));

                                    foreach (var diagnosticId in fixableDiagnosticIds)
                                    {
                                        if (!fixersByDiagnostic.TryGetValue(diagnosticId, out var fixers))
                                        {
                                            fixers = new List<CodeFixProvider>();
                                            fixersByDiagnostic[diagnosticId] = fixers;
                                        }
                                        
                                        // Only add if not already present
                                        if (!fixers.Any(f => f.GetType() == providerType))
                                        {
                                            fixers.Add(provider);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to instantiate code fix provider {Type}", 
                                    providerType.Name);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load analyzer assembly: {Path}", assemblyPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load analyzer code fix providers");
        }
        
        await Task.CompletedTask; // To satisfy async signature
    }

    public ImmutableArray<string> GetFixableDiagnosticIds()
    {
        return _fixersByDiagnosticId.Keys.ToImmutableArray();
    }
}