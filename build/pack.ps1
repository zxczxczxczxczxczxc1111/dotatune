if ($PSVersionTable.PSVersion.Major -lt 6) { throw 'Run with pwsh 7' }
# Builds the Windows installer: self-contained publish, then a Velopack release in releases\,
# then releases\DotaTune-Setup.exe, the installer to hand out: the dark DotaTune window with
# Velopack's setup inside, which it runs silently.
# Names stay ASCII: packId is the install folder %LocalAppData%\DotaTune, packTitle
# names the shortcuts and the entry in Installed apps.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $version) { throw 'Version not found in Directory.Build.props' }

$publish = Join-Path $root 'publish\win-x64'
$setupPublish = Join-Path $root 'publish\setup'
$releases = Join-Path $root 'releases'
foreach ($folder in $publish, $setupPublish) {
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
}
# vpk refuses to pack a version that is already in releases\. Both folders hold only this
# script's output, so a local rebuild of the same version starts from an empty releases\.
$sameVersion = Join-Path $releases "DotaTune-$version-full.nupkg"
if (Test-Path -LiteralPath $sameVersion) { Remove-Item -LiteralPath $releases -Recurse -Force }

Push-Location $root
try {
    dotnet publish src\DotaTune\DotaTune.csproj -c Release -r win-x64 --self-contained -o $publish -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with code $LASTEXITCODE" }

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed with code $LASTEXITCODE" }

    dotnet vpk pack `
        --packId DotaTune `
        --packTitle DotaTune `
        --packVersion $version `
        --packDir $publish `
        --mainExe DotaTune.exe `
        --icon (Join-Path $root 'src\DotaTune\Assets\dotatune.ico') `
        --outputDir $releases
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed with code $LASTEXITCODE" }

    $velopackSetup = Join-Path $releases 'DotaTune-win-Setup.exe'
    if (-not (Test-Path -LiteralPath $velopackSetup)) { throw "vpk did not write $velopackSetup" }
    dotnet publish src\DotaTune.Setup\DotaTune.Setup.csproj -c Release -r win-x64 --self-contained -o $setupPublish -nologo "-p:SetupPayload=$velopackSetup"
    if ($LASTEXITCODE -ne 0) { throw "installer publish failed with code $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $setupPublish 'DotaTune.Setup.exe') -Destination (Join-Path $releases 'DotaTune-Setup.exe') -Force
}
finally {
    Pop-Location
}

Get-ChildItem -LiteralPath $releases | Select-Object Name, Length
