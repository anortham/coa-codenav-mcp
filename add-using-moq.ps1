# Add using Moq; to test files that use Mock.Of
$testsPath = "C:\source\COA CodeNav MCP\COA.CodeNav.McpServer.IntegrationTests"

# Get all .cs files in test directory
$testFiles = Get-ChildItem -Path $testsPath -Recurse -Filter "*.cs"

foreach ($file in $testFiles) {
    $content = Get-Content $file.FullName -Raw
    
    # Skip if no Mock.Of usage
    if (-not ($content -match "Mock\.Of")) {
        continue
    }
    
    # Skip if already has using Moq
    if ($content -match "using Moq;") {
        continue
    }
    
    # Add using Moq; after existing using statements
    $content = $content -replace 
        "(using [^;]+;\s*\n)(namespace|\[|public)", 
        "`$1using Moq;`n`n`$2"
    
    # Write back to file
    Set-Content -Path $file.FullName -Value $content -NoNewline
    Write-Host "Added using Moq to: $($file.Name)"
}

Write-Host "Added using Moq statements"