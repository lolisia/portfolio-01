using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace aisilol.Tests
{
    // 테스트마다 빈 씬을 만들어 활성화하고, 끝나면 씬째로 언로드해 Instantiate된 복제본까지 정리
    internal sealed class TestHelper
    {
        private readonly List<Object> _assets = new();
        private Scene _scene;
        private GameObject _templateRoot;

        public void CreateScene()
        {
            _scene = SceneManager.CreateScene($"TestScene_{System.Guid.NewGuid():N}");
            SceneManager.SetActiveScene(_scene);
        }

        public IEnumerator UnloadScene()
        {
            foreach (var asset in _assets)
            {
                if (asset)
                    Object.Destroy(asset);
            }

            _assets.Clear();

            if (_scene.IsValid() && _scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(_scene);
        }

        public static IEnumerator WaitFrames(int count)
        {
            for (var index = 0; index < count; index++)
                yield return null;
        }

        // 비활성 부모 아래에 두어 템플릿 자신은 동작하지 않고, Instantiate한 복제본만 활성 상태로 동작
        public GameObject CreateTemplate(string name)
        {
            return CreateTemplate(new GameObject(name));
        }

        public GameObject CreateTemplate(PrimitiveType type, string name)
        {
            var gameObject = GameObject.CreatePrimitive(type);
            gameObject.name = name;
            Object.DestroyImmediate(gameObject.GetComponent<Collider>());
            return CreateTemplate(gameObject);
        }

        private GameObject CreateTemplate(GameObject gameObject)
        {
            if (!_templateRoot)
            {
                _templateRoot = new GameObject("Templates");
                _templateRoot.SetActive(false);
            }

            gameObject.transform.SetParent(_templateRoot.transform, false);
            return gameObject;
        }

        public Material CreateMaterial(bool enableInstancing)
        {
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"))
            {
                enableInstancing = enableInstancing,
            };

            _assets.Add(material);
            return material;
        }

        // Floor를 내려다보는 카메라. GameCamera 기본 높이/거리와 같은 위치
        public static Camera CreateCamera(string name = "Camera")
        {
            var camera = new GameObject(name).AddComponent<Camera>();
            camera.transform.SetPositionAndRotation(new Vector3(0, 10, -10), Quaternion.LookRotation(new Vector3(0, -10, 10)));
            return camera;
        }
    }
}
