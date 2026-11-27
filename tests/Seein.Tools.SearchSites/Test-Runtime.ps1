$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$buildRoot=Join-Path $root '.build'
$exe=Join-Path $buildRoot 'search-sites-pane\seewin.exe'
$profile=Join-Path $buildRoot 'search-sites-profile'
$pipe='Seein.Tools.SiteValidation.'+[guid]::NewGuid().ToString('N')
$results=[Collections.Generic.List[object]]::new()
function Request($operation,$values=$null){
  $client=[IO.Pipes.NamedPipeClientStream]::new('.',$pipe,[IO.Pipes.PipeDirection]::InOut)
  try{
    $client.Connect(15000)
    $writer=[IO.StreamWriter]::new($client,[Text.UTF8Encoding]::new($false),4096,$true);$writer.AutoFlush=$true
    $reader=[IO.StreamReader]::new($client,[Text.Encoding]::UTF8,$false,4096,$true)
    $id=[guid]::NewGuid().ToString('N')
    $writer.WriteLine((@{id=$id;version=1;operation=$operation;values=$values}|ConvertTo-Json -Depth 30 -Compress))
    $read=$reader.ReadLineAsync()
    if(-not $read.Wait(20000)){throw 'Pipe response timed out'}
    $r=$read.Result|ConvertFrom-Json
    if($r.id -ne $id -or -not $r.success){throw "Request failed: $($r|ConvertTo-Json -Depth 5 -Compress)"}
    return $r
  }finally{$client.Dispose()}
}
function Start-Candidate {
  $start=[Diagnostics.ProcessStartInfo]::new($exe,'--autostart');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
  $start.WorkingDirectory=Split-Path $exe
  $start.Environment['SEEIN_TOOL_PIPE']=$pipe;$start.Environment['SEEIN_SHELL_PIPE']=$pipe+'.shell'
  $start.Environment['SEEIN_TOOL_DATA']=$profile;$start.Environment['PANE_DATA_DIR']=$profile
  $start.Environment['PANE_INSTANCE']=$pipe;$start.Environment['SEEIN_SEARCH_SITE_TEST']='1'
  return [Diagnostics.Process]::Start($start)
}
function Check($name,$ok,$value){
  $results.Add(@{name=$name;passed=[bool]$ok;actual=$value})
  if(-not $ok){throw "Failed: $name : $value"}
}
$candidate=Start-Candidate
try{
  $initial=Request 'settings.get'
  $sites=@(($initial.fields|Where-Object id -eq 'Launcher.SearchEngines').value|Where-Object Keyword -ne 'sitex')
  $sites+=@{Name='示例站点';Keyword='sitex';UrlTemplate='https://example.com/search?q={query}'}
  $null=Request 'settings.apply' @{'Launcher.SearchEngines'=$sites}
  $saved=Request 'settings.get'
  $savedSites=@(($saved.fields|Where-Object id -eq 'Launcher.SearchEngines').value)
  Check 'Added site returned by settings.get' ($savedSites.Keyword -contains 'sitex') $savedSites.Count
  foreach($q in @('sitex','SITEX','示例','bd','gh','b')){
    $rendered=Request 'diagnostics.search-site' @{query=$q}
    $expected=switch -CaseSensitive ($q){'sitex'{'示例站点'} 'SITEX'{'示例站点'} '示例'{'示例站点'} 'bd'{'百度'} 'gh'{'GitHub'} 'b'{'哔哩哔哩'}}
    Check "Rendered site for [$q]" ($rendered.state.sites -contains $expected) ($rendered.state.sites -join ',')
  }
  $chosen=Request 'diagnostics.search-site' @{query='示例';choose=$true}
  Check 'Choosing name match fills keyword mode' ($chosen.state.query -ceq 'sitex ') $chosen.state.query
  foreach($q in @('sitex 测试内容','SITEX　测试内容',"sitex`t测试内容")){
    $searched=Request 'diagnostics.search-site' @{query=$q}
    Check 'Keyword search parses ordinary/fullwidth/tab separator' ($searched.state.engine -eq 'sitex') $q
  }
  $null=Request 'shutdown';Check 'Graceful exit' ($candidate.WaitForExit(10000)) $candidate.Id
  $candidate=Start-Candidate
  $reloaded=Request 'settings.get'
  $reloadedSites=@(($reloaded.fields|Where-Object id -eq 'Launcher.SearchEngines').value)
  Check 'Added site persisted across restart' ($reloadedSites.Keyword -contains 'sitex') $reloadedSites.Count
  $again=Request 'diagnostics.search-site' @{query='sitex'}
  Check 'Restarted launcher renders new site' ($again.state.sites -contains '示例站点') ($again.state.sites -join ',')
}finally{
  if(-not $candidate.HasExited){try{$null=Request 'shutdown';$null=$candidate.WaitForExit(5000)}catch{}}
  $results|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $buildRoot 'search-sites-runtime-results.json') -Encoding utf8
}
$results|ForEach-Object { [pscustomobject]$_ }|Format-Table name,passed,actual -AutoSize
