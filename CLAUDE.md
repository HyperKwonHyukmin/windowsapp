# DeskKeeper

늘 떠 있는 가벼운 Windows 유틸리티(런처·임시 파일 점검·폴더 분석). 결정과 단계는 `PLAN.md`에 있다.

## 규칙
- 상주 프로그램(`DeskKeeper.Tray`)에는 무거운 기능을 넣지 않는다. 파일 스캔이나 큰 목록은 `DeskKeeper.Analyzer`에서 처리한다.
- 상주 프로그램에 기능을 더하면 유휴 메모리를 다시 잰다(목표 25MB 이하). 넘으면 고친다.
- 파일 삭제는 반드시 휴지통으로 보낸다(`Analyzer/RecycleBin.cs`의 `SHFileOperation`, `FOF_ALLOWUNDO | FOF_WANTNUKEWARNING`). 바로 지우는 코드는 쓰지 않는다.
- 설정은 `%APPDATA%\DeskKeeper\settings.json` 하나에 둔다. PC마다 다른 경로는 저장소에 넣지 않는다.
- 결정이 바뀌면 `PLAN.md`를 고친다.

## 구조 (2026-10-28)
- `src/DeskKeeper.Core`: `SettingsStore`(쓰기 전에 다시 읽고, 파일에 오류가 있으면 저장 거부), `FolderScanner`, `Categorizer`, `Expiry`(정리 후보 판단)
- `src/DeskKeeper.Tray` → `DeskKeeper.exe`: 트레이, 단축키, 런처, 하루 한 번 점검, `--mark` 명령(보내기 메뉴)
- `src/DeskKeeper.Analyzer` → `DeskKeeper.Analyzer.exe [폴더|파일] | --expired`
- 두 프로그램은 settings.json 으로만 주고받는다. 상주 프로그램은 분석 창 종료(`Process.Exited`)만 받아 다시 점검한다.

## 주의
- 상주 프로그램의 기한 점검은 파일 목록을 만들지 않고 개수만 센다(`Expiry.CountExpired`). 목록이 필요한 기능은 분석 창으로 보낸다.
- 다른 프로그램(탐색기)에서 누른 마우스 버튼은 `Control.MouseButtons`로 보이지 않는다. `GetAsyncKeyState`를 쓴다(런처 끌어다 놓기).
- 프로그램이 settings.json 을 저장하면 사용자가 단 주석은 사라진다(System.Text.Json 한계, 그대로 두기로 함).
- 이 PC에서는 화면 자동 확인이 안 된다. UI 변경은 빌드와 로직 시험까지 하고, 화면은 사용자에게 확인을 부탁한다.

## 빌드
```powershell
pwsh ./build.ps1          # 두 exe 를 %LOCALAPPDATA%\Programs\DeskKeeper 에 설치하고 상주 프로그램 재시작
pwsh ./build.ps1 -NoStart # 설치만
dotnet build DeskKeeper.sln -c Release   # 컴파일 확인만
```
- `build.ps1`은 실행 중인 DeskKeeper 를 먼저 끄고(exe 잠김 방지), 자동 시작이 켜져 있으면 Run 값을 설치 경로로 고친다.
- 상주 프로그램은 같은 폴더의 `DeskKeeper.Analyzer.exe`를 실행하므로 두 exe 는 항상 같은 폴더에 있어야 한다.
- 아이콘을 바꾸려면 `pwsh tools/make-icon.ps1` 실행 후 다시 빌드한다.
- 메모리 측정: 실행 6초 뒤 `Get-Process DeskKeeper`의 `PrivateMemorySize64`를 본다. 작업 메모리(WS)는 정리 직후 2MB 정도로 내려가서 기준으로 쓰지 않는다.
