# Long-shot UI smoke: --scroll mode (auto selection -> long shot state,
# 6s auto exit). PrintWindow-grab overlay window and preview window.
$ErrorActionPreference = 'Stop'

Get-Process PlainSnip -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

Start-Process "C:\Users\richard\Develop\PlainSnip\dist\PlainSnip.exe" -ArgumentList '--scroll'
Start-Sleep -Seconds 3   # long shot entered (400ms after start), frames flowing

$py = "C:\Users\richard\.workbuddy\binaries\python\versions\3.13.12\python.exe"
$skill = "C:\Users\richard\.workbuddy\skills\win-ui-selfscreenshot\scripts\shot_window.py"
$shots = "C:\Users\richard\Develop\PlainSnip\.workbuddy\shots"
& $py $skill "PlainSnip 长截图" "$shots\scroll_preview.png"
Write-Output "preview exit=$LASTEXITCODE"
& $py $skill "PlainSnip 截图" "$shots\scroll_overlay.png"
Write-Output "overlay exit=$LASTEXITCODE"
