using Unity.Entities.Serialization;
using UnityEngine;

[CreateAssetMenu(fileName = "SubsceneCache", menuName = "Scriptable Objects/SubsceneCache")]
public class SubsceneCache : ScriptableObject
{
    public EntitySceneReference gamePhysicsSubscene;
}
