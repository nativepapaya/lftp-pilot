[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$ManifestPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ManifestPath) { $ManifestPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'src\LFTPPilot.App\Package.appxmanifest' }
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$validated = (Assert-MsixVersion -Version $Version).ToString(4)
$resolved = (Resolve-Path -LiteralPath $ManifestPath).Path
[xml]$document = Get-Content -LiteralPath $resolved -Raw
$namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
$namespace.AddNamespace('p', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$identity = $document.SelectSingleNode('/p:Package/p:Identity', $namespace)
if ($null -eq $identity -or $identity.Name -ne 'LFTPPilot.Desktop' -or $identity.Publisher -ne 'CN=LFTPPilot.Dev') {
    throw 'Refusing to version an unexpected package identity.'
}
$identity.SetAttribute('Version', $validated)
$temporary = "$resolved.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $settings = [Xml.XmlWriterSettings]::new(); $settings.Encoding = [Text.UTF8Encoding]::new($false); $settings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($temporary, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $resolved -Force
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Get-Item -LiteralPath $resolved
