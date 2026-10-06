param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$project = Join-Path $repoRoot 'src/Qingyue.Desktop/EpubKindleFix.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project -Raw -Encoding UTF8
$version = [string]$projectXml.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a semantic project version.' }
$packageFolder = Join-Path $OutputRoot "qingyue-v$version-win-x64"
$zip = "$packageFolder.zip"
$sourceZip = Join-Path $OutputRoot "qingyue-v$version-source.zip"
foreach ($target in @($packageFolder, $zip, $sourceZip, (Join-Path $OutputRoot 'SHA256SUMS.txt'))) {
    if (Test-Path -LiteralPath $target) { throw "Output already exists: $target. Choose another OutputRoot." }
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $packageFolder
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'), (Join-Path $repoRoot 'LICENSE'), (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $packageFolder
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $packageFolder -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $packageFolder -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Qingyue.Desktop/Assets/KindleIcon.ico') -Destination (Join-Path $packageFolder '轻阅桌面图标.ico')
Compress-Archive -LiteralPath $packageFolder -DestinationPath $zip -CompressionLevel Optimal
& git -C $repoRoot archive --format=zip --output=$sourceZip HEAD
if ($LASTEXITCODE -ne 0) { throw 'git archive failed; commit the source before publishing.' }
$sums = foreach ($file in @($zip, $sourceZip)) { "$( (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))" }
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
Write-Output "Published Qingyue $version to $OutputRoot"
