# Builds "Lunalyr-Windows.exe", the one file people download (in artifacts\installer\<version>\): publishes the app
# and the setup program (Native AOT), zips the app into the setup, and optionally signs everything and writes the update feed. Uploads nothing and publishes nothing: putting
# the installer and feed online is the owner's step. See docs/PACKAGING.md.
#
#   tools\Packaging\build-installer.ps1 -Version 0.8.0 [-Out <folder>]
#       [-FeedUrl https://…/update.json -UpdateKey <Base64 public key>]     built into the app: its update service
#       [-DownloadUrl https://…/Lunalyr-Windows.exe -FeedKeyFile <private .pem>]   writes and signs update.json
#       [-Thumbprint <cert thumbprint>]                                    signs the app, the setup and the installer
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Out = "",
    [string]$FeedUrl = "",
    [string]$UpdateKey = "",
    [string]$DownloadUrl = "",
    [string]$FeedKeyFile = "",
    [string]$Thumbprint = ""
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ($Out -eq "") { $Out = Join-Path $repo "artifacts\installer\$Version" }
New-Item -ItemType Directory -Force $Out | Out-Null
$sign = $Thumbprint -ne ""
$requireSignature = if ($sign) { "true" } else { "" }

Push-Location $repo
try {
    "Publishing the app ($Version)…"
    dotnet publish src/Lunalyr.App/Lunalyr.App.csproj -c Release -p:Platform=x64 "-p:Version=$Version" `
        "-p:LunalyrUpdateFeed=$FeedUrl" "-p:LunalyrUpdateKey=$UpdateKey" "-p:LunalyrRequireSignature=$requireSignature" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publishing the app failed." }
    "Publishing the setup…"
    dotnet publish src/Lunalyr.Setup/Lunalyr.Setup.csproj -c Release -p:Platform=x64 "-p:Version=$Version" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publishing the setup failed." }

    $appPublish = Get-ChildItem "artifacts\bin\Lunalyr.App\x64\Release" -Recurse -Directory -Filter publish | Select-Object -First 1
    $setupExe = Get-ChildItem "artifacts\bin\Lunalyr.Setup\x64\Release" -Recurse -Filter "Lunalyr Setup.exe" |
        Where-Object { $_.DirectoryName -like "*\publish" } | Select-Object -First 1
    $stage = Join-Path $Out "app"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    robocopy $appPublish.FullName $stage /E /XF *.pdb /NFL /NDL /NJH /NJS | Out-Null
    $stub = Join-Path $Out "Lunalyr Setup (stub).exe"
    Copy-Item $setupExe.FullName $stub -Force
    $inner = Join-Path $Out "Lunalyr Setup (inner).exe"
    Copy-Item $setupExe.FullName $inner -Force

    if ($sign) {
        # The app and the setup that goes inside the payload are signed before zipping; the stub stays unsigned
        # until the whole installer is signed (a signature in the middle of the file wouldn't survive).
        & (Join-Path $PSScriptRoot "sign.ps1") -Thumbprint $Thumbprint -Files @((Join-Path $stage "Lunalyr.exe"), $inner)
    }

    # One plain name for downloads and release files (GitHub turns spaces into dots); the version is inside it.
    $installer = Join-Path $Out "Lunalyr-Windows.exe"
    dotnet run --project tools/InstallerBuilder -c Release -- make --app $stage --setup $inner --stub $stub --version $Version --out $installer | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Making the installer failed." }
    if ($sign) { & (Join-Path $PSScriptRoot "sign.ps1") -Thumbprint $Thumbprint -Files @($installer) }

    if ($DownloadUrl -ne "") {
        $feed = Join-Path $Out "update.json"
        dotnet run --project tools/InstallerBuilder -c Release -- feed --installer $installer --version $Version --url $DownloadUrl --out $feed | Out-Host
        if ($FeedKeyFile -ne "") {
            dotnet run --project tools/InstallerBuilder -c Release -- sign --feed $feed --key $FeedKeyFile | Out-Host
            if ($UpdateKey -ne "") { dotnet run --project tools/InstallerBuilder -c Release -- verify --feed $feed --public $UpdateKey | Out-Host }
        }
    }
    Remove-Item $stub, $inner -Force
    $hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "Installer: $installer ($([math]::Round((Get-Item $installer).Length / 1MB, 1)) MB, SHA-256 $hash)"
    if (-not $sign) { "Unsigned: Windows SmartScreen will warn until it is signed (docs/PACKAGING.md)." }
}
finally {
    Pop-Location
}
