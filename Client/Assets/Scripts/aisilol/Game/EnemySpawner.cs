using System.Collections;
using aisilol.Common;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace aisilol.Game
{
    public class EnemySpawner : MonoBehaviour
    {
        private const float MaxSpawnDistance = 200;     // 지평선이 보이는 경우 무한히 멀어지지 않도록 제한하는 최대 거리
        private const float StopDistance = 0.1f;        // 플레이어와 이 거리 이내면 이동하지 않음
        private const int InitialCapacity = 1024;

        private const int SpawnCountPerFrame = 2;
        
        private GameObject _enemyPrefab;
        private GameObject _player;
        private Camera _gameCamera;

        private Vector3 _enemyBoundsSize;
        private float _enemyHalfHeight;

        private static readonly Vector3[] ViewportCorners = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), };

        private TransformAccessArray _enemyTransforms;
        private NativeList<float> _enemySpeeds;
        private JobHandle _moveJobHandle;

        private float _spawnEnemyDistance = 0;
        
        public int EnemyCount { get; private set; }
        
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

            var meshBounds = meshFilter.sharedMesh.bounds;
            var scale = enemyPrefab.transform.localScale;

            _enemyBoundsSize = Vector3.Scale(meshBounds.size, scale);
            _enemyHalfHeight = _enemyBoundsSize.y * 0.5f;

            if (_enemyHalfHeight <= 0)
            {
                Log.Error($"Enemy의 높이가 0 입니다. [Size : ${meshBounds.size} / Scale : ${scale}]", enemyPrefab);
                return;
            }

            _player = player;
            
            _gameCamera = cameraComponent;
            gameCamera.onZoomChanged += CalcSpawnEnemyDistance;
            
            _enemyPrefab = enemyPrefab;

            _enemyTransforms = new TransformAccessArray(InitialCapacity);
            _enemySpeeds = new NativeList<float>(InitialCapacity, Allocator.Persistent);

            StartCoroutine(Spawn());
        }

        private IEnumerator Spawn()
        {
            while (true)
            {
                if (!_player)
                    break;

                yield return new WaitForEndOfFrame();

                // Spawn Distance가 아직 계산 전이면 Skip
                if (Mathf.Approximately(_spawnEnemyDistance, MaxSpawnDistance) || _spawnEnemyDistance == 0)
                {
                    CalcSpawnEnemyDistance();
                    continue;
                }
                
                for (var index = 0; index < SpawnCountPerFrame; index++)
                {
                    if (!Utility.TryInstantiate(_enemyPrefab, out var enemyGameObject, this))
                        break;

                    enemyGameObject.transform.position = CalcSpawnPosition();

                    if (!Utility.TryGetComponent<Enemy>(enemyGameObject, out var enemy, enemyGameObject))
                        break;

                    _enemyTransforms.Add(enemyGameObject.transform);
                    _enemySpeeds.Add(enemy.CurrentSpeed);

                    EnemyCount++;
                }

            }
        }

        // 이동 Job을 Update에서 예약하고 LateUpdate에서 완료해, 그 사이 메인 스레드 작업과 병렬로 실행
        private void Update()
        {
            if (!_enemyTransforms.isCreated || !_player)
                return;

            var job = new MoveJob
            {
                Speeds = _enemySpeeds.AsArray(),
                target = _player.transform.position,
                deltaTime = Time.deltaTime,
            };

            _moveJobHandle = job.Schedule(_enemyTransforms);
        }

        // Spawn 코루틴(프레임 끝)에서 TransformAccessArray를 수정하기 전에 반드시 완료되어야 함
        private void LateUpdate()
        {
            _moveJobHandle.Complete();
        }

        private void OnDestroy()
        {
            _moveJobHandle.Complete();

            if (_enemyTransforms.isCreated)
                _enemyTransforms.Dispose();

            if (_enemySpeeds.IsCreated)
                _enemySpeeds.Dispose();
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
            var angle = Random.Range(0, Mathf.PI * 2);
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
        private struct MoveJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<float> Speeds;
            public Vector3 target;
            public float deltaTime;

            public void Execute(int index, TransformAccess transform)
            {
                var position = transform.position;
                var direction = target - position;
                var sqrDistance = direction.sqrMagnitude;
                if (sqrDistance < StopDistance * StopDistance)
                    return;

                transform.position = position + direction * (Speeds[index] * deltaTime / Mathf.Sqrt(sqrDistance));
            }
        }
    }
}
