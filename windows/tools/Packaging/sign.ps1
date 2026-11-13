# Signs files with the owner's code-signing certificate (Authenticode, SHA-256, RFC 3161 timestamp).
#
# Prepared in milestone 8, NOT used yet: Lunalyr has no certificate. Nothing in the repo or its tests runs this.
# The certificate is the owner's: this script never reads, lists or exports certificate stores itself; signtool
# looks the certificate up by the thumbprint the owner passes. See docs/PACKAGING.md, "Code signing".
#
#   tools\Packaging\sign.ps1 -Thumbprint <40 hex digits> -Files <paths…> [-TimestampUrl <url>]
#
# Azure Trusted Signing instead of a certificate in the store: pass -Dlib and -DlibMetadata (the
# Azure.CodeSigning.Dlib.dll path and its metadata.json) and no thumbprint.
param(
    [string]$Thumbprint = "0000000000000000000000000000000000000000",   # placeholder: the owner's certificate
    [Parameter(Mandatory)][string[]]$Files,
    [string]$TimestampUrl = "http://timestamp.digicert.com",              # replace with the certificate issuer's
    [string]$Dlib = "",
    [string]$DlibMetadata = ""
)
$ErrorActionPreference = 'Stop'

$usingDlib = $Dlib -ne "" -and $DlibMetadata -ne ""
if (-not $usingDlib -and ($Thumbprint -notmatch '^[0-9A-Fa-f]{40}$' -or $Thumbprint -match '^0+$')) {
    throw "No certificate yet: pass the owner's certificate thumbprint (-Thumbprint) or Azure Trusted Signing (-Dlib, -DlibMetadata)."
}

$kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$signtool = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw "signtool.exe not found under $kits (Windows SDK)." }

foreach ($file in $Files) {
    if (-not (Test-Path $file)) { throw "$file not found." }
    $args = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'Lunalyr')
    if ($usingDlib) { $args += @('/dlib', $Dlib, '/dmdf', $DlibMetadata) } else { $args += @('/sha1', $Thumbprint) }
    & $signtool.FullName @args $file
    if ($LASTEXITCODE -ne 0) { throw "signtool failed on $file." }
    & $signtool.FullName verify /pa /q $file
    if ($LASTEXITCODE -ne 0) { throw "$file doesn't verify after signing." }
    "signed $file"
}
