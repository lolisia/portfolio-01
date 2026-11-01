using aisilol.Common;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace aisilol.Game
{
    // Enemy는 GameObject 없이 위치/속도 데이터로만 관리하고, GPU Instancing으로 일괄 렌더링
    // Enemy Prefab은 Mesh / Material / Scale / 이동 속도 설정을 읽어오는 템플릿으로만 사용
    public class EnemySpawner : MonoBehaviour
    {
        private const float MaxSpawnDistance = 200;     // 지평선이 보이는 경우 무한히 멀어지지 않도록 제한하는 최대 거리
        private const float StopDistance = 0.1f;        // 플레이어와 이 거리 이내면 이동하지 않음
        private const int InitialCapacity = 1024;
        private const int MaxInstancesPerDraw = 1023;   // Draw Call 1회당 최대 Instance 수
        private const int MoveJobBatchCount = 64;

        private const int SpawnCountPerFrame = 2;

        private GameObject _player;
        private Camera _gameCamera;

        private Mesh _enemyMesh;
        private RenderParams _renderParams;
        private Quaternion _enemyRotation;
        private Vector3 _enemyScale;
        private Vector2 _enemySpeedRange;
        private float _enemyHalfHeight;

        private static readonly Vector3[] ViewportCorners = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), };

        private NativeList<float3> _enemyPositions;
        private NativeList<float> _enemySpeeds;
        private NativeList<Matrix4x4> _enemyMatrices;
        private JobHandle _moveJobHandle;

        private float _spawnEnemyDistance;

        public int EnemyCount => _enemyPositions.IsCreated ? _enemyPositions.Length : 0;

        public void Initialize(GameObject player, GameCamera gameCamera, GameObject enemyPrefab)
        {
            if (!Utility.IsValid(player, this))
                return;

            if (!Utility.IsValid(gameCamera, this))
                return;

            if (!Utility.IsValid(enemyPrefab, this))
                return;

            if (!Utility.TryGetComponent<Camera>(gameCamera.gameObject, out var cameraComponent, this))
                return;

            if (!Utility.TryGetComponent<MeshFilter>(enemyPrefab, out var meshFilter, enemyPrefab))
                return;

            if (!Utility.IsValid(meshFilter.sharedMesh, enemyPrefab))
                return;

            if (!Utility.TryGetComponent<MeshRenderer>(enemyPrefab, out var meshRenderer, enemyPrefab))
                return;

            if (!Utility.IsValid(meshRenderer.sharedMaterial, enemyPrefab))
                return;

            if (!Utility.TryGetComponent<Enemy>(enemyPrefab, out var enemy, enemyPrefab))
                return;

            var material = meshRenderer.sharedMaterial;
            if (!material.enableInstancing)
            {
                Log.Error($"Enemy Material의 GPU Instancing이 꺼져 있습니다. [Material : {material.name}]", material);
                return;
            }

            var meshBounds = meshFilter.sharedMesh.bounds;
            var enemyTransform = enemyPrefab.transform;

            _enemyScale = enemyTransform.localScale;
            _enemyRotation = enemyTransform.localRotation;
            _enemyHalfHeight = meshBounds.size.y * _enemyScale.y * 0.5f;

            if (_enemyHalfHeight <= 0)
            {
                Log.Error($"Enemy의 높이가 0 입니다. [Size : ${meshBounds.size} / Scale : ${_enemyScale}]", enemyPrefab);
                return;
            }

            _enemyMesh = meshFilter.sharedMesh;
            _enemySpeedRange = enemy.moveSpeed;

            // Enemy는 플레이어를 따라다니며 어디에나 있을 수 있으므로, 전체 배치를 컬링하지 않도록 충분히 큰 Bounds 사용
            _renderParams = new RenderParams(material)
            {
                layer = enemyPrefab.layer,
                shadowCastingMode = meshRenderer.shadowCastingMode,
                receiveShadows = meshRenderer.receiveShadows,
                motionVectorMode = meshRenderer.motionVectorGenerationMode,
                renderingLayerMask = meshRenderer.renderingLayerMask,
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000),
            };

            _player = player;

            _gameCamera = cameraComponent;
            gameCamera.onZoomChanged += CalcSpawnEnemyDistance;

            _enemyPositions = new NativeList<float3>(InitialCapacity, Allocator.Persistent);
            _enemySpeeds = new NativeList<float>(InitialCapacity, Allocator.Persistent);
            _enemyMatrices = new NativeList<Matrix4x4>(InitialCapacity, Allocator.Persistent);
        }

        // 생성(목록 변경) → 이동 Job 예약 순서. Job은 LateUpdate에서 완료되므로 그 사이 메인 스레드 작업과 병렬로 실행
        private void Update()
        {
            if (!_enemyPositions.IsCreated || !_player)
                return;

            Spawn();

            var job = new MoveJob
            {
                positions = _enemyPositions.AsArray(),
                speeds = _enemySpeeds.AsArray(),
                matrices = _enemyMatrices.AsArray(),
                target = _player.transform.position,
                rotation = _enemyRotation,
                scale = _enemyScale,
                deltaTime = Time.deltaTime,
            };

            _moveJobHandle = job.Schedule(_enemyPositions.Length, MoveJobBatchCount);
        }

        private void LateUpdate()
        {
            _moveJobHandle.Complete();

            if (!_enemyMatrices.IsCreated)
                return;

            var matrices = _enemyMatrices.AsArray();
            for (var start = 0; start < matrices.Length; start += MaxInstancesPerDraw)
            {
                var count = Mathf.Min(MaxInstancesPerDraw, matrices.Length - start);
                Graphics.RenderMeshInstanced(_renderParams, _enemyMesh, 0, matrices, count, start);
            }
        }

        private void OnDestroy()
        {
            _moveJobHandle.Complete();

            if (_enemyPositions.IsCreated)
                _enemyPositions.Dispose();

            if (_enemySpeeds.IsCreated)
                _enemySpeeds.Dispose();

            if (_enemyMatrices.IsCreated)
                _enemyMatrices.Dispose();
        }

        private void Spawn()
        {
            // Spawn Distance가 아직 계산 전이면 Skip
            if (Mathf.Approximately(_spawnEnemyDistance, MaxSpawnDistance) || _spawnEnemyDistance == 0)
            {
                CalcSpawnEnemyDistance();
                return;
            }

            for (var index = 0; index < SpawnCountPerFrame; index++)
            {
                _enemyPositions.Add(CalcSpawnPosition());
                _enemySpeeds.Add(UnityEngine.Random.Range(_enemySpeedRange.x, _enemySpeedRange.y));
                _enemyMatrices.Add(default);    // MoveJob에서 매 프레임 채움
            }
        }

        private void CalcSpawnEnemyDistance()
        {
            var origin = _player.transform.position;
            origin.y = _enemyHalfHeight;

            _spawnEnemyDistance = CalcMaxSpawnDistance(origin);
        }

        // 플레이어 기준 랜덤 방향으로, 최대 Spawn 거리만큼 떨어진 지점을 반환
        private Vector3 CalcSpawnPosition()
        {
            var angle = UnityEngine.Random.Range(0, Mathf.PI * 2);
            var direction = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));

            var origin = _player.transform.position;
            origin.y = _enemyHalfHeight;

            return origin + direction * _spawnEnemyDistance;
        }

        // 화면 영역 중 플레이어에게서 가장 먼 지점까지의 거리. 이 거리 밖은 어느 방향이든 화면 밖
        // 화면 영역은 볼록 사각형이므로 가장 먼 지점은 화면 네 모서리 중 하나
        private float CalcMaxSpawnDistance(Vector3 origin)
        {
            var maxSqrDistance = 0f;

            var plane = new Plane(Vector3.up, origin);

            foreach (var corner in ViewportCorners)
            {
                var ray = _gameCamera.ViewportPointToRay(corner);

                // 평면에 닿지 않는 모서리(지평선 위)는 최대 거리까지 보이는 것으로 간주
                if (!plane.Raycast(ray, out var enter))
                    return MaxSpawnDistance;

                var offset = ray.GetPoint(enter) - origin;
                offset.y = 0;
                maxSqrDistance = Mathf.Max(maxSqrDistance, offset.sqrMagnitude);
            }

            return Mathf.Min(Mathf.Sqrt(maxSqrDistance), MaxSpawnDistance);
        }

        [BurstCompile]
        private struct MoveJob : IJobParallelFor
        {
            public NativeArray<float3> positions;
            [ReadOnly] public NativeArray<float> speeds;
            [WriteOnly] public NativeArray<Matrix4x4> matrices;
            public float3 target;
            public quaternion rotation;
            public float3 scale;
            public float deltaTime;

            public void Execute(int index)
            {
                var position = positions[index];
                var direction = target - position;
                direction.y = 0;    // 높이는 Spawn 시점 값을 유지

                var sqrDistance = math.lengthsq(direction);
                if (sqrDistance >= StopDistance * StopDistance)
                {
                    position += direction * (speeds[index] * deltaTime * math.rsqrt(sqrDistance));
                    positions[index] = position;
                }

                matrices[index] = float4x4.TRS(position, rotation, scale);
            }
        }
    }
}
