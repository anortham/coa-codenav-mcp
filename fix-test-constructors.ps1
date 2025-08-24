# Fix all test constructor calls for framework v2.0.1
$testsPath = "C:\source\COA CodeNav MCP\COA.CodeNav.McpServer.IntegrationTests"

# Get all .cs files in test directory
$testFiles = Get-ChildItem -Path $testsPath -Recurse -Filter "*.cs"

foreach ($file in $testFiles) {
    $content = Get-Content $file.FullName -Raw
    
    # Skip if no constructor instantiations found
    if (-not ($content -match "new \w+Tool\(")) {
        continue
    }
    
    # Fix various constructor patterns
    # Pattern 1: new ToolName(logger.Object, 
    $content = $content -replace 
        "(new \w+Tool\()(\w+\.Object,)", 
        "`$1Mock.Of<IServiceProvider>(), `$2"
    
    # Pattern 2: new ToolName(NullLogger<ToolName>.Instance,
    $content = $content -replace 
        "(new \w+Tool\()(NullLogger<\w+>\.Instance,)", 
        "`$1Mock.Of<IServiceProvider>(), `$2"
    
    # Pattern 3: new ToolName(_mockLogger.Object,
    $content = $content -replace 
        "(new \w+Tool\()(_mockLogger\.Object,)", 
        "`$1Mock.Of<IServiceProvider>(), `$2"
    
    # Pattern 4: multiline constructors with logger as first param
    $content = $content -replace 
        "(new \w+Tool\(\s*\n\s+)(NullLogger<\w+>\.Instance,)", 
        "`$1Mock.Of<IServiceProvider>(),`n            `$2"
        
    # Pattern 5: multiline constructors with mock logger as first param  
    $content = $content -replace 
        "(new \w+Tool\(\s*\n\s+)(_mockLogger\.Object,)", 
        "`$1Mock.Of<IServiceProvider>(),`n            `$2"
    
    # Ensure using Moq; is present if Mock.Of is used
    if ($content -match "Mock\.Of" -and -not ($content -match "using Moq;")) {
        $content = $content -replace 
            "(using [^;]+;\s*\n)(namespace|\[|public)", 
            "`$1using Moq;`n`n`$2"
    }
    
    # Write back to file
    Set-Content -Path $file.FullName -Value $content -NoNewline
    Write-Host "Fixed: $($file.Name)"
}

Write-Host "All test constructor calls updated for framework v2.0.1"