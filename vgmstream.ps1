# Puts vgmstream-cli (which decodes LE2/LE3's Wwise audio, WwiseExport.cs) into <OutDir>\vgmstream.
#
# The release is pinned and checked by its SHA-256, and the zip is cached in obj/vgmstream/, so
# this downloads once. vgmstream itself is ISC-licensed; the release bundles FFmpeg (LGPL-2.1),
# mpg123 (LGPL-2.1), and libvorbis, Speex, CELT, and others under their own permissive licenses,
# all listed in THIRD-PARTY-NOTICES.txt. The release's own COPYING and README.md go along.
#
#   pwsh -NoProfile -File vgmstream.ps1 -OutDir <bin or publish folder>

param(
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'

$tag = 'r2117'
$asset = 'vgmstream-win64.zip'
$sha256 = '6C4A8A3813864FEFED081BBD337DBC0AD93BF88E0B92F5DB98D7AB258B22DC6C'
$url = "https://github.com/vgmstream/vgmstream/releases/download/$tag/$asset"

$cacheDir = Join-Path $PSScriptRoot 'obj\vgmstream'
$zip = Join-Path $cacheDir "vgmstream-$tag-win64.zip"
$target = Join-Path $OutDir 'vgmstream'

if ((Test-Path (Join-Path $target 'vgmstream-cli.exe')) -and (Test-Path (Join-Path $target "$tag.txt"))) {
    exit 0
}

if (-not (Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA256).Hash -ne $sha256) {
    Write-Host "vgmstream: fetching $tag ..."
    New-Item -ItemType Directory -Force $cacheDir | Out-Null
    Invoke-WebRequest -Uri $url -OutFile "$zip.tmp" -UseBasicParsing
    $hash = (Get-FileHash "$zip.tmp" -Algorithm SHA256).Hash
    if ($hash -ne $sha256) {
        Remove-Item "$zip.tmp" -Force
        throw "vgmstream: $asset from $tag has SHA-256 $hash, expected $sha256."
    }
    Move-Item "$zip.tmp" $zip -Force
}

if (Test-Path $target) { Remove-Item -Recurse -Force $target }
New-Item -ItemType Directory -Force $target | Out-Null
Expand-Archive -Path $zip -DestinationPath $target -Force
# Which release this is, for the up-to-date check above and for anyone reading the folder.
Set-Content -Path (Join-Path $target "$tag.txt") -Value "vgmstream $tag ($url)`nSource: https://github.com/vgmstream/vgmstream/tree/$tag"
