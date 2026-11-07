using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace aisilol.Game
{
    // 카메라 시야 밖의 Instance를 제외하고 그룹별로 렌더링. 그룹마다 다른 InstancedMesh(Material)를 사용할 수 있음
    // UseGpu가 켜져 있고 지원되면 GpuInstanceRenderer(Compute Shader 컬링 + RenderMeshIndirect)로 렌더링
    // 그 외에는 CPU 경로. 시야 판정은 IJobParallelFor, 그룹별로 모으는 작업은 IJob으로 처리한 뒤 RenderMeshInstanced
    internal sealed class InstanceRenderer : IDisposable
    {
        private const int JobBatchCount = 256;

        private readonly InstancedMesh[] _meshes;
        private readonly Plane[] _planes = new Plane[6];

        private NativeArray<float4> _frustum;
        private NativeList<byte> _visible;
        private NativeList<Matrix4x4> _visibleMatrices;
        private NativeArray<int> _groupStarts;
        private NativeArray<int> _groupCounts;
        private NativeArray<int> _noGroups;     // 그룹을 지정하지 않을 때 Job에 넘기는 빈 배열
        private readonly VertexAnimation _animation;
        private GpuInstanceRenderer _gpu;
        private bool _gpuCreateFailed;

        public int VisibleCount { get; private set; }

        // 켜면 다음 Render부터 GPU 경로 사용. 처음 켤 때 GPU 자원을 만들고, 지원하지 않으면 CPU 경로 유지
        public bool UseGpu { get; set; }
        public bool IsGpuActive => UseGpu && TryGetGpu(out _);

        public InstanceRenderer(params InstancedMesh[] meshes)
            : this(default, meshes)
        {
        }

        public InstanceRenderer(VertexAnimation animation, params InstancedMesh[] meshes)
        {
            _meshes = meshes;
            _animation = animation;
            _frustum = new NativeArray<float4>(6, Allocator.Persistent);
            _visible = new NativeList<byte>(Allocator.Persistent);
            _visibleMatrices = new NativeList<Matrix4x4>(Allocator.Persistent);
            _groupStarts = new NativeArray<int>(meshes.Length, Allocator.Persistent);
            _groupCounts = new NativeArray<int>(meshes.Length, Allocator.Persistent);
            _noGroups = new NativeArray<int>(0, Allocator.Persistent);
        }

        // groups는 Instance별 그룹 인덱스. 비어 있으면 모두 0번 그룹. camera가 없으면 시야 판정 없이 모두 렌더링
        public void Render(NativeArray<Matrix4x4> matrices, NativeArray<int> groups, Camera camera)
        {
            if (UseGpu && TryGetGpu(out var gpu))
            {
                gpu.Render(matrices, groups, camera);
                VisibleCount = gpu.VisibleCount;
                return;
            }

            var count = matrices.Length;
            VisibleCount = 0;

            if (count == 0)
                return;

            _visible.ResizeUninitialized(count);
            _visibleMatrices.ResizeUninitialized(count);

            var cull = camera != null;
            if (cull)
            {
                GeometryUtility.CalculateFrustumPlanes(camera, _planes);
                for (var index = 0; index < _planes.Length; index++)
                    _frustum[index] = new float4(_planes[index].normal, _planes[index].distance);
            }

            var visibilityHandle = new VisibilityJob
            {
                matrices = matrices,
                frustum = _frustum,
                boundsRadius = _meshes[0].BoundsRadius,
                cull = cull,
                visible = _visible.AsArray(),
            }.Schedule(count, JobBatchCount);

            new GroupJob
            {
                matrices = matrices,
                groups = groups.IsCreated ? groups : _noGroups,
                visible = _visible.AsArray(),
                visibleMatrices = _visibleMatrices.AsArray(),
                groupStarts = _groupStarts,
                groupCounts = _groupCounts,
            }.Schedule(visibilityHandle).Complete();

            var visibleMatrices = _visibleMatrices.AsArray();
            for (var group = 0; group < _meshes.Length; group++)
            {
                var groupCount = _groupCounts[group];
                if (groupCount == 0)
                    continue;

                _meshes[group].Render(visibleMatrices, _groupStarts[group], groupCount);
                VisibleCount += groupCount;
            }
        }

        public void Render(NativeArray<Matrix4x4> matrices, Camera camera)
        {
            Render(matrices, default, camera);
        }

        private bool TryGetGpu(out GpuInstanceRenderer gpu)
        {
            if (_gpu == null && !_gpuCreateFailed)
                _gpuCreateFailed = !GpuInstanceRenderer.TryCreate(_meshes, _animation, out _gpu);

            gpu = _gpu;
            return gpu != null;
        }

        public void Dispose()
        {
            _gpu?.Dispose();
            _gpu = null;

            if (_frustum.IsCreated)
                _frustum.Dispose();

            if (_visible.IsCreated)
                _visible.Dispose();

            if (_visibleMatrices.IsCreated)
                _visibleMatrices.Dispose();

            if (_groupStarts.IsCreated)
                _groupStarts.Dispose();

            if (_groupCounts.IsCreated)
                _groupCounts.Dispose();

            if (_noGroups.IsCreated)
                _noGroups.Dispose();
        }

        // Instance를 감싸는 구가 6개 평면 중 하나라도 완전히 바깥이면 시야 밖
        [BurstCompile]
        internal struct VisibilityJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Matrix4x4> matrices;
            [ReadOnly] public NativeArray<float4> frustum;
            [WriteOnly] public NativeArray<byte> visible;
            public float boundsRadius;      // Scale 1 기준 Mesh를 감싸는 구의 반지름
            public bool cull;

            public void Execute(int index)
            {
                if (!cull)
                {
                    visible[index] = 1;
                    return;
                }

                float4x4 matrix = matrices[index];
                var center = matrix.c3.xyz;
                var scale = math.sqrt(math.cmax(new float3(
                    math.lengthsq(matrix.c0.xyz),
                    math.lengthsq(matrix.c1.xyz),
                    math.lengthsq(matrix.c2.xyz))));
                var radius = boundsRadius * scale;

                for (var plane = 0; plane < 6; plane++)
                {
                    if (math.dot(frustum[plane].xyz, center) + frustum[plane].w < -radius)
                    {
                        visible[index] = 0;
                        return;
                    }
                }

                visible[index] = 1;
            }
        }

        // 보이는 Instance만 그룹 순서대로 모음. 그룹 수만 세고, 시작 위치를 정한 뒤 복사
        [BurstCompile]
        internal struct GroupJob : IJob
        {
            [ReadOnly] public NativeArray<Matrix4x4> matrices;
            [ReadOnly] public NativeArray<int> groups;
            [ReadOnly] public NativeArray<byte> visible;
            [WriteOnly] public NativeArray<Matrix4x4> visibleMatrices;
            public NativeArray<int> groupStarts;
            public NativeArray<int> groupCounts;

            public void Execute()
            {
                var groupCount = groupCounts.Length;
                var hasGroups = groups.Length == matrices.Length;

                for (var group = 0; group < groupCount; group++)
                    groupCounts[group] = 0;

                for (var index = 0; index < matrices.Length; index++)
                {
                    if (visible[index] != 0)
                        ++groupCounts[GroupOf(index, hasGroups, groupCount)];
                }

                var start = 0;
                for (var group = 0; group < groupCount; group++)
                {
                    groupStarts[group] = start;
                    start += groupCounts[group];
                }

                // 복사하면서 다시 세므로 groupCounts는 원래 값으로 돌아옴
                for (var group = 0; group < groupCount; group++)
                    groupCounts[group] = 0;

                for (var index = 0; index < matrices.Length; index++)
                {
                    if (visible[index] == 0)
                        continue;

                    var group = GroupOf(index, hasGroups, groupCount);
                    visibleMatrices[groupStarts[group] + groupCounts[group]] = matrices[index];
                    ++groupCounts[group];
                }
            }

            private int GroupOf(int index, bool hasGroups, int groupCount)
            {
                return hasGroups ? math.clamp(groups[index], 0, groupCount - 1) : 0;
            }
        }
    }
}
