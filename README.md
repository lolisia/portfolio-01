# portfolio-01

몰려드는 대량의 적을 자동 공격으로 처치하며 버티는 Unity 쿼터뷰 서바이버 프로토타입.

[![Quality Gate Status](https://sonarcloud.io/api/project_badges/measure?project=lolisia_portfolio-01&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=lolisia_portfolio-01)
[![Coverage](https://sonarcloud.io/api/project_badges/measure?project=lolisia_portfolio-01&metric=coverage)](https://sonarcloud.io/summary/overall?id=lolisia_portfolio-01)
[![Bugs](https://sonarcloud.io/api/project_badges/measure?project=lolisia_portfolio-01&metric=bugs)](https://sonarcloud.io/summary/new_code?id=lolisia_portfolio-01)
[![Code Smells](https://sonarcloud.io/api/project_badges/measure?project=lolisia_portfolio-01&metric=code_smells)](https://sonarcloud.io/summary/new_code?id=lolisia_portfolio-01)
[![Lines of Code](https://sonarcloud.io/api/project_badges/measure?project=lolisia_portfolio-01&metric=ncloc)](https://sonarcloud.io/summary/new_code?id=lolisia_portfolio-01)
[![SonarQube Cloud](https://github.com/lolisia/portfolio-01/actions/workflows/sonarqube.yml/badge.svg)](https://github.com/lolisia/portfolio-01/actions/workflows/sonarqube.yml)
[![Release](https://github.com/lolisia/portfolio-01/actions/workflows/release.yml/badge.svg)](https://github.com/lolisia/portfolio-01/actions/workflows/release.yml)

<!-- <img src="docs/images/gameplay.gif" width="720" alt="Gameplay"> -->

- Unity 6000.6.7f1, URP, Input System
- 다운로드: [Releases](https://github.com/lolisia/portfolio-01/releases/latest) (Windows x64)

## 구현

**Enemy** ([EnemyManager.cs](Client/Assets/Scripts/aisilol/Game/EnemyManager.cs))
- Enemy는 GameObject 없이 `NativeList`에 위치/속도/체력만 저장
- 매 프레임 Burst Job으로 공간 해시 격자([SpatialGrid.cs](Client/Assets/Scripts/aisilol/Game/SpatialGrid.cs))를 만들고, 이동 / 겹침 밀어내기 / 플레이어 접촉 판정이 격자를 공유
- 이동은 위치 이중 버퍼. 이웃 위치를 읽는 동안 덮어쓰지 않음
- Update에서 Job 예약, LateUpdate에서 완료 후 데미지 적용 / 사망 제거(swap-back)를 Burst `IJob`으로 처리
- 렌더링은 아래 렌더링 항목 참고
- 스폰 위치는 카메라 화면 모서리를 지면에 투영해서 항상 화면 밖으로 계산. 초당 생성 수는 레벨에 따라 지수 증가(기본값 * 증가율^(레벨 - 1)), 체력은 30레벨까지 그대로, 이후 레벨에 비례해 증가(배율 = 1 + 0.34 * (레벨 - 30), 100레벨에서 약 50)
- Enemy 수는 고정 상한 없음. 평균 FPS(0.5초 지수 이동 평균)가 기준(기본 60) 아래면 생성 중지
- 한 프레임 생성 수는 상한(기본 100)까지. 넘는 수는 이월하지 않음
- 피격된 Enemy는 0.1초 동안 피격 색으로 표시. 같은 Mesh에 피격 색 Material을 쓰는 렌더링 그룹으로 분리

**렌더링** ([InstanceRenderer.cs](Client/Assets/Scripts/aisilol/Game/InstanceRenderer.cs), [GpuInstanceRenderer.cs](Client/Assets/Scripts/aisilol/Game/GpuInstanceRenderer.cs))
- Enemy / 투사체 / 드롭 / 칼날 공용. GameManager의 `GPU 렌더링` 옵션으로 두 경로 전환. HUD에 현재 경로 표시
- GPU 경로: 행렬을 `GraphicsBuffer`로 올리고 Compute Shader([InstanceCulling.compute](Client/Assets/Resources/Shaders/InstanceCulling.compute))가 시야 판정 + 그룹별 수집 후 `Graphics.RenderMeshIndirect`로 그룹당 1회 렌더링. 보이는 수는 `AsyncGPUReadback`으로 받음
- GPU 경로 Shader([InstancedIndirect.shader](Client/Assets/Resources/Shaders/InstancedIndirect.shader))에서 정점 애니메이션. Enemy는 위치 기반 위상으로 튀어 오르며 찌그러지고, 드롭과 칼날은 회전
- CPU 경로: Burst Job으로 시야 판정 / 그룹 정렬 후 `Graphics.RenderMeshInstanced`로 1023개씩 렌더링 ([InstancedMesh.cs](Client/Assets/Scripts/aisilol/Game/InstancedMesh.cs))
- 그래픽 장치가 없거나(-nographics) Compute Shader를 지원하지 않으면 CPU 경로 사용

**투사체** ([ProjectileSystem.cs](Client/Assets/Scripts/aisilol/Game/ProjectileSystem.cs))
- 사거리 안의 가장 가까운 Enemy로 자동 발사. 투사체도 GameObject 없이 데이터로 처리
- 목표 탐색은 격자에서 플레이어 셀부터 한 겹씩 넓혀 가며 찾고, 남은 겹이 찾은 거리보다 멀면 중단. 검사할 셀이 Enemy 수보다 많으면 전체 순회
- 이동 + 충돌은 Burst `IJobParallelFor`. 한 프레임 이동 구간(선분)으로 판정해서 빠른 투사체도 Enemy를 건너뛰지 않음
- 관통 수만큼 구간 위의 Enemy를 가까운 순서로 맞힘. 밀집할수록 한 발에 여러 마리 처치
- 맞힌 Enemy는 `NativeQueue` 데미지 큐에 기록하고, EnemyManager가 LateUpdate에서 일괄 적용. 데미지를 기록하는 Job끼리는 순서대로 연결

**무기** ([WeaponSystem.cs](Client/Assets/Scripts/aisilol/Game/WeaponSystem.cs))
- 오라: 틱마다 플레이어 주변 반경 안의 Enemy에 피해. 레벨당 범위 +0.35m(1레벨 1.5m), 초당 틱 수 +15%(1레벨 0.5초 간격), 틱당 피해 +0.5%p(1레벨 Damage의 10%)
- 칼날: 플레이어 주위를 도는 칼날에 닿은 Enemy에 초당 피해. 칼날마다 `IJobParallelFor`
- 연쇄 번개: 1초마다 가까운 Enemy를 맞히고, 맞히지 않은 가장 가까운 Enemy로 이어서 튐
- 모두 Enemy 격자를 검색하는 Burst Job. 강화(Aura / Orbit / Chain)로 획득

**드롭** ([PickupSystem.cs](Client/Assets/Scripts/aisilol/Game/PickupSystem.cs))
- Enemy 사망 위치에 경험치 Object, 확률로 HP 포션 드롭. 경험치는 Object를 획득해야 증가
- 포션 드롭 확률은 플레이어 HP 비율 커브. HP가 낮을수록 높음
- 자석 범위 안에 들어오면 플레이어에게 끌려와 획득. 이동 / 획득 판정은 Burst Job
- 레벨업하면 맵에 남은 경험치 Object를 모두 끌어옴
- 가까이 모인 같은 단계의 경험치 Object 10개를 다음 단계 1개로 병합(1 → 10 → 100 ...). 셀 + 단계로 정렬 후 묶음 처리
- 병합 범위는 0단계 0.5m, 단계마다 3배. 단계별로 색과 크기가 다름
- 경험치 Object 수 상한(기본 5000)을 넘으면 병합 범위를 넓혀 다시 병합하고, 그래도 넘으면 위치와 관계없이 같은 단계끼리 병합. 가치는 유지
- 드롭 생성 / 획득 / 제거는 Burst `IJob`

**성장** ([PlayerStats.cs](Client/Assets/Scripts/aisilol/Game/PlayerStats.cs), [UpgradeDefinition.cs](Client/Assets/Scripts/aisilol/Game/UpgradeDefinition.cs))
- 레벨업 시 일시 정지 후 강화 3택 1. HUD의 Auto Upgrade 체크박스를 켜면 멈추지 않고 무작위 선택지 중 하나를 바로 적용
- 강화는 ScriptableObject 에셋([Data/Upgrades](Client/Assets/Data/Upgrades)). 능력치 / 적용 방식(Add, Percent) / 값 / 최대 레벨만 입력
- 능력치 = (기본값 + Add 합) * (1 + Percent 합)

**게임 진행** ([GameManager.cs](Client/Assets/Scripts/aisilol/Game/GameManager.cs), [GameHud.cs](Client/Assets/Scripts/aisilol/Game/GameHud.cs))
- Playing / LevelUp / GameOver 상태. 접촉 피해는 무적 시간 0.5초 단위
- HUD(체력/경험치 바, 선택지, 결과 화면)는 uGUI를 코드로 구성

**Floor** ([FloorManager.cs](Client/Assets/Scripts/aisilol/Game/FloorManager.cs))
- 카메라 Frustum에 걸리는 타일만 배치, 벗어난 타일은 Pool로 재사용

**공용** ([Utility.cs](Client/Assets/Scripts/aisilol/Common/Utility.cs), [Log.cs](Client/Assets/Scripts/aisilol/Common/Log.cs))
- `TryInstantiate` / `TryGetComponent` 실패 시 Caller 정보(파일:라인)를 포함해 로그 출력

```mermaid
flowchart LR
    G[Grid Job] --> M[Enemy 이동 Job]
    G --> C[접촉 Job]
    G --> P[투사체 이동/충돌 Job]
    G --> W[무기 Job]
    P --> Q[데미지 큐]
    W --> Q
    Q --> D[LateUpdate: 데미지 적용 / 제거]
    M --> D
    D --> K[드롭 생성 / 병합]
    D --> R[GPU 컬링 + RenderMeshIndirect]
```

## 테스트 / CI

- PlayMode 테스트 193개: [Client/Assets/Tests](Client/Assets/Tests). Job은 단위 테스트, 시스템은 빈 씬에서 통합 테스트
- push / PR 시 테스트 + 커버리지 + SonarQube Cloud 분석 ([sonarqube.yml](.github/workflows/sonarqube.yml))
- CI는 -nographics라 GPU 렌더링 테스트는 건너뛰고 GpuInstanceRenderer.cs는 커버리지에서 제외. 로컬 GPU 환경에서 실행
- `v*` 태그 push 시 GameCI로 Windows 빌드 후 Release 업로드 ([release.yml](.github/workflows/release.yml))

## 실행

`Client` 폴더를 Unity 6000.6.7f1로 열고 `Assets/Scenes/GameScene.unity` 실행. 공격은 자동.

| 입력 | 키 |
| --- | --- |
| 이동 | WASD / 방향키 / 왼쪽 스틱 |
| 줌 | 마우스 휠 |
| 강화 선택 | 1 / 2 / 3, 마우스 클릭 |
