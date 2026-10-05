# Installs the latest BgDesk release for the current user and, when BGDESK_AGENT is set,
# registers it as an MCP server in that coding agent.
#
#   $env:BGDESK_AGENT = 'codex'; irm https://raw.githubusercontent.com/enwong93-sketch/background-computer-use/main/install.ps1 | iex

$ErrorActionPreference = 'Stop'
$repo = 'enwong93-sketch/background-computer-use'
$dir = if ($env:BGDESK_DIR) { $env:BGDESK_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\BgDesk' }

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest"
$asset = $release.assets | Where-Object { $_.name -like '*win-x64.zip' } | Select-Object -First 1
if (-not $asset) { throw "No win-x64 asset in release $($release.tag_name)" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("bgdesk-install-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
$zip = Join-Path $work $asset.name
Invoke-WebRequest $asset.browser_download_url -OutFile $zip
Expand-Archive $zip -DestinationPath $work -Force

# A running desktop keeps the old binaries locked.
$old = Join-Path $dir 'bgdesk.exe'
if (Test-Path $old) { try { & $old down 2>$null | Out-Null; Start-Sleep 3 } catch { } }
Get-Process bgdeskw, bgdesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep 1

New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item (Join-Path $work 'bgdesk\*') $dir -Recurse -Force
Write-Host "BgDesk $($release.tag_name) installed in $dir"

if (-not $env:BGDESK_NO_PATH) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (($userPath -split ';') -notcontains $dir) {
        [Environment]::SetEnvironmentVariable('Path', ($userPath.TrimEnd(';') + ';' + $dir), 'User')
        Write-Host "Added $dir to your PATH (new terminals pick it up)"
    }
    if (($env:Path -split ';') -notcontains $dir) { $env:Path += ";$dir" }
}

$bgdesk = Join-Path $dir 'bgdesk.exe'
if ($env:BGDESK_AGENT) { & $bgdesk install $env:BGDESK_AGENT }
$status = & $bgdesk status | ConvertFrom-Json
if (-not $status.childSessionsEnabled) {
    Write-Host "One step left, once, from an elevated terminal:  `"$bgdesk`" enable"
}
