<div align="center">

<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/banner-ko.png" alt="MeshProtect — 뜯어 가도, 쓸 수 없다." width="100%">

[English](README.md) · [日本語](README.ja.md) · [简体中文](README.zh-CN.md) · **한국어**

[![Unity 2022.3](https://img.shields.io/badge/Unity-2022.3-222222?style=flat-square&logo=unity&logoColor=white)](https://unity.com/)
[![VRChat Avatar SDK3](https://img.shields.io/badge/VRChat-Avatar%20SDK3-00acc1?style=flat-square)](https://vrchat.com/)
[![lilToon 외 6종](https://img.shields.io/badge/lilToon-%EC%99%B8%206%EC%A2%85-e91e63?style=flat-square)](#지원-셰이더)
[![BOOTH](https://img.shields.io/badge/BOOTH-%EB%AC%B4%EB%A3%8C-fc4d50?style=flat-square)](https://humuhumuhumu.booth.pm/items/8731588)
[![라이선스](https://img.shields.io/badge/%EB%9D%BC%EC%9D%B4%EC%84%A0%EC%8A%A4-MIT-607d8b?style=flat-square)](LICENSE)

</div>

---

누군가 당신의 아바타를 게임 파일에서 뜯어내 Blender 로 열어 보면, 서로 무관한 점의 집합만
남아 있습니다. 이 도구가 하려는 일은 그게 전부입니다.

MeshProtect 는 업로드되는 길에 메시를 흐트러뜨리고, 그것을 되돌리는 셰이더를 함께 넣습니다.
되돌릴 수 있는 사람은 Expression 메뉴에서 6자리 비밀번호를 입력한 착용자뿐입니다.
**당신의 프로젝트는 건드리지 않습니다.** 보호는 SDK 가 만드는 임시 복사본에만 적용되므로,
컴포넌트를 떼어내면 다음 업로드는 평범한 아바타로 돌아가고, 씬 쪽은 무슨 일이 있었는지조차
알지 못합니다.

<div align="center">
<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/locked-ko.png" alt="비밀번호가 없으면 아바타가 아예 그려지지 않고, 입력하면 정상으로 돌아온다" width="92%">
<br><sub>잠긴 상태에서는 아예 그려지지 않습니다 — 흐트러진 모습이 남들 앞에 나오지 않습니다.</sub>
</div>

## 설치

[BOOTH](https://humuhumuhumu.booth.pm/items/8731588) 에서 `.unitypackage` 를 받아 임포트하거나
(무료입니다), 이 리포지토리를 UPM / VPM 패키지로 추가하세요.

```
https://github.com/edonamea/MeshProtect.git
```

Unity 2022.3 · VRChat Avatar SDK3 · lilToon 2.x · PC용 아바타
인스펙터는 **日本語 / English / 简体中文 / 한국어** 를 지원하며, 에디터 언어를 따라갑니다.

## 네 단계

1. 아바타 루트 선택 → `Add Component` → `MeshProtect / Mesh Protect Root`
2. **「비밀번호 생성」** 을 누릅니다 — 6자리, 각 자리 1~8. 직접 1~6자리를 입력해도 됩니다.
   **반드시 적어 두세요**: VRChat 의 비밀번호 저장은 PC 단위라, 다른 PC 에서는 다시 입력해야 합니다
3. 평소처럼 Build & Publish
4. VRChat 에서: Expressions → **Unlock** → 자리마다 숫자 선택

> [!WARNING]
> 업로드 전에 「이 컴포넌트는 클라이언트에서 제거됩니다」라는 빨간 오류가 나오지만, 사실이고
> 무해하며 예상된 동작입니다 — 빌드 시 SDK 가 만드는 임시 복사본에서 제거되기 때문입니다.
> **Auto Fix 만은 절대 누르지 마세요.** 씬에서 컴포넌트째로 삭제되고, 비밀번호도 함께 사라집니다.

<div align="center">
<img src="https://raw.githubusercontent.com/edonamea/MeshProtect/main/.github/images/inspector.png" alt="Mesh Protect Root 인스펙터" width="60%">
<br><sub>인터페이스는 이게 전부입니다. 베이크 버튼도, 씬의 두 번째 아바타도, 모델을 고친 뒤 다시 할 일도 없습니다.</sub>
</div>

의상·PhysBone·Modular Avatar·VRCFury·메시 최적화 도구와 순서 상관없이 함께 쓸 수 있습니다
(보호가 그것들 전부의 뒤에 실행되기 때문입니다). Quest 업로드는 그대로 통과하므로 두 빌드
사이에서 체크를 켰다 껐다 할 필요도 없습니다.

## 지원 셰이더

| | |
|---|---|
| **lilToon 2.x** | 네이티브 지원. lilToon 공식 확장 지점을 통하므로 포크도 패치도 하지 않고, lilToon 업데이트로 깨지지 않습니다. |
| **Poiyomi Toon** · **Xiexe's Toon Shader** · **UnityChanToonShader** · **Sunao Shader** · **GTAvaToon** · **blackbody** | 자동 이식: 생성된 해제 처리를 해당 셰이더의 복사본에 패스 단위로 옮깁니다. 텍스트에 패치를 대는 것이 아니라 시맨틱으로 위치를 찾습니다. |
| **lilSSAO** · **lilSSRT** | 병합 패밀리: 둘 다 lilToon 커스텀 셰이더 패밀리이므로, 폴더째 아바타용 생성 위치로 복제한 뒤 그곳에 해제 처리를 통합합니다. 호스트 셰이더 폴더의 사본이 생성 패밀리와 나란히 프로젝트에 남습니다. |

이식을 완전히 하지 못한 머티리얼은 깨지는 대신 **보호 없이 그대로 업로드**되고 Console 에
이름이 표시됩니다. 지원 목록 밖 셰이더는 손대지 않습니다 — 망가뜨리지 않습니다.

## 무엇을 막을 수 있는가

| | |
|---|---|
| 뜯어낸 메시를 Blender 에서 열기 | **막힘** — 셰이더부터 분석해야 합니다 |
| 분해하고 텍스처를 갈아 되팔기 | **막힘** — 위와 같습니다 |
| 기존 해제 스크립트를 돌리기 | **막힘** — 알고리즘이 같은 아바타는 둘도 없습니다 |
| DCC 툴로 메시를 다시 익스포트 | **막힘** — 정점의 동일성은 UV0 의 원본 비트 패턴입니다 |
| FX 컨트롤러에서 아바타가 뭘 하는지 읽기 | **막힘** — 레이어·스테이트·블렌드 트리·클립 모두 이름이 바뀝니다 |
| 메시·머티리얼·오브젝트 이름 읽기 | **막힘** — 아바타 자신의 알고리즘에서 생성된 이름입니다 |
| 이 아바타의 셰이더를 손으로 분석 | 몇 시간이 걸리고, **다음 아바타에는 아무 도움이 되지 않습니다** |

목표는 「뚫리지 않는 것」이 아니라 **뚫는 일이 규모의 경제를 갖지 못하게 하는 것**입니다. 비용은
모델 한 대마다 처음부터 다시 지불되고, 그것이 바로 스크립트화된·오프라인의·대량 처리 공격을
걷어냅니다 — 모델이 한번 돌기 시작하면 실제로 벌어지는 일의 대부분이 그것입니다.

텍스처는 보호되지 않고, 같은 인스턴스에 있는 사람이 착용 중인 복원된 메시를 GPU 에서 캡처하는
것도 막지 못합니다. 이 두 한계와 그 이유는 [기술자료.txt](기술자료.txt) 에 적혀 있습니다 —
판매물에 쓰기 전에 읽어 보시길 권합니다.

## 스스로 검증합니다

이 분야의 도구는 조용히 실패하고, 사용자는 게임 안에서야 알아차리는 것이 보통입니다. 이 도구는
업로드가 끝나기 전에 검증합니다. 생성된 셰이더를 C# 암호 처리와 **GPU 위에서** 대조하고, 구운
메시를 블렌드셰이프 프레임까지 포함해 정점 단위로 되돌려 확인하며, 잠금 해제 메뉴와 전송 비트가
갖춰졌는지 확인하고, 모든 컴포넌트의 모든 직렬화 필드를 훑어 원본 메시가 어디에서도 도달할 수
없음을 증명합니다. 완전히 처리하지 못한 것은 그대로 두고 Console 에 이름을 남기므로, 빌드가
조용히 망가진 아바타를 내보내는 일은 없습니다.

## 작동 원리

- **변위는 저장하지 않고 생성합니다.** 각 정점은 비밀번호와 그 정점 자신의 동일성에서 얻은
  해시만큼 접선과 법선 방향으로 밀려납니다. 메시 안에는 되읽을 계수가 없습니다.
- **알고리즘은 아바타마다 다릅니다.** 비밀번호를 설정하면 새 해시 프로그램이 조립되고, 상수가
  컴파일되어 들어간 셰이더 패밀리가 통째로 생성됩니다. 프로퍼티·파라미터·에셋 이름도 생성되므로
  업로드된 아바타에는 이 도구의 이름조차 남지 않습니다.
- **잠긴 아바타는 터지는 게 아니라 보이지 않습니다.** 모든 정점이 한 점으로 붕괴해 어떤 패스에서도
  래스터화되지 않습니다. 비밀번호가 틀려도 마찬가지입니다 — 정상이거나 없거나 둘 중 하나이고,
  흐트러진 모습이 남들 앞에 나오지 않습니다.
- **전 구간 정수 연산**이므로 C# 과 HLSL 이 어떤 GPU 에서도 비트 단위로 일치합니다.

**오버헤드:** UV 채널 하나(TEXCOORD6) · Expression 파라미터 예산 24 bit · 버텍스 셰이더 명령
몇 개로 **아바타의 Performance Rank 는 변하지 않습니다** · 알고리즘 생성 시 한 번, 약 3.5초.
비밀번호 변경과 재업로드에는 추가 비용이 없습니다.

## 그 밖에

📄 **[기술자료.txt](기술자료.txt)** — 기술 설명, 보호 범위, 패널 항목별 설명, 문제 해결
📘 **[읽어주세요.txt](읽어주세요.txt)** — 패키지에 동봉된 설명서
📋 **[CHANGELOG.md](CHANGELOG.md)** — 변경 내역
💬 질문과 버그 리포트 — [BOOTH 상품 페이지](https://humuhumuhumu.booth.pm/items/8731588)의 문의 양식

## 라이선스

[MIT 라이선스](LICENSE)입니다. 도구 자체를 포함해 사용·수정·재배포·판매가 모두 자유이며,
복제본에는 저작권 표시와 라이선스 고지를 남겨 주세요. 이 도구로 만든 것을 판매하는 것도 자유입니다.

`Shaders/Templates` 의 lilToon 템플릿은
[lilxyzw/lilToon](https://github.com/lilxyzw/lilToon)(MIT)에서 파생되었고 그 라이선스를 따릅니다.
변위 방식은 [rygo6/GTAvaCrypt](https://github.com/rygo6/GTAvaCrypt) 와
[lilxyzw/AvaterEncryption](https://github.com/lilxyzw/AvaterEncryption)(모두 MIT)의 선행 작업을
따릅니다. 전체 귀속 표시는 [NOTICE.md](NOTICE.md) 에 있습니다.
