$ErrorActionPreference = 'Stop'
$prepareOnly = ($args.Count -gt 0 -and $args[0] -eq '--prepare')
$skipBuild = ($args -contains '--no-build')
# Windows PowerShell 5.1 downloads far slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $projectRoot

[xml]$project = Get-Content src/ShiroBot.LagrangeAdapter.csproj -Raw
$sdkVersion = @($project.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'ShiroBot.SDK' } | Select-Object -First 1).Version
if (-not $sdkVersion) { throw 'Cannot read ShiroBot.SDK version from project file.' }

$architecture = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
# Windows PowerShell 5.1 has no $IsWindows/$IsMacOS/$IsLinux and only runs on Windows.
$onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
$platform = if ($onWindows) { 'win' }
    elseif ($IsMacOS) { 'osx' }
    elseif ($IsLinux) { if (Test-Path '/etc/alpine-release') { 'linux-musl' } else { 'linux' } }
    else { throw 'Unsupported operating system.' }
$rid = "$platform-$architecture"
# The host directory is not versioned: config.toml, plugins/ and adapters/ survive SDK upgrades.
$cacheDir = Join-Path $projectRoot ".shirobot-dev/host/$rid"
$hostExe = Join-Path $cacheDir $(if ($onWindows) { 'ShiroBot.exe' } else { 'ShiroBot' })
$versionFile = Join-Path $cacheDir '.host-version'
$archiveName = "shirobot-host-$rid-framework-dependent.zip"
$archive = Join-Path $cacheDir $archiveName

if (-not $skipBuild) {
    dotnet build src/ShiroBot.LagrangeAdapter.csproj -c Debug -p:ShiroBotPluginPackagingEnabled=false -p:ShiroBotPrepareDevelopmentHost=false
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$sourceFile = Join-Path $projectRoot '.shirobot-dev/host-source'
if ($env:SHIROBOT_DEV_HOST_SOURCE) {
    New-Item -ItemType Directory -Force (Split-Path $sourceFile) | Out-Null
    Set-Content $sourceFile $env:SHIROBOT_DEV_HOST_SOURCE -Encoding UTF8
}
$localSource = if (Test-Path $sourceFile) { (Get-Content $sourceFile -Raw).Trim() } else { '' }
if ($localSource) {
    $sourceExe = Join-Path $localSource $(if ($onWindows) { 'ShiroBot.exe' } else { 'ShiroBot' })
    if (-not (Test-Path $sourceExe)) { throw "Local host executable missing: $sourceExe" }
    New-Item -ItemType Directory -Force $cacheDir | Out-Null
    function Copy-HostFile($source, $target) {
        if ((Test-Path $target) -and (Get-FileHash $source).Hash -eq (Get-FileHash $target).Hash) { return }
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $source "$target.dev-copy" -Force
        Move-Item "$target.dev-copy" $target -Force
    }
    Get-ChildItem $localSource -File | Where-Object {
        $_.Extension -in '.dll', '.pdb' -or $_.Name -match '\.(deps|runtimeconfig)\.json$' -or $_.Name -in 'ShiroBot', 'ShiroBot.exe'
    } | ForEach-Object { Copy-HostFile $_.FullName (Join-Path $cacheDir $_.Name) }
    foreach ($folder in 'runtimes', 'Assets') {
        $sourceFolder = Join-Path $localSource $folder
        if (Test-Path $sourceFolder) {
            Get-ChildItem $sourceFolder -Recurse -File | ForEach-Object {
                $relative = $_.FullName.Substring($localSource.TrimEnd('/', '\').Length).TrimStart('/', '\')
                Copy-HostFile $_.FullName (Join-Path $cacheDir $relative)
            }
        }
    }
    if (-not $onWindows) { chmod +x $hostExe }
    Set-Content $versionFile $sdkVersion -Encoding ASCII
}

$installedVersion = if (Test-Path $versionFile) { "$(Get-Content $versionFile -Raw)".Trim() } else { '' }
if (-not (Test-Path $hostExe) -or $installedVersion -ne $sdkVersion) {
    New-Item -ItemType Directory -Force $cacheDir | Out-Null
    $url = "https://github.com/ShirokaProject/ShiroBot/releases/download/v$sdkVersion/$archiveName"
    Write-Host "Downloading ShiroBot v$sdkVersion for $rid..."
    Invoke-WebRequest -Uri $url -OutFile $archive
    # The archive only contains the host executable, so the existing local data is kept.
    Expand-Archive -Path $archive -DestinationPath $cacheDir -Force
    Remove-Item $archive
    # Expand-Archive does not keep the Unix executable bit.
    if (-not $onWindows) { chmod +x $hostExe }
    Set-Content -Path $versionFile -Value $sdkVersion -Encoding ASCII
}

$adapterDir = Join-Path $cacheDir 'adapters/lagrange'
$buildDir = (Resolve-Path 'src/bin/Debug/net11.0').Path
$manifest = Join-Path $adapterDir '.shirobot-dev-files'
New-Item -ItemType Directory -Force $adapterDir | Out-Null
# Remove files copied by the previous run that the build no longer produces. Files the
# adapter or host created there (config.toml, data, .shirobot/native, ...) are never touched.
if (Test-Path -LiteralPath $manifest) {
    foreach ($file in Get-Content -LiteralPath $manifest) {
        if (-not $file -or $file.Contains('..')) { continue }
        if (-not (Test-Path -LiteralPath (Join-Path $buildDir $file))) {
            $stale = Join-Path $adapterDir $file
            if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
        }
    }
}
Copy-Item "$buildDir/*" $adapterDir -Recurse -Force
# config.toml is never listed, so a user-edited config is not deleted if the build stops emitting one.
Get-ChildItem -LiteralPath $buildDir -Recurse -File | Where-Object { $_.Name -ne 'config.toml' } |
    ForEach-Object { $_.FullName.Substring($buildDir.Length).TrimStart('\', '/') -replace '\\', '/' } |
    Set-Content -LiteralPath $manifest -Encoding UTF8
# The host discovers the adapter entry from DLL metadata; no package-local manifest is needed.

$configPath = Join-Path $cacheDir 'config.toml'
if (-not (Test-Path $configPath)) {
    Set-Content -Path $configPath -Value "protocols = [`"lagrange`"]`n`n[api]`nenable = true`nlisten_urls = [`"http://127.0.0.1:7002`"]" -Encoding UTF8
}

$instanceConfig = Join-Path $adapterDir 'config.toml'
if (-not (Test-Path $instanceConfig)) {
    Set-Content $instanceConfig "[[instances]]`nid = `"lagrange-dev`"`nname = `"Lagrange Dev`"`nenabled = true`n`n[instances.config]`nauto_login = false`n" -Encoding UTF8
}
if (-not $env:DOTNET_ROOT) { $env:DOTNET_ROOT = Split-Path (Get-Command dotnet).Source }

if ($prepareOnly) {
    Write-Host "ShiroBot v$sdkVersion is ready. Use the Rider Run button to start it."
    exit 0
}

Write-Host "Starting ShiroBot v$sdkVersion with Lagrange V2..."
Push-Location $cacheDir
try {
    & $hostExe @args
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
