using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "AAGame/Flow Catalog")]
public sealed class FlowCatalog : ScriptableObject
{
    public GameObject[] Prefabs;

    public Dictionary<string, UnityEngine.Object> CreateCatalog()
    {
        var result = new Dictionary<string, UnityEngine.Object>();
        foreach (var prefab in Prefabs)
            result.Add(prefab.name, prefab);
        return result;
    }
}
