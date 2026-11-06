using System.Collections;
using aisilol.Game;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace aisilol.Tests.Game
{
    public class InstanceRendererTests
    {
        private readonly TestHelper _helper = new();

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        private static Matrix4x4 At(float x, float y, float z, float scale = 1)
        {
            return Matrix4x4.TRS(new Vector3(x, y, z), Quaternion.identity, Vector3.one * scale);
        }

        // 카메라 시야 안 / 밖을 판정. 원점 근처는 보이고, 카메라 뒤나 화면 옆 먼 곳은 보이지 않음
        [Test]
        public void VisibilityJob_ExcludesInstancesOutsideFrustum()
        {
            _helper.CreateScene();
            var camera = TestHelper.CreateCamera();

            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var frustumValues = new float4[6];
            for (var index = 0; index < 6; index++)
                frustumValues[index] = new float4(planes[index].normal, planes[index].distance);

            using var frustum = new NativeArray<float4>(frustumValues, Allocator.TempJob);
            using var matrices = new NativeArray<Matrix4x4>(new[]
            {
                At(0, 0, 0),            // 화면 중앙
                At(0, 0, -30),          // 카메라 뒤
                At(500, 0, 0),          // 화면 옆 멀리
                At(500, 0, 0, 2000),    // 화면 옆이지만 매우 커서 걸침
            }, Allocator.TempJob);
            using var visible = new NativeArray<byte>(matrices.Length, Allocator.TempJob);

            new InstanceRenderer.VisibilityJob
            {
                matrices = matrices,
                frustum = frustum,
                visible = visible,
                boundsRadius = 0.5f,
                cull = true,
            }.Run(matrices.Length);

            CollectionAssert.AreEqual(new byte[] { 1, 0, 0, 1 }, visible.ToArray());
        }

        [Test]
        public void VisibilityJob_CullDisabled_AllVisible()
        {
            using var frustum = new NativeArray<float4>(6, Allocator.TempJob);
            using var matrices = new NativeArray<Matrix4x4>(new[] { At(0, 0, -1000), At(1000, 0, 0) }, Allocator.TempJob);
            using var visible = new NativeArray<byte>(matrices.Length, Allocator.TempJob);

            new InstanceRenderer.VisibilityJob
            {
                matrices = matrices,
                frustum = frustum,
                visible = visible,
                boundsRadius = 0.5f,
                cull = false,
            }.Run(matrices.Length);

            CollectionAssert.AreEqual(new byte[] { 1, 1 }, visible.ToArray());
        }

        [Test]
        public void GroupJob_PacksVisibleInstancesByGroup()
        {
            using var matrices = new NativeArray<Matrix4x4>(new[] { At(0, 0, 0), At(1, 0, 0), At(2, 0, 0), At(3, 0, 0), At(4, 0, 0) }, Allocator.TempJob);
            using var groups = new NativeArray<int>(new[] { 1, 0, 1, 2, 9 }, Allocator.TempJob);
            using var visible = new NativeArray<byte>(new byte[] { 1, 1, 0, 1, 1 }, Allocator.TempJob);
            using var visibleMatrices = new NativeArray<Matrix4x4>(matrices.Length, Allocator.TempJob);
            using var groupStarts = new NativeArray<int>(3, Allocator.TempJob);
            using var groupCounts = new NativeArray<int>(3, Allocator.TempJob);

            new InstanceRenderer.GroupJob
            {
                matrices = matrices,
                groups = groups,
                visible = visible,
                visibleMatrices = visibleMatrices,
                groupStarts = groupStarts,
                groupCounts = groupCounts,
            }.Run();

            // 그룹 0: [1], 그룹 1: [0] (2는 안 보임), 그룹 2: [3, 4] (범위 밖 그룹 9는 마지막 그룹)
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, groupStarts.ToArray());
            CollectionAssert.AreEqual(new[] { 1, 1, 2 }, groupCounts.ToArray());
            Assert.AreEqual(1, visibleMatrices[0].m03);
            Assert.AreEqual(0, visibleMatrices[1].m03);
            Assert.AreEqual(3, visibleMatrices[2].m03);
            Assert.AreEqual(4, visibleMatrices[3].m03);
        }

        [Test]
        public void GroupJob_WithoutGroups_UsesFirstGroup()
        {
            using var matrices = new NativeArray<Matrix4x4>(new[] { At(0, 0, 0), At(1, 0, 0) }, Allocator.TempJob);
            using var groups = new NativeArray<int>(0, Allocator.TempJob);
            using var visible = new NativeArray<byte>(new byte[] { 1, 1 }, Allocator.TempJob);
            using var visibleMatrices = new NativeArray<Matrix4x4>(matrices.Length, Allocator.TempJob);
            using var groupStarts = new NativeArray<int>(2, Allocator.TempJob);
            using var groupCounts = new NativeArray<int>(2, Allocator.TempJob);

            new InstanceRenderer.GroupJob
            {
                matrices = matrices,
                groups = groups,
                visible = visible,
                visibleMatrices = visibleMatrices,
                groupStarts = groupStarts,
                groupCounts = groupCounts,
            }.Run();

            CollectionAssert.AreEqual(new[] { 2, 0 }, groupCounts.ToArray());
        }

        [UnityTest]
        public IEnumerator Render_CountsOnlyVisibleInstances()
        {
            _helper.CreateScene();
            var camera = TestHelper.CreateCamera();

            var prefab = _helper.CreateTemplate(PrimitiveType.Cube, "Instance");
            prefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);
            Assert.IsTrue(InstancedMesh.TryCreate(prefab, out var mesh));

            using var renderer = new InstanceRenderer(mesh);
            using var matrices = new NativeArray<Matrix4x4>(new[] { At(0, 0, 0), At(0, 0, -30), At(1, 0, 1) }, Allocator.TempJob);

            renderer.Render(matrices, camera);
            Assert.AreEqual(2, renderer.VisibleCount);

            renderer.Render(matrices, null);
            Assert.AreEqual(3, renderer.VisibleCount);

            renderer.Render(new NativeArray<Matrix4x4>(0, Allocator.Temp), camera);
            Assert.AreEqual(0, renderer.VisibleCount);
            yield return null;
        }
    }
}
