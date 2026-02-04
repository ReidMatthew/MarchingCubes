using UnityEngine;
using UnityEditor;

namespace MarchingCubes
{
    [CustomEditor(typeof(MarchingCubesConverter))]
    public class MarchingCubesConverterEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (GUILayout.Button("Generate"))
            {
                ((MarchingCubesConverter)target).Generate();
            }
        }
    }
}
