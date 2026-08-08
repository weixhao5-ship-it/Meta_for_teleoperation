**한국어** | [English](SCHEMA.md)

# 데이터 스키마 (TCP NDJSON)

헤드셋이 송신하는 스트림의 와이어 포맷 명세.
**한 줄 = UTF-8 JSON 객체 1개 = 한 프레임** (`\n` 종단, NDJSON).

## 공통 규약

| 항목 | 값 |
| --- | --- |
| 좌표계 | `robot_world` — **오른손(RIGHT-HANDED)**, **+X 전방 / +Y 왼쪽 / +Z 위(하늘)**, 단위 **미터** (FLU: Forward-Left-Up, ROS REP-103 스타일) |
| 회전 | 쿼터니언, 배열 순서 **`[x, y, z, w]`** (⚠ `[w,x,y,z]` 아님) |
| 위치 | `[x, y, z]` |
| 숫자 포맷 | 소수점 이하 최대 6자리, 소수점은 항상 `.` (InvariantCulture) |
| 배열 순서 | `hands`, `controllers` 모두 항상 **2개 고정, `[left, right]` 순서** |

> **좌표계 변환은 송신측(퍼블리셔)이 수행한다.** 헤드셋 내부(Unity)는 왼손·Y-up
> 좌표계지만, 직렬화 시점에 아래 변환으로 오른손 FLU 프레임으로 바꿔 내보낸다:
>
> - 위치: `(x, y, z)_unity → (z, −x, y)`
> - 쿼터니언: `(x, y, z, w)_unity → (−z, x, −y, w)`
>
> **+X 전방**은 XR 트래킹 공간의 초기 정면 — 앱 시작(또는 리센터) 시점에 사용자가
> 바라보던 방향이다. 수신측(로봇 등)은 추가 변환 없이 그대로 사용하면 된다.

### 생략 규칙 (파서가 반드시 처리)

- `"tracked": false` 인 객체(hmd / 손 / 컨트롤러)는 **그 아래 키가 아예 없다**
  (예: 미추적 손은 `{"side":"left","tracked":false}` 로 끝 — `palm`/`landmarks` 없음).
- `"valid": false` 인 pose(palm / landmark / grip / aim)는 `position`/`rotation` 이
  **생략**된다 (0으로 채워지지 않음).

## 전체 예시 (실제 직렬화 순서)

```json
{
  "timestamp": 12.345,
  "frame": 1024,
  "coordinate_system": "robot_world",
  "hmd": { "tracked": true, "position": [0, 0, 1.6], "rotation": [0, 0, 0, 1] },
  "hands": [
    {
      "side": "left",
      "tracked": true,
      "palm": { "valid": true, "position": [0, 0, 0], "rotation": [0, 0, 0, 1] },
      "landmarks": [
        { "i": 0, "valid": true, "position": [0, 0, 0], "rotation": [0, 0, 0, 1] }
        /* … 21개, i = 0..20 … */
      ]
    },
    { "side": "right", "tracked": false }
  ],
  "controllers": [
    {
      "side": "left",
      "tracked": true,
      "controller": { "valid": true, "position": [0, 0, 0], "rotation": [0, 0, 0, 1] },
      "grip_trigger": 0.85,
      "ctrl_trigger": 0.12,
      "thumbstick": [0.0, -0.7],
      "buttons": { "primary": false, "secondary": false, "menu": false, "thumbstick": false }
    },
    { "side": "right", "tracked": false }
  ]
}
```

- `timestamp` — 앱 시작 후 경과 초 (double).
- `frame` — 캡처 시점의 렌더 프레임 번호 (int).

## `hmd`

| 필드 | 타입 | 설명 |
| --- | --- | --- |
| `tracked` | bool | false면 아래 두 필드 생략 |
| `position` | [x,y,z] | 헤드셋 위치 (m) |
| `rotation` | [x,y,z,w] | 헤드셋 회전 |

---

## `hands` — 손 (hand tracking)

항상 2개, `[left, right]`. 각 요소:

| 필드 | 타입 | 설명 |
| --- | --- | --- |
| `side` | `"left"` \| `"right"` | 어느 손인지 |
| `tracked` | bool | false면 이하 전부 생략 |
| `palm` | pose | 손바닥 중앙 pose. `{valid, position, rotation}` |
| `landmarks` | 배열(21) | 21개 랜드마크, 아래 표 순서 고정 |

- **`palm` 정의**: OpenXR `Palm` 관절을 우선 사용. 런타임이 제공하지 않으면
  위치 = `midpoint(Wrist, MiddleProximal)`, 회전 = Wrist 회전으로 대체.
- 각 landmark 요소는 `{ "i": <0..20>, "valid": bool, "position": …, "rotation": … }`.
  관절 하나가 그 프레임에 누락되면 그 요소만 `{"i": n, "valid": false}`.

### 랜드마크 순서 (MediaPipe 21점 표준)

| i | 이름 | ← OpenXR 관절 | i | 이름 | ← OpenXR 관절 |
|---|---|---|---|---|---|
| 0 | WRIST | Wrist | 11 | MIDDLE_DIP | MiddleDistal |
| 1 | THUMB_CMC | ThumbMetacarpal | 12 | MIDDLE_TIP | MiddleTip |
| 2 | THUMB_MCP | ThumbProximal | 13 | RING_MCP | RingProximal |
| 3 | THUMB_IP | ThumbDistal | 14 | RING_PIP | RingIntermediate |
| 4 | THUMB_TIP | ThumbTip | 15 | RING_DIP | RingDistal |
| 5 | INDEX_MCP | IndexProximal | 16 | RING_TIP | RingTip |
| 6 | INDEX_PIP | IndexIntermediate | 17 | PINKY_MCP | LittleProximal |
| 7 | INDEX_DIP | IndexDistal | 18 | PINKY_PIP | LittleIntermediate |
| 8 | INDEX_TIP | IndexTip | 19 | PINKY_DIP | LittleDistal |
| 9 | MIDDLE_MCP | MiddleProximal | 20 | PINKY_TIP | LittleTip |
| 10 | MIDDLE_PIP | MiddleIntermediate | | | |

(OpenXR 26관절 중 MediaPipe에 대응하는 21개만 사용. 검지~새끼의 Metacarpal과
Palm 관절은 랜드마크에서 제외 — palm은 별도 필드로 제공.)

---

## `controllers` — 컨트롤러

항상 2개, `[left, right]`. **`hands`와 완전히 독립된 소스**로, 모드 전환이 아니다.
한 프레임에 손·컨트롤러가 **둘 다 / 하나만 / 둘 다 아님** 모두 가능하며,
수신측이 `tracked`인 쪽을 골라 쓰면 된다.

각 요소 (tracked=true일 때, 직렬화 순서대로):

| 필드 | 타입 | 설명 |
| --- | --- | --- |
| `side` | `"left"` \| `"right"` | 어느 손 컨트롤러인지 |
| `tracked` | bool | false면 이하 전부 생략 |
| `controller` | pose | **컨트롤러 손잡이의 6DoF pose** (hands의 palm에 대응) |
| `grip_trigger` | float 0..1 | **검지(전면) 트리거** — 그리퍼 제어 입력으로 사용 |
| `ctrl_trigger` | float 0..1 | **그립(측면) 트리거** 아날로그 |
| `thumbstick` | [x, y] | 각 축 -1..1 |
| `buttons` | 객체 | `{primary, secondary, menu, thumbstick}` 모두 bool |

- `buttons.primary` = A/X, `buttons.secondary` = B/Y, `buttons.thumbstick` = 스틱 클릭.

### ⚠ 필드 이름은 용도 기준 — 물리 입력과의 대응

필드 이름은 OpenXR 명명이 아니라 **텔레옵 용도 기준**이다. `grip_trigger`는
"그리퍼(gripper)를 조작하는 트리거"라는 역할 이름이지, 하드웨어의 그립 버튼이 아니다:

| JSON 필드 | 물리 입력 | OpenXR path |
| --- | --- | --- |
| `controller` | 손잡이 pose | `/input/grip/pose` |
| `grip_trigger` | **검지(전면) 트리거** | `/input/trigger/value` |
| `ctrl_trigger` | **그립(측면) 트리거** | `/input/squeeze/value` |

### tracked / valid 판정 규칙

- **`controller` pose 획득 실패 → 컨트롤러 전체가 `tracked: false`** (손잡이 pose가
  없으면 추종할 대상이 없으므로).
- 인터랙션 프로파일에 없는 입력(스틱 등)은 `0` / `false`로 채워진다.
- 앱 설정으로 컨트롤러 샘플링을 꺼도(`publishControllers = false`) `controllers`
  필드는 사라지지 않고 양쪽 모두 `tracked: false`로 나온다 — **스키마 형태는 항상 동일**.

---

## 파싱 분기 요약

| 조건 | 출력 | 주의 |
| --- | --- | --- |
| HMD 미추적 | `"hmd": {"tracked": false}` | `position`/`rotation` 키 없음 |
| 손 미추적 | `{"side": …, "tracked": false}` | `palm`/`landmarks` 키 없음 |
| palm/landmark `valid:false` | `{"valid": false}` / `{"i": n, "valid": false}` | `position`/`rotation` 키 없음 |
| 컨트롤러 미추적 | `{"side": …, "tracked": false}` | `controller` 이하 전부 키 없음 |
