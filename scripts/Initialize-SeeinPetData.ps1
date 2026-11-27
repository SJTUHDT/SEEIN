[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$Source = (Join-Path $env:USERPROFILE '.dsh\dsh-pet'),
    [switch]$MuteWhispers
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Destination) { throw 'The desktop pet migration destination already exists.' }
$origin = Get-Item -LiteralPath $Source
$sourceFiles = @(Get-ChildItem -LiteralPath $Source -Recurse -Force)
if (($origin.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    ($sourceFiles | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })) {
    throw 'A desktop pet data path uses a link and requires review before migration.'
}
$legacy = Join-Path $Source 'main-config.jsonc'
if (!(Test-Path -LiteralPath $legacy -PathType Leaf)) { throw 'The existing desktop pet configuration is missing.' }
$secretFile = 'C:\dafeiyu\private\pet-bridge-token.txt'
if (!(Test-Path -LiteralPath $secretFile -PathType Leaf)) { throw 'The Dafeiyu bridge credential is missing.' }
$bridgeCredential = [IO.File]::ReadAllText($secretFile).Trim()
if ([string]::IsNullOrWhiteSpace($bridgeCredential) -or $bridgeCredential -match '\s') { throw 'The local bridge credential is invalid.' }
$bridgeModels = Invoke-RestMethod -Method Get -TimeoutSec 6 -Uri 'http://127.0.0.1:26199/v1/models' -Headers @{ Authorization = 'Bearer ' + $bridgeCredential }
if (!($bridgeModels.data | Where-Object id -eq 'dafeiyu')) { throw 'The Dafeiyu bridge did not advertise its configured model.' }

$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
$directory = New-Item -ItemType Directory -Path (Join-Path $Destination 'dsh-pet') -Force
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($owner)
$acl.SetAccessRuleProtection($true, $false)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
Set-Acl -LiteralPath $directory.FullName -AclObject $acl
foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
    if ($item.Name -in 'main-config.jsonc','standalone-ai.json','logs.txt','runtime.json' -or $item.Name -like '*.bak') { continue }
    Copy-Item -LiteralPath $item.FullName -Destination $directory.FullName -Recurse
}
$config = Get-Content -LiteralPath $legacy -Raw | ConvertFrom-Json -AsHashtable -Depth 64
$model = @{ provider = 'standalone'; model = 'dafeiyu' }
$config.chatModel = $model
$config.whisperModel = $model
foreach ($pet in $config.pets) {
    if ($pet.chatModel.provider -eq 'dafeiyu') { $pet.chatModel = $model }
    if ($pet.whisperModel.provider -eq 'dafeiyu') { $pet.whisperModel = $model }
    if ($MuteWhispers) { $pet.whisperEnabled = $false }
}
$ai = [ordered]@{
    protocol = 'openai-compatible'
    baseUrl = 'http://127.0.0.1:26199/v1'
    model = 'dafeiyu'
    balanceProvider = ''
    enabled = $true
    automaticWhispers = !$MuteWhispers -and [bool]($config.pets | Where-Object { $_.whisperEnabled -eq $true })
    whisperIntervalSec = 300
    maxTokens = 1024
    timeoutMs = 60000
    apiKey = $bridgeCredential
    deepseekApiKey = ''
}
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $directory.FullName 'standalone-ai.json'), ($ai | ConvertTo-Json -Compress -Depth 8), $utf8)
[IO.File]::WriteAllText((Join-Path $directory.FullName 'main-config.jsonc'), ($config | ConvertTo-Json -Depth 64), $utf8)
Write-Host 'Desktop pet settings and history staged. The Dafeiyu model was confirmed through the existing bridge.'

