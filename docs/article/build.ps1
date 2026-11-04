param([string]$Pandoc = "$env:LOCALAPPDATA\Pandoc\pandoc-3.12.1\pandoc.exe")

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & $Pandoc article.md --from markdown --to docx --reference-doc reference.docx --resource-path . -o article.docx
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "article.docx built"
}
finally {
    Pop-Location
}
