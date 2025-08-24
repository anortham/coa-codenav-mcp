# Fix all test files for framework v2.0.1 migration
$testsPath = "C:\source\COA CodeNav MCP\COA.CodeNav.McpServer.IntegrationTests"

# Get all .cs files in test directory
$testFiles = Get-ChildItem -Path $testsPath -Recurse -Filter "*.cs"

foreach ($file in $testFiles) {
    $content = Get-Content $file.FullName -Raw
    
    # Skip if no constructor instantiations found
    if (-not ($content -match "new \w+Tool\(")) {
        continue
    }
    
    # Add mock ServiceProvider before the first parameter for tool constructors
    # Pattern: new ToolName(logger, -> new ToolName(Mock.Of<IServiceProvider>(), logger,
    $content = $content -replace 
        "(new \w+Tool\(\s*\n\s+)(\w+\.\w+<\w+>.*?,)", 
        "`$1Mock.Of<IServiceProvider>(),`n            `$2"
    
    # For single-line constructors
    $content = $content -replace 
        "(new \w+Tool\()([\w\.]+<\w+>\w*.*?,)", 
        "`$1Mock.Of<IServiceProvider>(), `$2"
    
    # Write back to file
    Set-Content -Path $file.FullName -Value $content -NoNewline
    Write-Host "Fixed: $($file.Name)"
}

Write-Host "All test files updated for framework v2.0.1"