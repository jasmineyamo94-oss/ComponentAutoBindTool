using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace PsdTools.Tests
{
    public sealed class PSDAutoPrefabGuidTests
    {
        private string _tempFolder;
        private readonly List<GameObject> _transientRoots = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _tempFolder = "Assets/__PSDAutoPrefabGuidTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _tempFolder.Substring("Assets/".Length));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject root in _transientRoots)
            {
                if (root != null)
                    UnityEngine.Object.DestroyImmediate(root);
            }

            _transientRoots.Clear();
            AssetDatabase.DeleteAsset(_tempFolder);
        }

        [Test]
        public void SaveMainPrefabPreservingGuid_FirstSaveCreatesGuid()
        {
            string prefabPath = _tempFolder + "/FirstSave.prefab";
            GameObject root = CreateRoot("FirstSave");

            PSDAutoPrefab.SaveMainPrefabPreservingGuid(root, prefabPath);

            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), Is.Not.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(prefabPath), Is.Not.Empty);
        }

        [Test]
        public void SaveMainPrefabPreservingGuid_ReplacesContentsAndDoesNotAccumulateComponents()
        {
            string prefabPath = _tempFolder + "/Overwrite.prefab";
            GameObject original = CreateRoot("Overwrite");
            original.AddComponent<CanvasGroup>();
            CreateChild(original, "Obsolete");
            PrefabUtility.SaveAsPrefabAsset(original, prefabPath);
            AssetDatabase.Refresh();
            string originalGuid = AssetDatabase.AssetPathToGUID(prefabPath);

            GameObject firstReplacement = CreateRoot("Overwrite");
            firstReplacement.AddComponent<Image>();
            CreateChild(firstReplacement, "Current");
            PSDAutoPrefab.SaveMainPrefabPreservingGuid(firstReplacement, prefabPath);

            AssertPrefabContents(prefabPath, originalGuid);

            GameObject secondReplacement = CreateRoot("Overwrite");
            secondReplacement.AddComponent<Image>();
            CreateChild(secondReplacement, "Current");
            PSDAutoPrefab.SaveMainPrefabPreservingGuid(secondReplacement, prefabPath);

            AssertPrefabContents(prefabPath, originalGuid);
        }

        private GameObject CreateRoot(string name)
        {
            var root = new GameObject(name, typeof(RectTransform));
            _transientRoots.Add(root);
            return root;
        }

        private static void CreateChild(GameObject parent, string name)
        {
            var child = new GameObject(name, typeof(RectTransform));
            child.transform.SetParent(parent.transform, false);
        }

        private static void AssertPrefabContents(string prefabPath, string expectedGuid)
        {
            Assert.That(AssetDatabase.AssetPathToGUID(prefabPath), Is.EqualTo(expectedGuid));

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Assert.That(contents.GetComponent<CanvasGroup>(), Is.Null);
                Assert.That(contents.GetComponents<Image>(), Has.Length.EqualTo(1));
                Assert.That(contents.transform.Find("Obsolete"), Is.Null);
                Assert.That(contents.transform.Find("Current"), Is.Not.Null);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }
    }
}
