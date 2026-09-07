using UnityEngine;

[CreateAssetMenu(fileName = "ShaderCache", menuName = "Scriptable Objects/ShaderCache")]
public class ShaderCache : ScriptableObject
{
    public Shader lit;
    public Shader unlit;
}
