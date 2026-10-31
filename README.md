# KoEnShow

텍스트 입력란에 포커스가 들어가면 마우스 커서 옆에 현재 한/영 상태를 작은 배지("한" / "A")로 보여주는 Windows 트레이 프로그램.

- 입력란 진입, 포커스 이동, 한/영 전환 시 1.5초 표시 (트레이 메뉴에서 "입력 중 항상 표시" 가능)
- Win32 앱은 시스템 캐럿으로, 브라우저·Electron·UWP 앱은 UI Automation으로 입력 상태 감지
- 배지는 클릭을 통과시키고 포커스를 가져가지 않음

## 다운로드

[Releases](https://github.com/imorangepie20/koEnShow/releases/latest)에서 `KoEnShow.exe`를 받아 실행한다. 설치 과정은 없다.

## 빌드

.NET Framework 4.x 기본 컴파일러만 있으면 된다(별도 SDK 불필요).

```
build.bat
```

`KoEnShow.exe`가 생성된다. 실행하면 트레이에 상주하고, 트레이 아이콘 우클릭 메뉴에서 설정·종료할 수 있다.

## 트레이 메뉴

| 메뉴 | 저장 위치 |
| --- | --- |
| 입력 중 항상 표시 | `HKCU\Software\KoEnShow\AlwaysShow` |
| Windows 시작 시 실행 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\KoEnShow` |
| 종료 | - |

## 한계

- 시스템 캐럿도 UI Automation 정보도 없는 앱(일부 게임, 원격 데스크톱 창)에서는 표시되지 않는다.
- 관리자 권한으로 실행된 창의 IME 상태는 일반 권한으로 읽지 못할 수 있다.
