[CmdletBinding()]
param([ValidateSet('Core','PublicFull')][string]$Profile='PublicFull',[string]$Version='2.2.1',[switch]$Force,[switch]$KeepExpanded)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release=Join-Path $project "artifacts/releases/v$Version"
$work=Join-Path $project ('.build/publish/transaction-'+[guid]::NewGuid().ToString('N'))
$stage=Join-Path $work 'package'
$destination=Join-Path $release "Seein-win-x64-$Version-$Profile"
if((Test-Path -LiteralPath $destination) -and !$Force){throw 'Release already exists; use -Force to replace it.'}
if($Version -ne '2.2.1'){throw 'This publisher targets the 2.2.1 feature distribution.'}
. (Join-Path $PSScriptRoot 'BuildEnvironment.ps1')
$dotnet=Get-SeeinDotnet
Initialize-SeeinDependencies -ForPublish:$($Profile -ne 'Core')
New-Item -ItemType Directory -Path $stage,$release -Force|Out-Null
function Publish([string]$Source,[string]$Target,[bool]$SelfContained=$false){
    & $dotnet publish $Source -c Release -r win-x64 --self-contained $SelfContained.ToString().ToLowerInvariant() -p:PublishSingleFile=false -o $Target -v:q
    if($LASTEXITCODE){throw "Publish failed: $Source"}
}
function CopyTree([string]$Source,[string]$Target){New-Item -ItemType Directory -Path $Target -Force|Out-Null;& robocopy $Source $Target /E /XJ /R:1 /W:1 /NFL /NDL /NJH /NJS|Out-Null;if($LASTEXITCODE -ge 8){throw "Copy failed: $Source"}}
function MergeBase([string]$Source,[string]$Target){
    foreach($file in Get-ChildItem -LiteralPath $Source -File -Recurse){
        $relative=[IO.Path]::GetRelativePath($Source,$file.FullName);$to=Join-Path $Target $relative
        if(Test-Path -LiteralPath $to){if((Get-FileHash -LiteralPath $to).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Base dependency collision: $relative"}}
        else{New-Item -ItemType Directory -Path (Split-Path $to) -Force|Out-Null;Copy-Item -LiteralPath $file.FullName -Destination $to}
    }
}
$app=Join-Path $stage 'App'
Publish (Join-Path $project 'src/Seein.Tray/Seein.Tray.csproj') $app $true
foreach($name in 'Service','Bootstrap'){$output=Join-Path $work $name;Publish (Join-Path $project "src/Seein.$name/Seein.$name.csproj") $output $true;MergeBase $output $app}
# A tiny shared launcher keeps all .NET 10 hosts on the one self-contained runtime in App.
foreach($hostName in 'Seein.Tray.exe','seedisplay.exe','seepet.exe'){Copy-Item -LiteralPath (Join-Path $app 'Seein.Bootstrap.exe') -Destination (Join-Path $app $hostName) -Force}
Remove-Item -LiteralPath (Join-Path $app 'Seein.Bootstrap.exe')
Set-Content -LiteralPath (Join-Path $app 'SEEIN.modular') -Value '2.2.1' -Encoding ascii
$tool=Join-Path $work 'packager';Publish (Join-Path $project 'src/Seein.PackageTool/Seein.PackageTool.csproj') $tool $true
$packager=Join-Path $tool 'Seein.PackageTool.exe'
$key=Join-Path $env:LOCALAPPDATA 'SeeinPublicBuild/keys/module-publisher.dpapi'
& $packager key $key (Join-Path $app 'module-publisher.pem');if($LASTEXITCODE){throw 'Publisher key could not be loaded.'}
# Protect the encrypted publishing key as well as its DPAPI user binding.
$keyDir=Get-Item -LiteralPath (Split-Path $key);$acl=Get-Acl -LiteralPath $keyDir.FullName;$acl.SetAccessRuleProtection($true,$false)
foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))};Set-Acl -LiteralPath $keyDir.FullName -AclObject $acl
$packages=Join-Path $work 'plugin-packages';New-Item -ItemType Directory -Path $packages|Out-Null
$definitions=@(Get-Content -LiteralPath (Join-Path $project 'config/plugin-definitions.json') -Raw -Encoding UTF8|ConvertFrom-Json)
if($Profile -eq 'Core'){$definitions=@()}
foreach($module in $definitions){
    $moduleRoot=Join-Path $stage ('Plugins/'+$module.Id)
    $source=if([IO.Path]::IsPathFullyQualified($module.Project)){$module.Project}else{Join-Path $project $module.Project}
    Publish $source $moduleRoot
    if($module.Id -eq 'companion-pet'){
        $pet=Join-Path $work 'PetHost';Publish (Join-Path $project 'src/Seein.PetHost/Seein.PetHost.csproj') $pet
        MergeBase $pet $moduleRoot
        & (Join-Path $PSScriptRoot 'Prepare-NativePet.ps1') -Destination $moduleRoot|Out-Null
        # Launcher and runtime are owned by the base; the module carries only the engine and assets.
        Remove-Item -LiteralPath (Join-Path $moduleRoot 'seepet.exe') -ErrorAction SilentlyContinue
    }
    if($module.Id -eq 'display'){
        CopyTree (Join-Path $project 'themes') (Join-Path $moduleRoot 'themes')
        $fps=Join-Path $project '.build/deps/PresentMon-2.6.0/PresentMon.exe'
        if((Get-FileHash -LiteralPath $fps).Hash -ne 'B2A706BC6AD475749E3B7E3409263AA1E6906D45BDCF993F6DBC0F660188F1AF'){throw 'FPS helper hash mismatch.'}
        New-Item -ItemType Directory -Path (Join-Path $moduleRoot 'tools') -Force|Out-Null;Copy-Item -LiteralPath $fps -Destination (Join-Path $moduleRoot 'tools/seefps.exe')
        Copy-Item -LiteralPath (Join-Path (Split-Path $fps) 'LICENSE.txt') -Destination (Join-Path $moduleRoot 'tools/PresentMon.LICENSE.txt')
    }
    if($module.Id -eq 'devices-rgb'){
        CopyTree (Join-Path $project '.build/runtime/openrgb') (Join-Path $moduleRoot 'rgb-engine')
        $legacy=Join-Path $moduleRoot 'rgb-engine/Seein.RgbEngine.exe';if(Test-Path -LiteralPath $legacy){Move-Item -LiteralPath $legacy -Destination (Join-Path $moduleRoot 'rgb-engine/seergb.exe')}
        if((Get-FileHash -LiteralPath (Join-Path $moduleRoot 'rgb-engine/seergb.exe')).Hash -ne 'E26AE71162795A8ABCFB90C05C1572A00BA06CA63B886ACA57392A0138963B1E'){throw 'RGB helper hash mismatch.'}
    }
    if($module.Id -in 'capture','efficiency'){
        $native=if($module.Id -eq 'capture'){'snow'}else{'pane'}
        $payload=Join-Path $project ('.build/tool-payload/'+$native)
        foreach($line in Get-Content -LiteralPath (Join-Path $payload 'RUNTIME.sha256')){
            if($line -notmatch '^([0-9a-fA-F]{64}) \*(.+)$'){throw 'Invalid native runtime manifest.'};$hash=$Matches[1];$path=[IO.Path]::GetFullPath((Join-Path $payload $Matches[2]));if(!$path.StartsWith($payload+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path).Hash -ne $hash){throw 'Native runtime hash mismatch.'}
        }
        CopyTree $payload (Join-Path $moduleRoot ('tools/'+$native))
        if($module.Id -eq 'capture'){
            # Keep the right-Alt switch usable without installing the efficiency plugin.
            Copy-Item -LiteralPath (Join-Path $project '.build/tool-payload/pane/Pane.Native.dll') -Destination (Join-Path $moduleRoot 'Pane.Native.dll')
            Copy-Item -LiteralPath (Join-Path $project '.build/deps/Pane-0.1.606/LICENSE') -Destination (Join-Path $moduleRoot 'Pane.Native.LICENSE')
        }
        if($module.Id -eq 'efficiency'){& (Join-Path $PSScriptRoot 'Prepare-Everything.ps1') -Destination $moduleRoot|Out-Null}
    }
    # Only byte-identical base dependencies are omitted. Native ABI/version differences stay isolated.
    foreach($file in Get-ChildItem -LiteralPath $moduleRoot -File -Recurse){
        $relative=[IO.Path]::GetRelativePath($moduleRoot,$file.FullName);$baseFile=Join-Path $app $relative
        if($file.Extension -eq '.pdb' -or ($file.Extension -eq '.dll' -and (Test-Path -LiteralPath $baseFile) -and (Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $baseFile).Hash)){Remove-Item -LiteralPath $file.FullName}
    }
    foreach($dependency in $module.Dependencies){
        $depRoot=Join-Path $stage ('Plugins/'+$dependency)
        foreach($file in Get-ChildItem -LiteralPath $moduleRoot -File -Recurse){
            $relative=[IO.Path]::GetRelativePath($moduleRoot,$file.FullName);$sharedFile=Join-Path $depRoot $relative
            if($file.Extension -eq '.dll' -and (Test-Path -LiteralPath $sharedFile) -and (Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $sharedFile).Hash){Remove-Item -LiteralPath $file.FullName}
        }
    }
    if($module.Id -in 'capture','efficiency'){
        # Both reference exactly one shared adapter and its dependencies.
        $adapter=Join-Path $moduleRoot 'Seein.Desktop.Tools.dll';if(Test-Path -LiteralPath $adapter){Remove-Item -LiteralPath $adapter}
    }
    if($module.Id -in 'companion-workbench','companion-astrbot','companion-qq'){
        foreach($support in @('companion-common','webview-runtime')){
            $depRoot=Join-Path $stage ('Plugins/'+$support)
            foreach($file in Get-ChildItem -LiteralPath $moduleRoot -File -Recurse){$relative=[IO.Path]::GetRelativePath($moduleRoot,$file.FullName);$match=Join-Path $depRoot $relative;if($file.Extension -eq '.dll' -and (Test-Path -LiteralPath $match) -and (Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath $match).Hash){Remove-Item -LiteralPath $file.FullName}}
        }
    }
    $desktop=if($module.desktopAssembly){@{assembly=$module.desktopAssembly;type=$module.desktopType}}else{$null}
    $service=if($module.serviceAssembly){@{assembly=$module.serviceAssembly;type=$module.serviceType}}else{$null}
    $dependencies=@($module.Dependencies|ForEach-Object{@{id=$_;minimumVersion=$Version}})
    $metadata=@{id=$module.Id;name=$module.Name;version=$Version;apiVersion=1;minimumHostVersion='2.2.1';desktop=$desktop;service=$service;dependencies=$dependencies;files=@();private=$false}
    $meta=Join-Path $work ($module.Id+'.json');$metadata|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $meta -Encoding utf8
    & $packager pack $moduleRoot $meta (Join-Path $packages ($module.Id+'-'+$Version+'.seeinplugin')) $key
    if($LASTEXITCODE){throw "Plugin signing failed: $($module.Id)"}
}
if($Profile -ne 'Core'){
    $entries=@(foreach($module in $definitions){
        $package=Join-Path $packages ($module.Id+'-'+$Version+'.seeinplugin');$folder=Join-Path $stage ('Plugins/'+$module.Id)
        @{id=$module.Id;name=$module.Name;group=$module.Group;description=$module.Description;version=$Version;asset=[IO.Path]::GetFileName($package);downloadBytes=(Get-Item -LiteralPath $package).Length;installedBytes=[long](Get-ChildItem -LiteralPath $folder -Recurse -File|Measure-Object Length -Sum).Sum;sha256=(Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant();dependencies=@($module.Dependencies);selectable=[bool]$module.Selectable}
    })
    $catalog=@{schema=1;release='v'+$Version;minimumHostVersion=$Version;groups=@(@{id='display';name='副屏与主题'},@{id='applications';name='应用监控'},@{id='devices';name='设备控制'},@{id='tools';name='小工具'});packages=$entries}
    $catalog|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $release 'plugin-catalog.json') -Encoding utf8
    & $packager sign-file (Join-Path $release 'plugin-catalog.json') $key (Join-Path $release 'plugin-catalog.sig');if($LASTEXITCODE){throw 'Catalog signing failed.'}
}
foreach($name in @('plugin-catalog.json','plugin-catalog.sig')){if(Test-Path -LiteralPath (Join-Path $release $name)){Copy-Item -LiteralPath (Join-Path $release $name) -Destination $app -Force}}
Get-ChildItem -LiteralPath $app -Recurse -Filter '*.pdb' -File|Remove-Item
Copy-Item -LiteralPath (Join-Path $project 'scripts/Install.ps1'),(Join-Path $project 'scripts/Uninstall.ps1'),(Join-Path $project 'README.md'),(Join-Path $project 'THIRD-PARTY-NOTICES.md') -Destination $stage
Set-Content -LiteralPath (Join-Path $stage 'VERSION') -Value $Version -Encoding ascii
Copy-Item -LiteralPath (Join-Path $project 'scripts/Preserve-InstalledPlugins.ps1') -Destination $stage
Set-Content -LiteralPath (Join-Path $stage 'PROFILE') -Value $Profile -Encoding ascii
# Corresponding source archives are release sidecars, not installed runtime files.
if($Profile -ne 'Core'){
    & (Get-SeeinPython) -X utf8 (Join-Path $project 'vendor/tools/export-sources.py') (Join-Path $work 'ToolSources');if($LASTEXITCODE){throw 'Source export failed.'}
    Compress-Archive -Path (Join-Path $work 'ToolSources/*') -DestinationPath (Join-Path $work 'ToolSources.zip') -CompressionLevel Optimal
}
if($Profile -ne 'Core'){
    $sources=Join-Path $release 'SourcePackages';New-Item -ItemType Directory -Path $sources -Force|Out-Null
    $rgbSource=Join-Path $project '.build/deps/OpenRGB-8e16de27858cf2be6af54a33ce003df74cb9fd3e/OpenRGB-1.1-source.zip'
    if((Get-FileHash -LiteralPath $rgbSource).Hash -ne '064FD11D18FF0CC854C2DA0F0B8EE703B4A053089B0CC0CF2E8B925DDE7276D8'){throw 'RGB corresponding source hash mismatch.'}
    Copy-Item -LiteralPath $rgbSource -Destination $sources -Force
    foreach($name in 'FFmpeg-n8.1-source.tar.gz','libvpx-1.17.0-source.tar.gz','toolchain-versions.txt','origin.json'){Copy-Item -LiteralPath (Join-Path $project ('.build/deps/pet-ffmpeg-minimal/'+$name)) -Destination $sources -Force}
    Copy-Item -LiteralPath (Join-Path $project 'scripts/Build-NativePetCodecs.ps1') -Destination $sources -Force
}
$lines=Get-ChildItem -LiteralPath $stage -File -Recurse|Sort-Object FullName|ForEach-Object{((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant())+' *'+[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/')}
[IO.File]::WriteAllLines((Join-Path $stage 'MANIFEST.sha256'),[string[]]$lines,[Text.UTF8Encoding]::new($false))
& $packager verify $stage;if($LASTEXITCODE){throw 'Final module verification failed.'}
if(Test-Path -LiteralPath $destination){$old=Join-Path $work 'previous-release';Move-Item -LiteralPath $destination -Destination $old}
Move-Item -LiteralPath $stage -Destination $destination
$pluginOutput=Join-Path $release ('Plugins-'+$Profile)
if(Test-Path -LiteralPath $pluginOutput){Move-Item -LiteralPath $pluginOutput -Destination (Join-Path $work 'previous-plugin-packages')}
Move-Item -LiteralPath $packages -Destination $pluginOutput
if(Test-Path -LiteralPath (Join-Path $work 'ToolSources.zip')){Copy-Item -LiteralPath (Join-Path $work 'ToolSources.zip') -Destination (Join-Path $release 'ToolSources.zip') -Force}
Write-Output "Release: $destination"
Write-Output "Offline plugin packages: $pluginOutput"
