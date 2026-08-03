**한국어** | [English](README_ENG.md)

# Meta_with_hands

**Meta Quest(Horizon OS)용 Unity 앱.** Passthrough 화면 위에 손(hand tracking)을
시각화하고, HMD·양손(21 landmark)·컨트롤러의 pose/입력 상태를 매 프레임
TCP로 PC에 실시간 스트리밍한다. 헤드셋이 서버, PC가 클라이언트다.

```
Meta Quest (Unity APK)  ──TCP :5555 (NDJSON)──▶  PC (subscriber / 로봇 제어 등)
```

## 전송 방식

| 항목 | 값 |
| --- | --- |
| 프로토콜 | **TCP 서버** — 헤드셋이 포트를 열고, PC가 `tcp://<헤드셋IP>:5555`로 접속 |
| 포트 | `5555` (Inspector에서 변경 가능) |
| 포맷 | **NDJSON** — 한 줄에 JSON 1개, `\n` 종단, UTF-8 |
| 주기 | **매 렌더 프레임** (Quest ≈ 72 Hz). `maxPublishHz > 0`로 상한 설정 가능 (기본 0 = 무제한) |
| 시맨틱 | **최신값 브로드캐스트** — 백그라운드 송신 스레드가 접속한 모든 클라이언트에 가장 최근 프레임만 전송. 느린 클라이언트가 있어도 스트림이 밀리지 않고, 끊긴 클라이언트는 자동 제거 |
| 발행 보장 | 모두 미추적인 프레임도 건너뛰지 않고 발행 (`tracked: false`로 표시) |

퍼블리셔(`PoseFramePublisher`)는 `SampleScene`의 `PosePublisher` 오브젝트로 씬에
이미 배선돼 있어 수동 작업이 필요 없고, 갓 빌드한 APK가 바로 송신을 시작한다.
필요한 권한(`INTERNET`, `com.oculus.permission.HAND_TRACKING`)은 빌드 시
매니페스트에 자동 주입된다.

**수신 테스트** (PC, 표준 라이브러리만 사용):

```bash
python Tools/subscriber.py --host <헤드셋IP> --port 5555
```

헤드셋 IP는 기기의 Wi-Fi 설정에서 확인하고, PC와 같은 네트워크에 있어야 한다.

## 데이터 스키마

한 줄 = 한 프레임. `timestamp` / `frame` / `hmd` / `hands`(손, 21 landmark) /
`controllers`(컨트롤러)로 구성된다. 필드별 정의·좌표계·생략 규칙은:

**→ [SCHEMA.md](SCHEMA.md)**

(Galaxy_with_hands 저장소와 **동일한 스키마**를 쓴다 — 수신측 코드는 두 헤드셋을
구분 없이 그대로 재사용할 수 있다.)

## APK 빌드

### 필수 설치 (머신에 직접)

| 필요한 것 | 비고 |
| --- | --- |
| **Unity Editor `6000.2.12f1`** | 이 프로젝트의 고정 버전 (Unity Hub로 설치) |
| **Android Build Support 모듈** | 위 에디터에 Hub로 추가. OpenJDK + Android SDK/NDK가 번들되므로 별도 설치 불필요 |
| **`adb`** (Android platform-tools) | APK를 기기에 설치하고 로그를 보는 데 필요 (빌드 자체에는 불필요). Ubuntu/Debian은 `sudo apt install adb`. Android Build Support에 번들된 `<Editor>/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`를 PATH에 추가해 써도 된다 |

그 외 프로젝트 설정(패키지, 씬, XR 설정, 권한)은 **전부 repo에 포함**돼 있어 클론 후
Unity로 열면 자동 복원된다. 첫 오픈 시 패키지 임포트가 끝나면 한 번만 확인:
**Project Settings ▸ XR Plug-in Management ▸ Android**에서 `OpenXR` 로더와
`Hand Tracking Subsystem` + `Meta Quest Support` + `AR Session`/`AR Camera`(Meta)
기능이 켜져 있는지.

### 빌드 방법

- **에디터 메뉴**: `Build ▸ Android APK` → `Build/<productName>.apk`
- **배치 모드** (에디터 UI 없이):
  ```bash
  Unity -quit -batchmode -nographics -projectPath . \
        -buildTarget Android -executeMethod BuildAndroid.Build \
        -output Build/Meta_hand.apk
  ```
- **두 APK 한 번에** (권장):
  ```bash
  UNITY=~/Unity/Hub/Editor/6000.2.12f1/Editor/Unity ./Tools/build_all.sh
  # → Build/Meta_hand.apk        (com.dongridong.metahand)
  # → Build/Meta_controller.apk  (com.dongridong.metacontroller)
  ```
  두 APK는 패키지명이 달라 헤드셋에 **나란히 설치**된다. 제품 정체성은 빌드 인자
  (`-productName` / `-applicationIdentifier`)로 주입되고 빌드 후 원복되므로
  `ProjectSettings.asset`은 더러워지지 않는다.

### 기기에 설치

1. Quest **개발자 모드** 활성화(Meta Horizon 모바일 앱), PC에 `adb` 준비,
   USB(또는 무선 adb) 연결.
2. 서명은 키 미지정 시 **디버그 서명이 자동 적용** → sideload에 별도 키 불필요.
   ```bash
   adb install -r Build/Meta_hand.apk
   ```
3. 첫 실행 시 **손 추적 권한** 다이얼로그 허용.

## 자매 저장소

같은 스키마·구성의 **Samsung Galaxy XR(Android XR)판**: `Galaxy_with_hands`
(github.com/dongridong/Galaxy_with_hands). Unity XR 설정(플러그인·패키지)만 기기에
맞게 다르고, 전송 방식과 와이어 포맷은 동일하다.
