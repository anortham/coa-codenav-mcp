using System.IO;
using System.Threading.Tasks;
using COA.CodeNav.McpServer.Infrastructure.TypeScript;
using Microsoft.Extensions.Logging;
using Xunit;
using FluentAssertions;

namespace COA.CodeNav.McpServer.IntegrationTests.TypeScript;

public class TsServerProtocolHandlerTests : IDisposable
{
    private readonly ILogger<TsServerProtocolHandler> _logger;
    private readonly string _testProjectPath;
    private TsServerProtocolHandler? _handler;

    public TsServerProtocolHandlerTests()
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Trace); // Enable all logs
        });
        _logger = loggerFactory.CreateLogger<TsServerProtocolHandler>();
        
        // Use the test project we created
        _testProjectPath = Path.Combine(Directory.GetCurrentDirectory(), "..\\..\\..\\..\\..\\ts-test-project");
        if (!Directory.Exists(_testProjectPath))
        {
            // Fallback to test data directory
            _testProjectPath = Path.Combine(Directory.GetCurrentDirectory(), "TestData", "TypeScriptProject");
        }
    }

    [Fact]
    public async Task TsServerProtocolHandler_ShouldStartSuccessfully()
    {
        // Act
        _handler = await TsServerProtocolHandler.CreateAsync(_logger, _testProjectPath);

        // Assert
        _handler.Should().NotBeNull();
        _handler!.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task TsServerProtocolHandler_GetDefinition_ShouldWork()
    {
        // Arrange
        _handler = await TsServerProtocolHandler.CreateAsync(_logger, _testProjectPath);
        _handler.Should().NotBeNull();

        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        if (!File.Exists(testFile))
        {
            // Look for any .ts file in the project
            var tsFiles = Directory.GetFiles(_testProjectPath, "*.ts", SearchOption.AllDirectories);
            testFile = tsFiles.FirstOrDefault() ?? Path.Combine(_testProjectPath, "test.ts");
        }

        // Act - Try to get definition of UserService at line 28 (0-indexed = 27)
        var result = await _handler!.GetDefinitionAsync(testFile, 28, 18);

        // Assert
        result.Should().NotBeNull();
        _logger.LogInformation("GetDefinition result: {Result}", result?.ToString());
    }

    [Fact]
    public async Task TsServerProtocolHandler_GetQuickInfo_ShouldWork()
    {
        // Arrange
        _handler = await TsServerProtocolHandler.CreateAsync(_logger, _testProjectPath);
        _handler.Should().NotBeNull();

        var testFile = Path.Combine(_testProjectPath, "src", "index.ts");
        if (!File.Exists(testFile))
        {
            // Look for any .ts file in the project
            var tsFiles = Directory.GetFiles(_testProjectPath, "*.ts", SearchOption.AllDirectories);
            testFile = tsFiles.FirstOrDefault() ?? Path.Combine(_testProjectPath, "test.ts");
        }

        // Act - Try to get hover info for TestClass at line 1, offset 7
        var result = await _handler!.GetQuickInfoAsync(testFile, 1, 7);

        // Assert
        result.Should().NotBeNull();
        _logger.LogInformation("QuickInfo result: {Result}", result?.ToString());
    }

    public void Dispose()
    {
        _handler?.Dispose();
    }
}