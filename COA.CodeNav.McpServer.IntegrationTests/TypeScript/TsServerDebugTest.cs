using System;
using System.IO;
using System.Threading.Tasks;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class TsServerDebugTest : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly ILogger<TsServerProtocolHandler> _logger;
    private readonly string _testProjectPath;
    private TsServerProtocolHandler? _handler;

    public TsServerDebugTest(ITestOutputHelper output)
    {
        _output = output;
        
        // Create a logger that writes to test output
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new XunitLoggerProvider(output));
            builder.SetMinimumLevel(LogLevel.Trace);
        });
        _logger = loggerFactory.CreateLogger<TsServerProtocolHandler>();
        
        // Use the actual test project we created
        _testProjectPath = @"C:\source\COA CodeNav MCP\ts-test-project";
        
        _output.WriteLine($"Test project path: {_testProjectPath}");
    }

    [Fact(Skip = "TSP server debug test requires full tsserver setup in CI")]
    public async Task Debug_TsServer_Navigation()
    {
        // Create a simple test file
        var testFile = Path.Combine(_testProjectPath, "src", "simple.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        
        var testContent = @"class TestClass {
    constructor() {}
    
    testMethod(): string {
        return 'test';
    }
}

const instance = new TestClass();
instance.testMethod();
";
        File.WriteAllText(testFile, testContent);
        _output.WriteLine($"Created test file: {testFile}");
        
        // Start the handler
        _handler = await TsServerProtocolHandler.CreateAsync(_logger, _testProjectPath);
        _handler.Should().NotBeNull();
        _output.WriteLine("Handler created successfully");
        
        // Open the file
        var openResult = await _handler!.OpenFileAsync(testFile, testContent);
        _output.WriteLine($"File opened: {openResult}");
        
        // Try different positions
        await TestPosition(testFile, 1, 7, "TestClass definition");  // On 'TestClass'
        await TestPosition(testFile, 9, 10, "new TestClass()");      // On 'TestClass' in new
        await TestPosition(testFile, 10, 1, "instance variable");    // On 'instance'
        await TestPosition(testFile, 10, 10, "testMethod call");     // On 'testMethod'
    }

    private async Task TestPosition(string file, int line, int offset, string description)
    {
        _output.WriteLine($"\n=== Testing {description} at line {line}, offset {offset} ===");
        
        // Get definition
        var definition = await _handler!.GetDefinitionAsync(file, line, offset);
        _output.WriteLine($"Definition result: {definition}");
        
        // Assert we get results for known good positions
        if (description.Contains("TestClass definition") || description.Contains("testMethod call"))
        {
            definition.Should().NotBeNull("Should find definition for {0}", description);
            var hasResults = definition?.ValueKind == System.Text.Json.JsonValueKind.Array && 
                           definition?.GetArrayLength() > 0;
            hasResults.Should().BeTrue("Should have definition results for {0}", description);
        }
        
        // Get hover info
        var hover = await _handler!.GetQuickInfoAsync(file, line, offset);
        _output.WriteLine($"Hover result: {hover}");
        
        // Get references
        var references = await _handler!.GetReferencesAsync(file, line, offset);
        _output.WriteLine($"References result: {references}");
    }

    public void Dispose()
    {
        _handler?.Dispose();
    }
}

// Helper class to write logs to xUnit output
public class XunitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public XunitLoggerProvider(ITestOutputHelper output)
    {
        _output = output;
    }

    public ILogger CreateLogger(string categoryName) => new XunitLogger(_output, categoryName);
    public void Dispose() { }
}

public class XunitLogger : ILogger
{
    private readonly ITestOutputHelper _output;
    private readonly string _categoryName;

    public XunitLogger(ITestOutputHelper output, string categoryName)
    {
        _output = output;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _output.WriteLine($"[{logLevel}] {_categoryName}: {formatter(state, exception)}");
        if (exception != null)
            _output.WriteLine(exception.ToString());
    }
}