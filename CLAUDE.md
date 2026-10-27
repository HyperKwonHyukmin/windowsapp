# DeskKeeper

늘 떠 있는 가벼운 Windows 유틸리티(런처·임시 파일 점검·폴더 분석). 결정과 단계는 `PLAN.md`에 있다.

## 규칙
- 상주 프로그램(`DeskKeeper.Tray`)에는 무거운 기능을 넣지 않는다. 파일 스캔이나 큰 목록은 `DeskKeeper.Analyzer`에서 처리한다.
- 상주 프로그램에 기능을 더하면 유휴 메모리를 다시 잰다(목표 25MB 이하). 넘으면 고친다.
- 파일 삭제는 반드시 휴지통으로 보낸다(`FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`). 바로 지우는 코드는 쓰지 않는다.
- 설정은 `%APPDATA%\DeskKeeper\`에 둔다. PC마다 다른 경로는 저장소에 넣지 않는다.
- 결정이 바뀌면 `PLAN.md`를 고친다.

## 빌드
```powershell
dotnet publish src/DeskKeeper.Tray -c Release -o publish
```
- 실행 중인 `DeskKeeper.exe`가 있으면 publish 가 파일 잠김으로 실패한다. 트레이에서 종료한 뒤 빌드한다.
- 아이콘을 바꾸려면 `pwsh tools/make-icon.ps1` 실행 후 다시 빌드한다.
- 메모리 측정: 실행 6초 뒤 `Get-Process DeskKeeper`의 `PrivateMemorySize64`를 본다. 작업 메모리(WS)는 정리 직후 2MB 정도로 내려가서 기준으로 쓰지 않는다.
