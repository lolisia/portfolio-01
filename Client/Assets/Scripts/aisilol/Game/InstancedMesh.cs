using aisilol.Common;
using Unity.Collections;
using UnityEngine;

namespace aisilol.Game
{
    // Prefab의 Mesh / Material / Transform 설정으로 GameObject 없이 GPU Instancing 렌더링
    internal sealed class InstancedMesh
    {
        private const int MaxInstancesPerDraw = 1023;   // Draw Call 1회당 최대 Instance 수

        private readonly Mesh _mesh;
        private readonly RenderParams _renderParams;

        public Quaternion Rotation { get; }
        public Vector3 Scale { get; }
        public Vector3 Size { get; }    // Scale이 적용된 Mesh 크기
        public float BoundsRadius { get; }  // Scale 1 기준, 원점에서 Mesh 전체를 감싸는 구의 반지름. 시야 판정에 사용
        public Material Material => _renderParams.material;

        private InstancedMesh(Mesh mesh, RenderParams renderParams, Transform transform)
        {
            _mesh = mesh;
            _renderParams = renderParams;
            Rotation = transform.localRotation;
            Scale = transform.localScale;
            Size = Vector3.Scale(mesh.bounds.size, Scale);
            BoundsRadius = mesh.bounds.center.magnitude + mesh.bounds.extents.magnitude;
        }

        private InstancedMesh(InstancedMesh source, Material material)
        {
            _mesh = source._mesh;
            _renderParams = source._renderParams;
            _renderParams.material = material;
            Rotation = source.Rotation;
            Scale = source.Scale;
            Size = source.Size;
            BoundsRadius = source.BoundsRadius;
        }

        // 같은 Mesh / Transform 설정에 Material만 바꾼 복사본
        public InstancedMesh WithMaterial(Material material)
        {
            return new InstancedMesh(this, material);
        }

        public static bool TryCreate(GameObject prefab, out InstancedMesh instancedMesh)
        {
            instancedMesh = null;

            if (!Utility.TryGetComponent<MeshFilter>(prefab, out var meshFilter, prefab))
                return false;

            if (!Utility.IsValid(meshFilter.sharedMesh, prefab))
                return false;

            if (!Utility.TryGetComponent<MeshRenderer>(prefab, out var meshRenderer, prefab))
                return false;

            if (!Utility.IsValid(meshRenderer.sharedMaterial, prefab))
                return false;

            var material = meshRenderer.sharedMaterial;
            if (!material.enableInstancing)
            {
                Log.Error($"Material의 GPU Instancing이 꺼져 있습니다. [Material : {material.name}]", material);
                return false;
            }

            // Instance는 어디에나 있을 수 있으므로, 전체 배치를 컬링하지 않도록 충분히 큰 Bounds 사용
            var renderParams = new RenderParams(material)
            {
                layer = prefab.layer,
                shadowCastingMode = meshRenderer.shadowCastingMode,
                receiveShadows = meshRenderer.receiveShadows,
                motionVectorMode = meshRenderer.motionVectorGenerationMode,
                renderingLayerMask = meshRenderer.renderingLayerMask,
                worldBounds = new Bounds(Vector3.zero, Vector3.one * 100000),
            };

            instancedMesh = new InstancedMesh(meshFilter.sharedMesh, renderParams, prefab.transform);
            return true;
        }

        // matrices[start] 부터 count개를 Draw Call 1회당 최대 1023개씩 렌더링
        public void Render(NativeArray<Matrix4x4> matrices, int start, int count)
        {
            var end = start + count;
            for (var batch = start; batch < end; batch += MaxInstancesPerDraw)
                Graphics.RenderMeshInstanced(_renderParams, _mesh, 0, matrices, Mathf.Min(MaxInstancesPerDraw, end - batch), batch);
        }
    }
}
