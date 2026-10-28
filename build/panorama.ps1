if ($PSVersionTable.PSVersion.Major -lt 6) { throw 'Run with pwsh 7' }
# Compiles the menu scripts (the MMR line, the hidden event tiles) with resourcecompiler from
# Dota 2 Workshop Tools and puts the compiled files next to their sources. The game's own base
# layout is compiled from the same source without the scripts block, for the test that checks
# the base comparison.
# Usage: pwsh -NoProfile -File build\panorama.ps1 "<folder of dota 2 beta>"

$ErrorActionPreference = 'Stop'
$GameRoot = $args[0]
if (-not $GameRoot) { throw 'Pass the Dota 2 folder, the one with game\ and content\' }
$source = Join-Path $PSScriptRoot '..\src\DotaTune.Core\Mmr\Panorama'
$compiled = Join-Path $source 'compiled'
$fixture = Join-Path $PSScriptRoot '..\tests\DotaTune.Tests\Fixtures'
$compiler = Join-Path $GameRoot 'game\bin\win64\resourcecompiler.exe'
if (-not (Test-Path $compiler)) { throw "No resourcecompiler.exe in ${GameRoot}: Dota 2 Workshop Tools are needed" }

# The compiler reads paths in the ANSI code page: the addon name stays ASCII.
$addon = 'dotatune_build'
$content = Join-Path $GameRoot "content\dota_addons\$addon"
$game = Join-Path $GameRoot "game\dota_addons\$addon"
if ((Test-Path $content) -or (Test-Path $game)) { throw "Addon folder $addon already exists, remove it first" }

try {
    New-Item -ItemType Directory -Force (Join-Path $content 'panorama\layout'), (Join-Path $content 'panorama\scripts'), (Join-Path $content 'panorama\layout\valve') | Out-Null
    Copy-Item (Join-Path $source 'base.xml') (Join-Path $content 'panorama\layout\base.xml')
    Copy-Item (Join-Path $source 'dotatune_mmr.js'), (Join-Path $source 'dotatune_mmr_data.js'), (Join-Path $source 'dotatune_events.js') (Join-Path $content 'panorama\scripts')
    $valve = (Get-Content (Join-Path $source 'base.xml') -Raw) -replace '(?s)\s*<scripts>.*?</scripts>', ''
    [System.IO.File]::WriteAllText((Join-Path $content 'panorama\layout\valve\base.xml'), $valve, (New-Object System.Text.UTF8Encoding $false))

    & $compiler -i "$content\*" -r
    if ($LASTEXITCODE -ne 0) { throw "resourcecompiler exited with $LASTEXITCODE" }

    New-Item -ItemType Directory -Force $compiled, $fixture | Out-Null
    Copy-Item (Join-Path $game 'panorama\layout\base.vxml_c') $compiled -Force
    Copy-Item (Join-Path $game 'panorama\scripts\dotatune_mmr.vjs_c'), (Join-Path $game 'panorama\scripts\dotatune_mmr_data.vjs_c'), (Join-Path $game 'panorama\scripts\dotatune_events.vjs_c') $compiled -Force
    Copy-Item (Join-Path $game 'panorama\layout\valve\base.vxml_c') (Join-Path $fixture 'valve_base.vxml_c') -Force
    Get-ChildItem $compiled, (Join-Path $fixture 'valve_base.vxml_c') -File | Select-Object Name, Length
}
finally {
    foreach ($dir in $content, $game) {
        if (Test-Path $dir) { Remove-Item $dir -Recurse -Force -Confirm:$false }
    }
}
