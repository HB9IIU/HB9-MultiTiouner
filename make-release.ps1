# Builds the program and makes the portable release zip in the "dist" folder.
# Run:  .\make-release.ps1
# The zip contains the program, the libraries, the web page, FFmpeg and MediaMTX (from tools\), and the licence files.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = ([xml](Get-Content "$root\HB9-MultiTiouner.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
foreach ($tool in 'tools\ffmpeg\ffmpeg.exe', 'tools\mediamtx\mediamtx.exe') {
    if (-not (Test-Path "$root\$tool")) { throw "$tool is missing. See NOTICE.md and the README for where to get it." }
}

$dist = "$root\dist"
$out = "$dist\build"   # its own folder, so that the build also works while the program is running
dotnet build "$root\HB9-MultiTiouner.csproj" -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

$name = "HB9-MultiTiouner-v$version-win-x64"
$stage = "$dist\$name"
Remove-Item $stage, "$dist\$name.zip" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage -Force | Out-Null

Get-ChildItem $out -File | Where-Object { $_.Extension -in '.exe', '.dll', '.config' } | Copy-Item -Destination $stage
Copy-Item "$root\Web", "$root\tools" -Destination $stage -Recurse
foreach ($file in 'LICENSE', 'NOTICE.md', 'README.md', 'CHANGELOG.md', 'player.example.json') { Copy-Item "$root\$file" $stage }

Compress-Archive -Path $stage -DestinationPath "$dist\$name.zip"
$megabytes = [math]::Round((Get-Item "$dist\$name.zip").Length / 1MB, 1)
"Done: $dist\$name.zip ($megabytes MB)"
