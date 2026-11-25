# Toolbar UI smoke: use built-in --shot mode (auto selection + demo
# annotations + 6s auto exit) and PrintWindow-grab the overlay window.
$ErrorActionPreference = 'Stop'

Get-Process PlainSnip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

Start-Process "C:\Users\richard\Develop\PlainSnip\dist\PlainSnip.exe" -ArgumentList '--shot'
Start-Sleep -Seconds 3   # overlay painted, toolbar rendered

$py = "C:\Users\richard\.workbuddy\binaries\python\versions\3.13.12\python.exe"
$skill = "C:\Users\richard\.workbuddy\skills\win-ui-selfscreenshot\scripts\shot_window.py"
$out = "C:\Users\richard\Develop\PlainSnip\.workbuddy\shots\toolbar_dark.png"
& $py $skill "PlainSnip 截图" $out
Write-Output "exit=$LASTEXITCODE"
