[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputPath = (Join-Path (Get-Location) 'LFTPPilot.appinstaller'),
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'nativepapaya/lftp-pilot',
    [ValidatePattern('^[A-Za-z0-9._-]+$')][string]$PackageAssetName = 'LFTPPilot.msix',
    [ValidatePattern('^[A-Za-z0-9._-]+$')][string]$AppInstallerAssetName = 'LFTPPilot.appinstaller'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseTools.psm1') -Force
$validatedVersion = (Assert-MsixVersion -Version $Version).ToString(4)
$resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.Path]::GetExtension($resolved) -ine '.appinstaller') { throw 'OutputPath must use .appinstaller.' }
$directory = [IO.Path]::GetDirectoryName($resolved)
if (-not [IO.Directory]::Exists($directory)) { [void][IO.Directory]::CreateDirectory($directory) }
$base = "https://github.com/$Repository/releases/latest/download"
$schema = 'http://schemas.microsoft.com/appx/appinstaller/2021'
$document = [Xml.XmlDocument]::new()
[void]$document.AppendChild($document.CreateXmlDeclaration('1.0', 'utf-8', $null))
$root = $document.CreateElement('AppInstaller', $schema)
$root.SetAttribute('Version', $validatedVersion)
$root.SetAttribute('Uri', "$base/$([Uri]::EscapeDataString($AppInstallerAssetName))")
[void]$document.AppendChild($root)
$package = $document.CreateElement('MainPackage', $schema)
$package.SetAttribute('Name', 'LFTPPilot.Desktop')
$package.SetAttribute('Publisher', 'CN=LFTPPilot.Dev')
$package.SetAttribute('Version', $validatedVersion)
$package.SetAttribute('ProcessorArchitecture', 'x64')
$package.SetAttribute('Uri', "$base/$([Uri]::EscapeDataString($PackageAssetName))")
[void]$root.AppendChild($package)
$settings = $document.CreateElement('UpdateSettings', $schema)
$onLaunch = $document.CreateElement('OnLaunch', $schema)
$onLaunch.SetAttribute('HoursBetweenUpdateChecks', '24')
$onLaunch.SetAttribute('ShowPrompt', 'false')
$onLaunch.SetAttribute('UpdateBlocksActivation', 'false')
[void]$settings.AppendChild($onLaunch)
[void]$settings.AppendChild($document.CreateElement('AutomaticBackgroundTask', $schema))
[void]$root.AppendChild($settings)
$temporary = "$resolved.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    $xmlSettings = [Xml.XmlWriterSettings]::new()
    $xmlSettings.Encoding = [Text.UTF8Encoding]::new($false)
    $xmlSettings.Indent = $true
    $writer = [Xml.XmlWriter]::Create($temporary, $xmlSettings)
    try { $document.Save($writer) } finally { $writer.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $resolved -Force
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Get-Item -LiteralPath $resolved
