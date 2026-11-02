using System.Collections;
using System.Text.RegularExpressions;
using aisilol.Common;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace aisilol.Tests.Common
{
    public class UtilityTests
    {
        private static readonly Regex NullError = TestHelper.Pattern("인자가 null 입니다");

        private readonly TestHelper _helper = new();

        [SetUp]
        public void SetUp() => _helper.CreateScene();

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        [Test]
        public void IsValid_UnityObject()
        {
            Assert.IsTrue(Utility.IsValid(new GameObject("Valid")));

            LogAssert.Expect(LogType.Error, NullError);
            Assert.IsFalse(Utility.IsValid((Object)null));
        }

        [UnityTest]
        public IEnumerator IsValid_DestroyedUnityObject_ReturnsFalse()
        {
            var gameObject = new GameObject("Destroyed");
            Object.Destroy(gameObject);
            yield return null;

            LogAssert.Expect(LogType.Error, NullError);
            Assert.IsFalse(Utility.IsValid(gameObject));
        }

        [Test]
        public void IsValid_PlainObject()
        {
            Assert.IsTrue(Utility.IsValid(new object()));

            LogAssert.Expect(LogType.Error, NullError);
            Assert.IsFalse(Utility.IsValid((object)null));
        }

        [Test]
        public void CheckValid_LogsOnlyWhenNull()
        {
            Utility.CheckValid(new object());
            LogAssert.NoUnexpectedReceived();

            LogAssert.Expect(LogType.Error, NullError);
            Utility.CheckValid(null);
        }

        [Test]
        public void TryInstantiate_ValidPrefab_CreatesClone()
        {
            var prefab = new GameObject("Prefab");

            Assert.IsTrue(Utility.TryInstantiate(prefab, out var instance));
            Assert.IsNotNull(instance);
            Assert.AreNotSame(prefab, instance);
        }

        [Test]
        public void TryInstantiate_NullPrefab_ReturnsFalse()
        {
            LogAssert.Expect(LogType.Error, NullError);

            Assert.IsFalse(Utility.TryInstantiate<GameObject>(null, out var instance));
            Assert.IsNull(instance);
        }

        [Test]
        public void TryGetComponent_ExistingComponent_ReturnsIt()
        {
            var gameObject = new GameObject("Owner", typeof(BoxCollider));

            Assert.IsTrue(Utility.TryGetComponent<BoxCollider>(gameObject, out var component));
            Assert.AreSame(gameObject.GetComponent<BoxCollider>(), component);
        }

        [Test]
        public void TryGetComponent_MissingComponent_ReturnsFalse()
        {
            var gameObject = new GameObject("Owner");
            LogAssert.Expect(LogType.Error, TestHelper.Pattern("Owner에 BoxCollider Component가 존재하지 않습니다"));

            Assert.IsFalse(Utility.TryGetComponent<BoxCollider>(gameObject, out var component));
            Assert.IsNull(component);
        }

        [Test]
        public void TryGetComponent_NullGameObject_ReturnsFalse()
        {
            LogAssert.Expect(LogType.Error, NullError);

            Assert.IsFalse(Utility.TryGetComponent<BoxCollider>(null, out var component));
            Assert.IsNull(component);
        }
    }
}
