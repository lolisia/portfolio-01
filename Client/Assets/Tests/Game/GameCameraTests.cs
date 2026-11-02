using System.Collections;
using System.Text.RegularExpressions;
using aisilol.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace aisilol.Tests.Game
{
    public class GameCameraTests
    {
        private readonly TestHelper _helper = new();
        private GameCamera _camera;

        [SetUp]
        public void SetUp()
        {
            _helper.CreateScene();
            _camera = new GameObject("GameCamera").AddComponent<GameCamera>();
            _camera.cameraHeight = 10;
            _camera.cameraDistance = 10;
            _camera.zoomStep = 0.1f;
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        [Test]
        public void Initialize_PlacesCameraBehindAndAbovePlayer()
        {
            var player = new GameObject("Player");
            player.transform.position = new Vector3(1, 2, 3);

            _camera.Initialize(player);

            Assert.That(_camera.transform.position, Is.EqualTo(new Vector3(1, 12, -7)).Using(Vector3EqualityComparer.Instance));
        }

        [Test]
        public void Initialize_NullPlayer_LogsError()
        {
            LogAssert.Expect(LogType.Error, new Regex("인자가 null 입니다"));

            _camera.Initialize(null);

            Assert.AreEqual(Vector3.zero, _camera.transform.position);
        }

        [Test]
        public void Zoom_In_ShrinksHeightAndDistanceAtSameRatio()
        {
            var zoomChangedCount = 0;
            _camera.onZoomChanged += () => zoomChangedCount++;

            _camera.Zoom(1);

            Assert.AreEqual(9, _camera.cameraDistance, 1e-4f);
            Assert.AreEqual(9, _camera.cameraHeight, 1e-4f);
            Assert.AreEqual(1, zoomChangedCount);
        }

        [Test]
        public void Zoom_Out_GrowsHeightAndDistanceAtSameRatio()
        {
            _camera.cameraHeight = 5;

            _camera.Zoom(-1);

            Assert.AreEqual(10 / 0.9f, _camera.cameraDistance, 1e-4f);
            Assert.AreEqual(5 / 0.9f, _camera.cameraHeight, 1e-4f);
        }

        [Test]
        public void Zoom_In_StopsAtMinimumDistance()
        {
            _camera.cameraDistance = 2.1f;
            _camera.cameraHeight = 2.1f;

            _camera.Zoom(1);

            Assert.AreEqual(2, _camera.cameraDistance, 1e-4f);
            Assert.AreEqual(2, _camera.cameraHeight, 1e-4f);
        }

        [Test]
        public void Zoom_ClampsZoomStep()
        {
            _camera.zoomStep = 5;

            _camera.Zoom(-1);

            Assert.AreEqual(100, _camera.cameraDistance, 1e-3f);
        }

        [TestCase(0f, 10f)]
        [TestCase(1f, 0f)]
        public void Zoom_IgnoredWithoutDirectionOrDistance(float direction, float distance)
        {
            _camera.cameraDistance = distance;
            var zoomChanged = false;
            _camera.onZoomChanged += () => zoomChanged = true;

            _camera.Zoom(direction);

            Assert.AreEqual(distance, _camera.cameraDistance);
            Assert.AreEqual(10, _camera.cameraHeight);
            Assert.IsFalse(zoomChanged);
        }

        [Test]
        public void Zoom_WithoutListener_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => _camera.Zoom(1));
        }

        [UnityTest]
        public IEnumerator Update_FollowsAndLooksAtPlayer()
        {
            var player = new GameObject("Player");
            _camera.Initialize(player);

            player.transform.position = new Vector3(5, 0, 5);
            yield return null;

            Assert.That(_camera.transform.position, Is.EqualTo(new Vector3(5, 10, -5)).Using(Vector3EqualityComparer.Instance));
            Assert.That(_camera.transform.forward, Is.EqualTo(new Vector3(0, -1, 1).normalized).Using(new Vector3EqualityComparer(1e-4f)));
        }

        [UnityTest]
        public IEnumerator Update_WithoutPlayer_KeepsPosition()
        {
            _camera.transform.position = new Vector3(1, 1, 1);
            yield return null;

            Assert.AreEqual(new Vector3(1, 1, 1), _camera.transform.position);
        }
    }
}
