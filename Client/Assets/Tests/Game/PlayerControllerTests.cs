using System.Collections;
using System.Text.RegularExpressions;
using aisilol.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace aisilol.Tests.Game
{
    public class PlayerControllerTests : InputTestFixture
    {
        private readonly TestHelper _helper = new();
        private Keyboard _keyboard;
        private Mouse _mouse;
        private PlayerController _player;

        public override void Setup()
        {
            base.Setup();
            _helper.CreateScene();

            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();

            _player = new GameObject("Player").AddComponent<PlayerController>();
            _player.moveSpeed = 5;
            _player.rotateSpeed = 1000;
        }

        // Input 테스트 환경이 해제되기 전에 Input Action을 정리
        public override void TearDown()
        {
            if (_player)
                Object.DestroyImmediate(_player.gameObject);

            base.TearDown();
        }

        [UnityTearDown]
        public IEnumerator UnloadScene() => _helper.UnloadScene();

        [UnityTest]
        public IEnumerator Update_WithoutInput_StaysStill()
        {
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(Vector3.zero, _player.transform.position);
            Assert.AreEqual(Quaternion.identity, _player.transform.rotation);
        }

        [UnityTest]
        public IEnumerator Update_MoveInput_MovesAndTurnsTowardInput()
        {
            Press(_keyboard.dKey);
            yield return TestHelper.WaitFrames(5);
            Release(_keyboard.dKey);

            var position = _player.transform.position;
            Assert.Greater(position.x, 0);
            Assert.AreEqual(0, position.y, 1e-5f);
            Assert.AreEqual(0, position.z, 1e-5f);
            Assert.That(_player.transform.forward, Is.EqualTo(Vector3.right).Using(new Vector3EqualityComparer(1e-3f)));
        }

        [UnityTest]
        public IEnumerator Update_ScrollInput_ZoomsCamera()
        {
            var gameCamera = new GameObject("GameCamera").AddComponent<GameCamera>();
            gameCamera.cameraDistance = 10;
            _player.Initialize(gameCamera);

            Set(_mouse.scroll.y, 1f, queueEventOnly: true);
            yield return TestHelper.WaitFrames(2);

            Assert.Less(gameCamera.cameraDistance, 10);
        }

        [UnityTest]
        public IEnumerator Update_ScrollInputWithoutCamera_IsIgnored()
        {
            Set(_mouse.scroll.y, 1f, queueEventOnly: true);
            yield return TestHelper.WaitFrames(2);

            Assert.AreEqual(Vector3.zero, _player.transform.position);
        }

        [Test]
        public void Initialize_NullCamera_LogsError()
        {
            LogAssert.Expect(LogType.Error, new Regex("인자가 null 입니다"));

            _player.Initialize(null);
        }
    }
}
