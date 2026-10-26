# Puts the Corresponding Source for the Legendary Explorer binaries this tool ships (GPLv3 §6)
# into the published extractor's source/ folder.
#
# The tag is read from LegendaryExplorerCore.dll itself: LEX tags every release with its file
# version (6.4.1.6065), so the zip always matches the DLLs actually shipped. A LEX build with no
# matching tag (a nightly, a local build) fails the publish instead of shipping mismatched source.
#
# Only the projects behind the shipped binaries are fetched (a sparse, blobless clone; the full
# repository is ~470 MB): LegendaryExplorerCore, TexConverter, LEX's license notices, and the
# root files (LICENSE, README). The zip is cached per tag in obj/, so this runs once per LEX version.
#
#   pwsh -NoProfile -File lex_source.ps1 -LexDir <extracted LEX release> -OutDir <publish>\source

param(
    [Parameter(Mandatory)] [string] $LexDir,
    [Parameter(Mandatory)] [string] $OutDir
)

$ErrorActionPreference = 'Stop'

$coreDll = Join-Path $LexDir 'LegendaryExplorerCore.dll'
if (-not (Test-Path $coreDll)) { throw "lex_source: $coreDll not found." }
$tag = (Get-Item $coreDll).VersionInfo.FileVersion
if (-not $tag) { throw "lex_source: $coreDll has no file version." }

$repo = 'https://github.com/ME3Tweaks/LegendaryExplorer.git'
$zipName = "LegendaryExplorer-$tag-source.zip"
$cacheDir = Join-Path $PSScriptRoot 'obj\lex-source'
$cachedZip = Join-Path $cacheDir $zipName

if (-not (Test-Path $cachedZip)) {
    Write-Host "lex_source: fetching Legendary Explorer $tag source ..."
    New-Item -ItemType Directory -Force $cacheDir | Out-Null
    $work = Join-Path $cacheDir "work-$tag"
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }

    git clone --quiet --filter=blob:none --no-checkout --depth 1 --branch $tag $repo $work
    if ($LASTEXITCODE -ne 0) {
        throw "lex_source: no tag '$tag' in $repo. Point LexDir at an official Legendary Explorer release."
    }
    git -C $work sparse-checkout set LegendaryExplorer/LegendaryExplorerCore LegendaryExplorer/TexConverter Licenses
    if ($LASTEXITCODE -ne 0) { throw "lex_source: sparse-checkout failed." }
    git -C $work checkout --quiet $tag
    if ($LASTEXITCODE -ne 0) { throw "lex_source: checkout of $tag failed." }
    $commit = (git -C $work rev-parse HEAD).Trim()
    Remove-Item -Recurse -Force (Join-Path $work '.git')

    # Where each shipped LEX binary's source is, including the one LEX itself only ships prebuilt.
    $sourceNote = @"
Legendary Explorer $tag
Repository: https://github.com/ME3Tweaks/LegendaryExplorer
Tag: $tag (commit $commit)

LegendaryExplorerCore.dll  LegendaryExplorer/LegendaryExplorerCore/LegendaryExplorerCore.csproj (GPLv3, LICENSE)
TexConverter.dll           LegendaryExplorer/TexConverter/TexConverter.vcxproj (GPLv3; DirectXTex is MIT)
CompressionWrappers.dll    Shipped prebuilt in LegendaryExplorer/LegendaryExplorerCore/Libraries/Windows/.
                           Per Licenses/COPYING_MASSEFFECTMODDER.txt it is built from the
                           MassEffectModder repository's library build target (GPLv3):
                           https://github.com/MassEffectModder/MassEffectModder
                           It links LZO (GPLv2 or later, distributed under GPLv3), zlib, MiniZip,
                           fast_zlib, and the LZMA SDK; the latter four's notices are in Licenses/.
"@
    Set-Content -LiteralPath (Join-Path $work 'SOURCE.txt') -Value $sourceNote

    Compress-Archive -Path (Join-Path $work '*') -DestinationPath $cachedZip -CompressionLevel Optimal
    Remove-Item -Recurse -Force $work
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Copy-Item -LiteralPath $cachedZip -Destination $OutDir -Force
Write-Host "lex_source: $zipName -> $OutDir"
