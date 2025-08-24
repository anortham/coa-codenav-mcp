# Fix all tools for framework v2.0.1 migration
$toolsPath = "C:\source\COA CodeNav MCP\COA.CodeNav.McpServer\Tools"

# Get all .cs files in Tools directory and subdirectories
$toolFiles = Get-ChildItem -Path $toolsPath -Recurse -Filter "*.cs"

foreach ($file in $toolFiles) {
    $content = Get-Content $file.FullName -Raw
    
    # Skip if already has IServiceProvider parameter
    if ($content -match "IServiceProvider serviceProvider") {
        continue
    }
    
    # Find constructor patterns and add IServiceProvider parameter
    $content = $content -replace 
        "(\s+public \w+Tool\(\s*\n\s+)(ILogger<\w+> logger,)", 
        "`$1IServiceProvider serviceProvider,`n        `$2"
    
    # Fix base constructor calls
    $content = $content -replace ": base\(logger\)", ": base(serviceProvider, logger)"
    
    # Write back to file
    Set-Content -Path $file.FullName -Value $content -NoNewline
    Write-Host "Fixed: $($file.Name)"
}

Write-Host "All tools updated for framework v2.0.1"