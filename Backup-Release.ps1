param(
    [string]$ArchiveDirectory = 'H:\anno 1800\archive',
    [string]$Version = '46.68',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+$') { throw 'Numéro de version invalide.' }

$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$publishDirectory = Join-Path $projectRoot "publish\v$Version-win-x64"
$archiveRoot = [IO.Path]::GetFullPath($ArchiveDirectory)
New-Item -ItemType Directory -Force -Path $publishDirectory, $archiveRoot | Out-Null

if (-not $SkipPublish) {
    & dotnet publish (Join-Path $projectRoot 'src\Anno1800IslandTool.UI\Anno1800IslandTool.UI.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $publishDirectory -v quiet -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Compilation échouée : aucune archive créée.' }
}

$sourceZip = Join-Path $archiveRoot "sources_$Version.zip"
$programZip = Join-Path $archiveRoot "programme_$Version.zip"
Add-Type -AssemblyName System.IO.Compression

function Write-Archive([string]$Destination, [string]$BaseDirectory, $Files) {
    $stream = [IO.File]::Open($Destination, [IO.FileMode]::Create)
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $Files) {
            $entry = [IO.Path]::GetRelativePath($BaseDirectory, $file.FullName).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $zip.Dispose(); $stream.Dispose() }
}

$sourceFiles = @(
    Get-ChildItem -LiteralPath $projectRoot -File | Where-Object { $_.Extension -in '.sln', '.slnLaunch', '.props', '.json', '.md', '.txt', '.bat', '.ps1' -and $_.Name -notmatch '\.(deps|runtimeconfig)\.json$' }
    $ignoreFile = Join-Path $projectRoot '.gitignore'
    if (Test-Path -LiteralPath $ignoreFile) { Get-Item -LiteralPath $ignoreFile -Force }
    foreach ($folder in 'src', 'tests') {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $folder) -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|TestResults)[\\/]' }
    }
)
Write-Archive $sourceZip $projectRoot $sourceFiles
Write-Archive $programZip $publishDirectory (Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)

foreach ($path in $sourceZip, $programZip) {
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        if ($archive.Entries.Count -eq 0) { throw "Archive vide : $path" }
        [pscustomobject]@{ Archive = $path; Files = $archive.Entries.Count; SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    } finally { $archive.Dispose() }
}
