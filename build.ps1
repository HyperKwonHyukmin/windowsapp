# DeskKeeper 빌드·설치: 두 프로그램을 설치 폴더에 publish 하고 상주 프로그램을 다시 띄운다.
# 사용법: pwsh ./build.ps1            (기본 설치 폴더 %LOCALAPPDATA%\Programs\DeskKeeper)
#         pwsh ./build.ps1 -NoStart   (빌드만, 실행 안 함)
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\DeskKeeper'),
    [switch]$NoStart
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# 실행 중이면 exe 가 잠겨 덮어쓸 수 없으므로 먼저 끈다
Get-Process DeskKeeper, DeskKeeper.Analyzer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

foreach ($proj in 'src/DeskKeeper.Tray', 'src/DeskKeeper.Analyzer') {
    dotnet publish $proj -c Release -o $InstallDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패: $proj" }
}

# 자동 시작이 켜져 있으면 새 위치를 가리키게 고친다(예전 위치에 등록돼 있어도 켜진 상태 유지)
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$exe = Join-Path $InstallDir 'DeskKeeper.exe'
if (Get-ItemProperty $runKey -Name DeskKeeper -ErrorAction SilentlyContinue) {
    Set-ItemProperty $runKey -Name DeskKeeper -Value "`"$exe`""
    "자동 시작 경로 갱신: $exe"
}

# 탐색기 우클릭 → 보내기 메뉴 바로가기(이미 있으면 새 경로로 덮어씀)
$sendTo = [Environment]::GetFolderPath('SendTo')
$shell = New-Object -ComObject WScript.Shell
$shortcuts = @(
    @{ Name = 'DeskKeeper 임시 표시'; Target = $exe; Args = '--mark' },
    @{ Name = 'DeskKeeper 폴더 분석'; Target = (Join-Path $InstallDir 'DeskKeeper.Analyzer.exe'); Args = '' }
)
foreach ($s in $shortcuts) {
    $lnk = $shell.CreateShortcut((Join-Path $sendTo "$($s.Name).lnk"))
    $lnk.TargetPath = $s.Target
    $lnk.Arguments = $s.Args
    $lnk.IconLocation = "$exe,0"
    $lnk.Save()
}
"보내기 메뉴 바로가기: $sendTo"

"설치 완료: $InstallDir"
if (-not $NoStart) { Start-Process $exe }
