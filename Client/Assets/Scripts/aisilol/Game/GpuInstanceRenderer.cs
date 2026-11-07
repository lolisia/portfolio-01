using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace aisilol.Game
{
    // 정점 애니메이션 설정. InstancedIndirect Shader의 Material 값으로 전달
    internal struct VertexAnimation
    {
        public float speed;     // 위상 변화 속도(rad/s)
        public float hop;       // 튀어 오르는 높이(m)
        public float squash;    // 세로로 찌그러지는 비율
        public float spin;      // Y축 회전 속도(rad/s)
    }

    // 행렬을 GraphicsBuffer로 올리고, 시야 판정과 그룹별 수집을 Compute Shader로 처리한 뒤 RenderMeshIndirect로 그룹마다 1회 렌더링
    // CPU는 행렬 업로드와 Dispatch만 수행. 1023개 단위 분할과 CPU 시야 판정이 없음
    // 보이는 수는 GPU에서 정해지므로 AsyncGPUReadback으로 몇 프레임 늦게 받아 표시용으로만 사용
    internal sealed class GpuInstanceRenderer : IDisposable
    {
        private const string CullingShaderPath = "Shaders/InstanceCulling";
        private const string RenderShaderPath = "Shaders/InstancedIndirect";
        private const string CullKernel = "Cull";
        private const int ThreadGroupSize = 64;
        private const int MinCapacity = 1024;
        private const int MatrixStride = 64;

        private static readonly int MatricesId = Shader.PropertyToID("_Matrices");
        private static readonly int GroupsId = Shader.PropertyToID("_Groups");
        private static readonly int VisibleId = Shader.PropertyToID("_Visible");
        private static readonly int ArgsId = Shader.PropertyToID("_Args");
        private static readonly int PlanesId = Shader.PropertyToID("_Planes");
        private static readonly int CountId = Shader.PropertyToID("_Count");
        private static readonly int CapacityId = Shader.PropertyToID("_Capacity");
        private static readonly int GroupCountId = Shader.PropertyToID("_GroupCount");
        private static readonly int HasGroupsId = Shader.PropertyToID("_HasGroups");
        private static readonly int CullId = Shader.PropertyToID("_Cull");
        private static readonly int BoundsRadiusId = Shader.PropertyToID("_BoundsRadius");
        private static readonly int VisibleOffsetId = Shader.PropertyToID("_VisibleOffset");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int AnimSpeedId = Shader.PropertyToID("_AnimSpeed");
        private static readonly int HopId = Shader.PropertyToID("_Hop");
        private static readonly int SquashId = Shader.PropertyToID("_Squash");
        private static readonly int SpinId = Shader.PropertyToID("_Spin");

        private readonly InstancedMesh[] _meshes;
        private readonly Material[] _materials;
        private readonly MaterialPropertyBlock[] _properties;
        private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _argsData;
        private readonly ComputeShader _culling;
        private readonly int _kernel;
        private readonly Plane[] _frustum = new Plane[6];
        private readonly Vector4[] _planes = new Vector4[6];
        private readonly GraphicsBuffer _args;

        private GraphicsBuffer _matrices;
        private GraphicsBuffer _groups;
        private GraphicsBuffer _visible;
        private bool _readbackPending;
        private bool _disposed;

        public int Capacity { get; private set; }
        public int VisibleCount { get; private set; }

        // Compute Shader를 지원하지 않거나 그래픽 장치가 없는 환경(-nographics)이면 false. 이 경우 CPU 렌더링을 사용
        // -nographics에서도 supportsComputeShaders는 true지만 Kernel이 컴파일되지 않으므로 장치 종류로 판단
        public static bool IsSupported => SystemInfo.supportsComputeShaders && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        private GpuInstanceRenderer(InstancedMesh[] meshes, ComputeShader culling, Shader shader, VertexAnimation animation)
        {
            _meshes = meshes;
            _culling = culling;
            _kernel = culling.FindKernel(CullKernel);

            _materials = new Material[meshes.Length];
            _properties = new MaterialPropertyBlock[meshes.Length];
            _argsData = new GraphicsBuffer.IndirectDrawIndexedArgs[meshes.Length];

            for (var group = 0; group < meshes.Length; group++)
            {
                var source = meshes[group].Material;
                var material = new Material(shader) { name = $"{source.name}_Indirect" };
                material.SetColor(BaseColorId, ColorOf(source));
                material.SetFloat(AnimSpeedId, animation.speed);
                material.SetFloat(HopId, animation.hop);
                material.SetFloat(SquashId, animation.squash);
                material.SetFloat(SpinId, animation.spin);
                _materials[group] = material;
                _properties[group] = new MaterialPropertyBlock();

                var mesh = meshes[group].Mesh;
                _argsData[group] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = mesh.GetIndexCount(0),
                    startIndex = mesh.GetIndexStart(0),
                    baseVertexIndex = mesh.GetBaseVertex(0),
                };
            }

            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, meshes.Length, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        }

        public static bool TryCreate(InstancedMesh[] meshes, VertexAnimation animation, out GpuInstanceRenderer renderer)
        {
            renderer = null;

            if (!IsSupported || meshes == null || meshes.Length == 0)
                return false;

            var culling = Resources.Load<ComputeShader>(CullingShaderPath);
            var shader = Resources.Load<Shader>(RenderShaderPath);
            if (!culling || !culling.HasKernel(CullKernel) || !shader || !shader.isSupported)
                return false;

            renderer = new GpuInstanceRenderer(meshes, culling, shader, animation);
            return true;
        }

        // Material 색상. URP Lit은 _BaseColor, Built-in은 _Color. 둘 다 없으면 흰색
        internal static Color ColorOf(Material material)
        {
            if (material.HasColor(BaseColorId))
                return material.GetColor(BaseColorId);

            return material.HasColor(ColorId) ? material.GetColor(ColorId) : Color.white;
        }

        // groups는 Instance별 그룹 인덱스. 비어 있으면 모두 0번 그룹. camera가 없으면 시야 판정 없이 모두 렌더링
        public void Render(NativeArray<Matrix4x4> matrices, NativeArray<int> groups, Camera camera)
        {
            var count = matrices.Length;
            if (count == 0)
            {
                VisibleCount = 0;
                return;
            }

            EnsureCapacity(count);

            _matrices.SetData(matrices, 0, 0, count);
            var hasGroups = groups.IsCreated && groups.Length == count && _meshes.Length > 1;
            if (hasGroups)
                _groups.SetData(groups, 0, 0, count);

            // instanceCount는 Compute Shader가 0부터 다시 셈
            _args.SetData(_argsData);

            var cull = camera != null;
            if (cull)
            {
                GeometryUtility.CalculateFrustumPlanes(camera, _frustum);
                for (var index = 0; index < _frustum.Length; index++)
                    _planes[index] = new Vector4(_frustum[index].normal.x, _frustum[index].normal.y, _frustum[index].normal.z, _frustum[index].distance);
            }

            _culling.SetBuffer(_kernel, MatricesId, _matrices);
            _culling.SetBuffer(_kernel, GroupsId, _groups);
            _culling.SetBuffer(_kernel, VisibleId, _visible);
            _culling.SetBuffer(_kernel, ArgsId, _args);
            _culling.SetVectorArray(PlanesId, _planes);
            _culling.SetInt(CountId, count);
            _culling.SetInt(CapacityId, Capacity);
            _culling.SetInt(GroupCountId, _meshes.Length);
            _culling.SetInt(HasGroupsId, hasGroups ? 1 : 0);
            _culling.SetInt(CullId, cull ? 1 : 0);
            _culling.SetFloat(BoundsRadiusId, _meshes[0].BoundsRadius);
            _culling.Dispatch(_kernel, (count + ThreadGroupSize - 1) / ThreadGroupSize, 1, 1);

            for (var group = 0; group < _meshes.Length; group++)
            {
                var properties = _properties[group];
                properties.SetBuffer(MatricesId, _matrices);
                properties.SetBuffer(VisibleId, _visible);
                properties.SetInteger(VisibleOffsetId, group * Capacity);

                var renderParams = _meshes[group].RenderParams;
                renderParams.material = _materials[group];
                renderParams.matProps = properties;
                Graphics.RenderMeshIndirect(renderParams, _meshes[group].Mesh, _args, 1, group);
            }

            RequestVisibleCount();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _args.Release();
            _matrices?.Release();
            _groups?.Release();
            _visible?.Release();

            foreach (var material in _materials)
                UnityEngine.Object.Destroy(material);
        }

        // 수가 늘면 2의 거듭제곱 크기로 다시 만듦. 줄어들 때는 유지
        private void EnsureCapacity(int count)
        {
            if (count <= Capacity)
                return;

            Capacity = Mathf.NextPowerOfTwo(Mathf.Max(count, MinCapacity));

            _matrices?.Release();
            _groups?.Release();
            _visible?.Release();

            _matrices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, MatrixStride);
            _groups = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity, sizeof(int));
            _visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Capacity * _meshes.Length, sizeof(uint));
        }

        private void RequestVisibleCount()
        {
            if (_readbackPending)
                return;

            _readbackPending = true;
            AsyncGPUReadback.Request(_args, OnVisibleCount);
        }

        private void OnVisibleCount(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;

            if (_disposed || request.hasError)
                return;

            var args = request.GetData<GraphicsBuffer.IndirectDrawIndexedArgs>();
            var visible = 0;
            foreach (var arg in args)
                visible += (int)arg.instanceCount;

            VisibleCount = visible;
        }
    }
}
