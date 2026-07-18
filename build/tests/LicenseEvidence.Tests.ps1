Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$buildRoot = Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-Sha512([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA512).Hash.ToLowerInvariant() }
function Get-Base64Sha512([string]$Path) { $h=[Security.Cryptography.SHA512]::Create();$s=[IO.File]::OpenRead($Path);try{return [Convert]::ToBase64String($h.ComputeHash($s))}finally{$s.Dispose();$h.Dispose()} }
function Get-Sha512Bytes([byte[]]$Bytes) { $h=[Security.Cryptography.SHA512]::Create();try{return $h.ComputeHash($Bytes)}finally{$h.Dispose()} }
function Write-Utf8([string]$Path,[string]$Text) { [IO.Directory]::CreateDirectory((Split-Path $Path -Parent))|Out-Null;[IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false)) }
function Write-Bytes([string]$Path,[byte[]]$Bytes) { [IO.Directory]::CreateDirectory((Split-Path $Path -Parent))|Out-Null;[IO.File]::WriteAllBytes($Path,$Bytes) }
function Assert-Throws([scriptblock]$Action,[string]$Label){try{&$Action;throw "$Label did not throw."}catch{if($_.Exception.Message-eq"$Label did not throw."){throw}}}
$repository=Split-Path $buildRoot -Parent
$reviewedManifestPath=Join-Path $repository 'third-party-licenses/licenses-manifest.json'
$reviewedManifest=Get-Content -LiteralPath $reviewedManifestPath -Raw | ConvertFrom-Json
if ($reviewedManifest.complete -ne $true) { throw 'Committed release license evidence is not marked complete.' }
$reviewedRoot=Split-Path $reviewedManifestPath -Parent
foreach ($entry in @($reviewedManifest.packages)+@($reviewedManifest.managedPackages)) {
    foreach ($file in $entry.licenseFiles) {
        $path=Join-Path $reviewedRoot ([string]$file.path).Replace('/','\')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Sha $path) -cne [string]$file.sha256) {
            throw "Committed license evidence hash is not checkout-stable: $($file.path)"
        }
    }
}
$root=Join-Path ([IO.Path]::GetTempPath()) "lftp-pilot-license-tests-$([Guid]::NewGuid().ToString('N'))"
try {
    $license=Join-Path $root 'licenses/fixture/LICENSE.txt';$source=Join-Path $root 'sources/fixture/fixture.src.tar.zst'
    Write-Utf8 $license 'fixture license';Write-Utf8 $source 'fixture corresponding source';Write-Utf8 (Join-Path $root 'do-not-stage.txt') 'private extra'
    $package=[ordered]@{name='fixture';version='1-1';filename='fixture-1-1-x86_64.pkg.tar.zst';sha256=('1'*64)}
    $lock=[ordered]@{schema=3;bundleRevision='8';architecture='x64';inventorySha256=('2'*64);packages=@($package)}
    $lockPath=Join-Path $root 'runtime.lock.json';Write-Utf8 $lockPath (($lock|ConvertTo-Json -Depth 10)+"`n")

    $managedArchive=Join-Path $root 'managed-packages/managed.fixture/managed.fixture.1.2.3.nupkg'
    Write-Bytes $managedArchive ([Text.Encoding]::UTF8.GetBytes('managed-fixture-package'))
    $managedRawSha=Get-Sha512 $managedArchive
    $normalizedBytes=[Text.Encoding]::UTF8.GetBytes('nuget-normalized-content-hash')
    $normalizedHash=Get-Sha512Bytes $normalizedBytes
    $managedContentHash=[Convert]::ToBase64String($normalizedHash)
    $managedLockedSha=([BitConverter]::ToString($normalizedHash)).Replace('-','').ToLowerInvariant()
    $managedLicense=Join-Path $root 'licenses/managed.fixture/LICENSE.txt';Write-Utf8 $managedLicense 'managed fixture license'
    $managedSource=Join-Path $root 'sources/managed/managed.fixture.src.zip';Write-Utf8 $managedSource 'managed source fixture'
    $nugetLock=[ordered]@{version=1;dependencies=[ordered]@{'net10.0-windows10.0.26100'=[ordered]@{
        'Managed.Fixture'=[ordered]@{type='Direct';requested='[1.2.3, )';resolved='1.2.3';contentHash=$managedContentHash}
    }}}
    $nugetLockPath=Join-Path $root 'src/Fixture/packages.lock.json';Write-Utf8 $nugetLockPath (($nugetLock|ConvertTo-Json -Depth 10)+"`n")

    $runtimeArchive=Join-Path $root 'managed-packages/runtime/microsoft.netcore.app.runtime.win-x64.10.0.10.nupkg'
    Write-Bytes $runtimeArchive ([Text.Encoding]::UTF8.GetBytes('runtime-pack-fixture'))
    $runtimeContentHash=Get-Base64Sha512 $runtimeArchive;$runtimeSha=Get-Sha512 $runtimeArchive
    $runtimeLicense=Join-Path $root 'licenses/runtime-pack/LICENSE.txt';Write-Utf8 $runtimeLicense 'runtime pack fixture license'
    $packageFolder=Join-Path $root 'nuget'
    $restoredRoot=Join-Path $packageFolder 'microsoft.netcore.app.runtime.win-x64/10.0.10'
    $restoredPackage=Join-Path $restoredRoot 'microsoft.netcore.app.runtime.win-x64.10.0.10.nupkg'
    Write-Bytes $restoredPackage ([IO.File]::ReadAllBytes($runtimeArchive))
    Write-Utf8 (Join-Path $restoredRoot 'microsoft.netcore.app.runtime.win-x64.10.0.10.nupkg.sha512') $runtimeContentHash
    $runtimePackLock=[ordered]@{schema=1;sdkVersion='10.0.302';runtimeIdentifier='win-x64';packs=@([ordered]@{
        frameworkReference='Microsoft.NETCore.App';name='Microsoft.NETCore.App.Runtime.win-x64';version='10.0.10';contentHash=$runtimeContentHash;sha512=$runtimeSha
    })}
    $runtimePackLockPath=Join-Path $root 'dotnet-runtime-packs.lock.json';Write-Utf8 $runtimePackLockPath (($runtimePackLock|ConvertTo-Json -Depth 10)+"`n")
    $globalPath=Join-Path $root 'global.json';Write-Utf8 $globalPath (([ordered]@{sdk=[ordered]@{version='10.0.302'}}|ConvertTo-Json -Depth 5)+"`n")
    $frameworks=[ordered]@{};$frameworks['net10.0-windows10.0.26100.0']=[ordered]@{
        downloadDependencies=@([ordered]@{name='Microsoft.NETCore.App.Runtime.win-x64';version='[10.0.10, 10.0.10]'})
        frameworkReferences=[ordered]@{'Microsoft.NETCore.App'=[ordered]@{privateAssets='all'}}
    }
    $folders=[ordered]@{};$folders[($packageFolder.TrimEnd('\')+'\')]=[ordered]@{}
    $assets=[ordered]@{version=3;packageFolders=$folders;project=[ordered]@{restore=[ordered]@{projectName='Fixture'};frameworks=$frameworks}}
    $assetsPath=Join-Path $root 'src/Fixture/obj/project.assets.json';Write-Utf8 $assetsPath (($assets|ConvertTo-Json -Depth 12)+"`n")

    $evidence=[ordered]@{schema=3;complete=$true;bundle=[ordered]@{schema=3;bundleRevision='8';architecture='x64';inventorySha256=('2'*64)};packages=@(
        [ordered]@{name='fixture';version='1-1';filename='fixture-1-1-x86_64.pkg.tar.zst';sha256=('1'*64);
            licenseFiles=@([ordered]@{path='licenses/fixture/LICENSE.txt';sha256=Get-Sha $license});
            correspondingSource=[ordered]@{kind='archive';path='sources/fixture/fixture.src.tar.zst';url='https://repo.msys2.org/sources/fixture.src.tar.zst';sha256=Get-Sha $source}}
    );managedPackages=@(
        [ordered]@{name='Managed.Fixture';version='1.2.3';sha512=$managedLockedSha;category='nuget';
            licenseFiles=@([ordered]@{path='licenses/managed.fixture/LICENSE.txt';sha256=Get-Sha $managedLicense});
            distributionArchive=[ordered]@{kind='nupkg';path='managed-packages/managed.fixture/managed.fixture.1.2.3.nupkg';url='https://api.nuget.org/v3-flatcontainer/managed.fixture/1.2.3/managed.fixture.1.2.3.nupkg';sha512=$managedRawSha};
            reviewedObligations=[ordered]@{sourceCodeRequired=$true;reviewNote='Fixture obligations independently reviewed.'};
            correspondingSource=[ordered]@{kind='archive';path='sources/managed/managed.fixture.src.zip';url='https://github.com/nativepapaya/license-fixtures/releases/download/v1/managed.fixture.src.zip';sha256=Get-Sha $managedSource}},
        [ordered]@{name='Microsoft.NETCore.App.Runtime.win-x64';version='10.0.10';sha512=$runtimeSha;category='dotnet-runtime-pack';
            licenseFiles=@([ordered]@{path='licenses/runtime-pack/LICENSE.txt';sha256=Get-Sha $runtimeLicense});
            distributionArchive=[ordered]@{kind='nupkg';path='managed-packages/runtime/microsoft.netcore.app.runtime.win-x64.10.0.10.nupkg';url='https://api.nuget.org/v3-flatcontainer/microsoft.netcore.app.runtime.win-x64/10.0.10/microsoft.netcore.app.runtime.win-x64.10.0.10.nupkg';sha512=$runtimeSha};
            reviewedObligations=[ordered]@{sourceCodeRequired=$false;reviewNote='Fixture obligations independently reviewed.'}}
    )}
    $manifest=Join-Path $root 'licenses-manifest.json';Write-Utf8 $manifest (($evidence|ConvertTo-Json -Depth 15)+"`n")
    $common=@{RuntimeLockPath=$lockPath;LicenseManifestPath=$manifest;NuGetLockPath=@($nugetLockPath);ProjectAssetsPath=@($assetsPath);RuntimePackLockPath=$runtimePackLockPath;GlobalJsonPath=$globalPath}
    & (Join-Path $buildRoot 'Test-LicenseEvidence.ps1') @common|Out-Null
    $archivePath=Join-Path $root 'evidence.zip'; & (Join-Path $buildRoot 'Stage-LicenseEvidence.ps1') @common -OutputPath $archivePath|Out-Null
    $archive=[IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $names=@($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        $expectedNames=@('licenses-manifest.json','licenses/fixture/LICENSE.txt','sources/fixture/fixture.src.tar.zst',
            'licenses/managed.fixture/LICENSE.txt','managed-packages/managed.fixture/managed.fixture.1.2.3.nupkg',
            'sources/managed/managed.fixture.src.zip',
            'licenses/runtime-pack/LICENSE.txt','managed-packages/runtime/microsoft.netcore.app.runtime.win-x64.10.0.10.nupkg')
        if ($names.Count -ne $expectedNames.Count -or $names -contains 'do-not-stage.txt' -or @($expectedNames|Where-Object{$_ -notin $names}).Count) {
            throw "Evidence staging did not use the exact allowlisted set: $($names -join ', ')"
        }
    } finally {$archive.Dispose()}
    $archivePath2=Join-Path $root 'evidence-2.zip'; & (Join-Path $buildRoot 'Stage-LicenseEvidence.ps1') @common -OutputPath $archivePath2|Out-Null
    if ((Get-Sha $archivePath) -cne (Get-Sha $archivePath2)) { throw 'Evidence archive staging is not deterministic.' }
    $evidence.packages[0].correspondingSource.url='https://127.0.0.1/fixture.src.tar.zst';Write-Utf8 $manifest (($evidence|ConvertTo-Json -Depth 15)+"`n")
    Assert-Throws {& (Join-Path $buildRoot 'Test-LicenseEvidence.ps1') @common} 'Private source URL'
    $evidence.packages[0].correspondingSource.url='https://repo.msys2.org/sources/fixture.src.tar.zst';$evidence.managedPackages=@();Write-Utf8 $manifest (($evidence|ConvertTo-Json -Depth 15)+"`n")
    Assert-Throws {& (Join-Path $buildRoot 'Test-LicenseEvidence.ps1') @common} 'Missing managed evidence'
}
finally {if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}}
'License/source evidence staging tests passed.'
