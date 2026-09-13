
$counter = Get-Content .\counter.txt
$counter = [convert]::ToInt32($counter)
$counter++
$counter | Out-file -FilePath .\counter.txt
$counter = "{0:0000}" -f $counter
$now = Get-Date -Format "yyyy-MM-dd";
$now = "export const BuildVersion = '[" + $now  + "-" + $counter + "]';"
Write-Host $now
$now | Out-file -Encoding ASCII -FilePath ..\nodeseteditor.client\src\version.ts