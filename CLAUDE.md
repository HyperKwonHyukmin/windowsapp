# DeskKeeper

늘 떠 있는 가벼운 Windows 유틸리티(런처·임시 파일 점검·폴더 분석). 결정과 단계는 `PLAN.md`에 있다.

## 규칙
- 상주 프로그램(`DeskKeeper.Tray`)에는 무거운 기능을 넣지 않는다. 파일 스캔이나 큰 목록은 `DeskKeeper.Analyzer`에서 처리한다.
- 상주 프로그램에 기능을 더하면 유휴 메모리를 다시 잰다. 목표는 **켜 둔 채 런처를 안 열었을 때 25MB 이하, 런처·메모를 쓴 뒤 30MB 이하**(Private, 2026-10-29 나눔). 넘으면 고친다.
- 파일 삭제는 반드시 휴지통으로 보낸다(`Analyzer/RecycleBin.cs`의 `SHFileOperation`, `FOF_ALLOWUNDO | FOF_WANTNUKEWARNING`). 바로 지우는 코드는 쓰지 않는다.
- 설정은 `%APPDATA%\DeskKeeper\settings.json` 하나에 둔다. PC마다 다른 경로는 저장소에 넣지 않는다.
- 결정이 바뀌면 `PLAN.md`를 고친다.

## 구조 (2026-10-28)
- `src/DeskKeeper.Core`: `SettingsStore`(쓰기 전에 다시 읽고, 파일에 오류가 있으면 저장 거부), `FolderScanner`, `Categorizer`, `Expiry`(정리 후보 판단)
- `src/DeskKeeper.Tray` → `DeskKeeper.exe`: 트레이, 단축키, 런처, 하루 한 번 점검, `--mark` 명령(보내기 메뉴)
- `src/DeskKeeper.Analyzer` → `DeskKeeper.Analyzer.exe [폴더|파일] | --expired`
- 런처 분류의 묶는 순서는 `Core/LauncherSections.cs` 한 곳에서 정한다(런처와 트레이 메뉴가 같이 쓴다).
- 문구 입력기(`SnippetExpander`) 훅 콜백 안에서는 `SendInput`을 부르지 않는다. `BeginInvoke`로 UI 루프에 넘긴다. 콜백이 오래 걸리면 Windows 가 훅을 뗀다. 우리가 넣은 키(`LLKHF_INJECTED`)는 무시하므로 **keybd_event 로는 자동 시험이 안 된다**. 사람이 직접 쳐서 확인한다.
- 런처에서 대화상자를 띄울 때는 `_modalOpen`을 켠다(`AskName`). 켜지 않으면 비활성 처리로 런처가 닫힌다.
- 메모 모음: 파일 처리는 `Core/Notes.cs`, 화면은 `Tray/NotesView.cs`(런처 안에서 `Tab`으로 바꿈), 마크다운 보기는 `Tray/MarkdownRtf.cs`. Tab 은 대화상자 키라 `OnKeyDown`까지 오지 않으므로 `ProcessCmdKey`에서 받는다. 편집 칸에 포커스가 있으면 `NotesView.ProcessCmdKey`가 먼저 받고 false 를 돌려 런처(화면 전환·Enter·방향키)까지 가지 않게 한다. 런처 `OnKeyDown`(KeyPreview)도 `EditorFocused`면 건드리지 않는다. 메모 폴더(`notesFolder`, 기본 `문서\DeskKeeper\메모`)는 사내 업무 내용이라 저장소에 넣지 않는다.
- 두 프로그램은 settings.json 으로만 주고받는다. 상주 프로그램은 분석 창 종료(`Process.Exited`)만 받아 다시 점검한다.

## 주의
- 상주 프로그램의 기한 점검은 파일 목록을 만들지 않고 개수만 센다(`Expiry.CountExpired`). 목록이 필요한 기능은 분석 창으로 보낸다.
- 다른 프로그램(탐색기)에서 누른 마우스 버튼은 `Control.MouseButtons`로 보이지 않는다. `GetAsyncKeyState`를 쓴다(런처 끌어다 놓기).
- 프로그램이 settings.json 을 저장하면 사용자가 단 주석은 사라진다(System.Text.Json 한계, 그대로 두기로 함).
- 창 크기·높이·여백 같은 픽셀 값은 `S(100% 기준값)`(`LogicalToDeviceUnits`)로 쓴다. 글꼴(pt)만 배율을 따라 커지면 125~200% 화면에서 글자가 잘린다.
- UI 글꼴은 `Segoe UI`를 쓴다. 맑은 고딕은 경로의 `\`를 `₩`로 그린다. 한글은 글꼴 연결로 맑은 고딕이 자동으로 쓰인다.
- 런처는 셸 아이콘(`ShellIconCache`)을 쓰므로 한 번 열고 나면 상주 메모리가 약 22MB가 된다(2026-10-28 측정, 40회 반복에도 GDI 개체 수 변화 없음). 메모 화면(서식 보기 RichTextBox)까지 쓰면 25.3~27.5MB 다(2026-10-29). 런처에 기능을 더할 때는 "쓴 뒤 30MB" 여유를 먼저 본다.
- 설치 직후 첫 런처 열기는 느려서, 자동 시험에서 `Alt+Space` 뒤 0.7초 만에 키를 보내면 창이 준비되기 전에 들어간다. 첫 회 결과는 버리고 다시 찍는다.
- 화면 복사로 런처를 찍을 때 스크립트의 `FindWindow("DeskKeeper 런처")`가 창을 못 찾은 적이 있다(원인 미확인, 2026-10-29). 단축키로 띄운 뒤 `GetForegroundWindow`를 찍으면 된다.
- 화면 모양은 `Graphics.CopyFromScreen`으로 창 영역을 찍어 확인할 수 있다(브라우저 자동화가 막힌 이 PC에서도 동작).
- 흐린 글자색은 `Color.Gray`(대비 3.9:1) 대신 `(96,96,96)` 이상 진하게 쓴다(흰 배경에서 4.5:1 이상).
- 브라우저 기반 화면 확인(Playwright)은 이 PC에서 안 된다. 위의 화면 복사로 대신하고, 마우스 조작이 필요한 동작은 사용자에게 확인을 부탁한다.

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
