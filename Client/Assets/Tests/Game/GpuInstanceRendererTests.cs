using System.Collections;
using aisilol.Game;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace aisilol.Tests.Game
{
    public class GpuInstanceRendererTests
    {
        private readonly TestHelper _helper = new();

        [SetUp]
        public void SetUp()
        {
            _helper.CreateScene();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        private static Matrix4x4 At(float x, float y, float z) => Matrix4x4.Translate(new Vector3(x, y, z));

        private InstancedMesh CreateMesh()
        {
            var prefab = _helper.CreateTemplate(PrimitiveType.Cube, "Instance");
            prefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);
            Assert.IsTrue(InstancedMesh.TryCreate(prefab, out var mesh));
            return mesh;
        }

        private static void RequireGpu()
        {
            if (!GpuInstanceRenderer.IsSupported)
                Assert.Ignore("Compute Shader를 지원하지 않는 환경");
        }

        // 보이는 수는 AsyncGPUReadback으로 몇 프레임 늦게 들어오므로 기대값이 될 때까지 매 프레임 렌더링
        private static IEnumerator RenderUntil(InstanceRenderer renderer, NativeArray<Matrix4x4> matrices, NativeArray<int> groups, Camera camera, int expected)
        {
            var endTime = Time.realtimeSinceStartupAsDouble + 5;
            while (renderer.VisibleCount != expected && Time.realtimeSinceStartupAsDouble < endTime)
            {
                renderer.Render(matrices, groups, camera);
                yield return null;
            }

            Assert.AreEqual(expected, renderer.VisibleCount);
        }

        [UnityTest]
        public IEnumerator Render_UseGpu_CullsOnGpu()
        {
            RequireGpu();
            var camera = TestHelper.CreateCamera();

            using var renderer = new InstanceRenderer(new VertexAnimation { speed = 1, hop = 0.1f, squash = 0.1f, spin = 1 }, CreateMesh()) { UseGpu = true };
            using var matrices = new NativeArray<Matrix4x4>(new[] { At(0, 0, 0), At(0, 0, -30), At(1, 0, 1) }, Allocator.Persistent);

            Assert.IsTrue(renderer.IsGpuActive);
            yield return RenderUntil(renderer, matrices, default, camera, 2);
            yield return RenderUntil(renderer, matrices, default, null, 3);

            renderer.Render(new NativeArray<Matrix4x4>(0, Allocator.Temp), camera);
            Assert.AreEqual(0, renderer.VisibleCount);
        }

        [UnityTest]
        public IEnumerator Render_UseGpuWithGroups_DrawsEveryGroup()
        {
            RequireGpu();
            var mesh = CreateMesh();
            var second = mesh.WithMaterial(_helper.CreateMaterial(enableInstancing: true));

            using var renderer = new InstanceRenderer(mesh, second) { UseGpu = true };
            using var matrices = new NativeArray<Matrix4x4>(new[] { At(0, 0, 0), At(1, 0, 0), At(2, 0, 0) }, Allocator.Persistent);
            using var groups = new NativeArray<int>(new[] { 0, 1, 5 }, Allocator.Persistent);

            yield return RenderUntil(renderer, matrices, groups, null, 3);
        }

        [UnityTest]
        public IEnumerator Render_MoreThanCapacity_GrowsBuffers()
        {
            RequireGpu();
            Assert.IsTrue(GpuInstanceRenderer.TryCreate(new[] { CreateMesh() }, default, out var gpu));

            using var small = new NativeArray<Matrix4x4>(10, Allocator.Persistent);
            using var large = new NativeArray<Matrix4x4>(3000, Allocator.Persistent);

            gpu.Render(small, default, null);
            Assert.AreEqual(1024, gpu.Capacity);

            gpu.Render(large, default, null);
            Assert.AreEqual(4096, gpu.Capacity);

            // 줄어들 때는 다시 만들지 않음
            gpu.Render(small, default, null);
            Assert.AreEqual(4096, gpu.Capacity);

            gpu.Dispose();
            gpu.Dispose();
            yield return null;
        }

        [Test]
        public void TryCreate_NoMeshes_ReturnsFalse()
        {
            Assert.IsFalse(GpuInstanceRenderer.TryCreate(null, default, out var renderer));
            Assert.IsNull(renderer);
            Assert.IsFalse(GpuInstanceRenderer.TryCreate(new InstancedMesh[0], default, out _));
        }

        [Test]
        public void UseGpu_CreateFails_StaysOnCpu()
        {
            // GPU 자원을 만들 수 없으면 CPU 경로를 사용하고, 다시 만들려고 시도하지 않음
            using var renderer = new InstanceRenderer { UseGpu = true };
            Assert.IsFalse(renderer.IsGpuActive);
            Assert.IsFalse(renderer.IsGpuActive);
        }

        [Test]
        public void ColorOf_ReadsBaseColorThenColorThenWhite()
        {
            var baseColor = _helper.CreateMaterial(enableInstancing: true);
            baseColor.SetColor("_BaseColor", Color.red);
            Assert.AreEqual(Color.red, GpuInstanceRenderer.ColorOf(baseColor));

            var colorShader = Shader.Find("Hidden/Internal-Colored");
            var noColorShader = Shader.Find("Hidden/BlitCopy");
            Assume.That(colorShader, Is.Not.Null);
            Assume.That(noColorShader, Is.Not.Null);

            var color = new Material(colorShader);
            color.SetColor("_Color", Color.green);
            Assert.AreEqual(Color.green, GpuInstanceRenderer.ColorOf(color));

            var none = new Material(noColorShader);
            Assert.AreEqual(Color.white, GpuInstanceRenderer.ColorOf(none));

            Object.DestroyImmediate(color);
            Object.DestroyImmediate(none);
        }
    }
}
