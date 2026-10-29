using System.Collections.Generic;
using aisilol.Common;
using UnityEngine;

namespace aisilol.Game
{
    public class FloorManager : MonoBehaviour
    {
        private const float FloorY = 0;             // Floor가 놓이는 높이
        private const float MaxViewDistance = 200;  // 지평선이 보이는 경우 무한히 생성하지 않도록 제한하는 최대 거리

        private GameObject _floorPrefab;
        private GameObject _player;
        private Camera _camera;

        private Vector2 _tileSize;  // Floor 한 칸의 XZ 크기

        private readonly Dictionary<Vector2Int, GameObject> _activeTiles = new();
        private readonly Stack<GameObject> _tilePool = new();
        private readonly HashSet<Vector2Int> _visibleIndices = new();
        private readonly List<Vector2Int> _removeIndices = new();
        private readonly Plane[] _frustumPlanes = new Plane[6];
        private readonly Vector3[] _viewportCorners = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), };

        private bool _initialized;

        public void Initialize(GameObject floorPrefab, GameObject player, Camera camera)
        {
            // 공용 Floor 정보 구성
            if (!Utility.IsValid(floorPrefab))
                return;

            if (!Utility.TryGetComponent<MeshFilter>(floorPrefab, out var meshFilter, floorPrefab))
                return;

            if (!Utility.IsValid(meshFilter.sharedMesh, floorPrefab))
                return;

            var meshSize = meshFilter.sharedMesh.bounds.size;
            var scale = floorPrefab.transform.localScale;

            _tileSize = new Vector2(meshSize.x * scale.x, meshSize.z * scale.z);

            if (_tileSize.x <= 0 || _tileSize.y <= 0)
            {
                Log.Error($"{floorPrefab.name}의 크기가 올바르지 않습니다. [MeshSize : ${meshSize}, Scale : ${scale}]", floorPrefab);
                return;
            }

            _floorPrefab = floorPrefab;

            if (!Utility.IsValid(player))
                return;

            _player = player;

            if (!Utility.IsValid(camera))
                return;

            _camera = camera;
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized)
                return;

            if (!_player)
                return;

            if (!_camera)
                return;

            // 카메라에 보이는 영역에 빈 곳이 없도록 floor를 배치. 카메라가 보이지 않는 영역은 생성하지 않음
            if (!TryGetVisibleRange(out var min, out var max))
                return;

            CollectVisibleIndices(min, max);
            ReleaseInvisibleTiles();
            SpawnVisibleTiles();
        }

        // 카메라 시야가 Floor 평면과 만나는 영역을 감싸는 타일 인덱스 범위
        private bool TryGetVisibleRange(out Vector2Int min, out Vector2Int max)
        {
            min = max = default;

            var floorPlane = new Plane(Vector3.up, new Vector3(0, FloorY, 0));
            var maxDistance = Mathf.Min(_camera.farClipPlane, MaxViewDistance);

            var minX = float.MaxValue;
            var minZ = float.MaxValue;
            var maxX = float.MinValue;
            var maxZ = float.MinValue;
            var anyHit = false;

            foreach (var corner in _viewportCorners)
            {
                var ray = _camera.ViewportPointToRay(corner);

                // 평면에 닿지 않거나 너무 먼 경우 최대 거리 지점을 평면에 투영
                var hit = floorPlane.Raycast(ray, out var enter) && enter <= maxDistance;
                var point = ray.GetPoint(hit ? enter : maxDistance);
                anyHit |= hit;

                minX = Mathf.Min(minX, point.x);
                minZ = Mathf.Min(minZ, point.z);
                maxX = Mathf.Max(maxX, point.x);
                maxZ = Mathf.Max(maxZ, point.z);
            }

            // 카메라가 Floor를 전혀 보지 않는 경우
            if (!anyHit)
                return false;

            min = WorldToIndex(minX, minZ);
            max = WorldToIndex(maxX, maxZ);
            return true;
        }

        private void CollectVisibleIndices(Vector2Int min, Vector2Int max)
        {
            _visibleIndices.Clear();
            GeometryUtility.CalculateFrustumPlanes(_camera, _frustumPlanes);

            var size = new Vector3(_tileSize.x, 0.01f, _tileSize.y);
            for (var x = min.x; x <= max.x; ++x)
            {
                for (var z = min.y; z <= max.y; ++z)
                {
                    var index = new Vector2Int(x, z);
                    var bounds = new Bounds(IndexToWorld(index), size);
                    if (GeometryUtility.TestPlanesAABB(_frustumPlanes, bounds))
                        _visibleIndices.Add(index);
                }
            }
        }

        private void ReleaseInvisibleTiles()
        {
            _removeIndices.Clear();
            foreach (var index in _activeTiles.Keys)
            {
                if (!_visibleIndices.Contains(index))
                    _removeIndices.Add(index);
            }

            foreach (var index in _removeIndices)
            {
                var tile = _activeTiles[index];
                _activeTiles.Remove(index);

                if (!tile)
                    continue;

                tile.SetActive(false);
                _tilePool.Push(tile);
            }
        }

        private void SpawnVisibleTiles()
        {
            foreach (var index in _visibleIndices)
            {
                if (_activeTiles.ContainsKey(index))
                    continue;

                if (!TryGetTile(out var tile))
                    return;

                tile.transform.SetPositionAndRotation(IndexToWorld(index), Quaternion.identity);
                tile.SetActive(true);
                _activeTiles.Add(index, tile);
            }
        }

        private bool TryGetTile(out GameObject tile)
        {
            while (_tilePool.Count > 0)
            {
                tile = _tilePool.Pop();
                if (tile)
                    return true;
            }

            if (!Utility.TryInstantiate(_floorPrefab, out tile, this))
                return false;

            tile.transform.SetParent(transform, false);
            return true;
        }

        private Vector2Int WorldToIndex(float x, float z)
        {
            return new Vector2Int(
                Mathf.FloorToInt(x / _tileSize.x + 0.5f),
                Mathf.FloorToInt(z / _tileSize.y + 0.5f));
        }

        private Vector3 IndexToWorld(Vector2Int index)
        {
            return new Vector3(index.x * _tileSize.x, FloorY, index.y * _tileSize.y);
        }
    }
}
