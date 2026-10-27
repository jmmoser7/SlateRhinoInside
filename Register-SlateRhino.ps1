<#
.SYNOPSIS
Register an installed Slate Rhino.Inside helper for selected local model folders.
.DESCRIPTION
Requires a separately installed, licensed Rhino 8 for Windows. Registration is
per user, backs up an existing entry, and does not launch Rhino or change its UI.
The default helper path works in both a normal source build and an extracted
allowlisted candidate package. In a source checkout, explicitly select the
tested versioned output when the default build is older. Candidate packaging
does not certify pending Grasshopper or camera-recall runtime acceptance.
.EXAMPLE
.\Register-SlateRhino.ps1 -SourceRoot 'C:\Models' -PrintOnly
Validate the extracted package/default build without changing registration.
.EXAMPLE
.\Register-SlateRhino.ps1 -Helper '.\bin\Release\production-0.2.0-private-ui-v10f\SlateRhinoInside.exe' -SourceRoot 'C:\Models'
Select the versioned v10f candidate from this source checkout.
#>
param(
    [Parameter(Mandatory = $true)][string[]]$SourceRoot,
    [string]$Helper = (Join-Path $PSScriptRoot 'bin\Release\net48\SlateRhinoInside.exe'),
    [string]$RhinoPath = (Join-Path $env:ProgramFiles 'Rhino 8'),
    [switch]$PrintOnly
)
$ErrorActionPreference = 'Stop'

function Existing-File([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer -or $item.PSProvider.Name -ne 'FileSystem') {
        throw "Expected a local file: $Path"
    }
    return $item.FullName
}

if ($SourceRoot.Count -eq 0 -or $SourceRoot.Count -gt 16) {
    throw 'Select between 1 and 16 existing source folders.'
}
$helperPath = Existing-File $Helper
foreach ($dependency in @('Rhino.Inside.dll', 'Newtonsoft.Json.dll')) {
    $null = Existing-File (Join-Path (Split-Path $helperPath) $dependency)
}
$rhinoRoot = (Get-Item -LiteralPath $RhinoPath).FullName
$null = Existing-File (Join-Path $rhinoRoot 'System\RhinoCommon.dll')
$null = Existing-File (Join-Path $rhinoRoot 'System\Rhino.exe')
$approved = @($SourceRoot | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    if (-not $item.PSIsContainer -or $item.PSProvider.Name -ne 'FileSystem') {
        throw "Expected an existing source folder: $_"
    }
    for ($ancestor = $item; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if (([uint32]$ancestor.Attributes -band 0x441400) -ne 0) {
            throw "Make the source folder local before registration: $_"
        }
    }
    $item.FullName
} | Select-Object -Unique)
$registration = [ordered]@{
    executable = $helperPath
    arguments = @('--rhino-path', $rhinoRoot)
    approved_roots = $approved
}
$json = $registration | ConvertTo-Json -Depth 4
if ($PrintOnly) { $json; return }
$folder = Join-Path $env:LOCALAPPDATA 'NativeFileAtlas\application-connectors'
$null = New-Item -ItemType Directory -Path $folder -Force
$destination = Join-Path $folder 'org.slate.rhino-inside.json'
if (Test-Path -LiteralPath $destination) {
    $suffix = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    Copy-Item -LiteralPath $destination -Destination ($destination + '.backup-' + $suffix)
}
$temporary = Join-Path $folder ([Guid]::NewGuid().ToString('N') + '.json.tmp')
[IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $destination -Force
Write-Output "Registered Slate Rhino.Inside: $destination"
Write-Output 'Select a .3dm model in Slate and use its Rhino blister to start the editor.'
