using UnityEngine;
using UnityEngine.Rendering;

namespace MarchingCubes
{
    /// <summary>
    /// When placed on a GameObject with a MeshFilter and mesh, sets up MeshToSDF and SDFTexture,
    /// bakes the mesh to an SDF, runs MarchingCubesRenderer to produce a new mesh. The MeshFilter
    /// will hold the marching cubes result. Converter, SDFTexture, and MeshToSDF remain on the object.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    public class MarchingCubesConverter : MonoBehaviour
    {
        [Tooltip("SDF voxel resolution along the longest axis. Y and Z are derived from size proportions.")]
        [Range(16, 256)]
        public int resolution = 64;

        const float MinSize = 0.001f;

        void OnEnable()
        {
            MeshFilter mf = GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                Debug.LogError("MarchingCubesConverter: GameObject must have a MeshFilter with an assigned mesh.", this);
                return;
            }

            Mesh mesh = mf.sharedMesh;
            Vector3 size = mesh.bounds.size;
            size.x = Mathf.Max(size.x, MinSize);
            size.y = Mathf.Max(size.y, MinSize);
            size.z = Mathf.Max(size.z, MinSize);

            // Add components if missing
            SDFTexture sdfTexture = GetComponent<SDFTexture>();
            if (sdfTexture == null)
                sdfTexture = gameObject.AddComponent<SDFTexture>();

            MeshToSDF meshToSDF = GetComponent<MeshToSDF>();
            if (meshToSDF == null)
                meshToSDF = gameObject.AddComponent<MeshToSDF>();

            MarchingCubesRenderer mcr = GetComponent<MarchingCubesRenderer>();
            if (mcr == null)
                mcr = gameObject.AddComponent<MarchingCubesRenderer>();

            if (!AssignMeshToSDFCompute(meshToSDF))
            {
                Debug.LogError("MarchingCubesConverter: Could not assign MeshToSDF compute shader. Ensure MeshToSDF package is set up.", this);
                return;
            }

            // Set SDF size to mesh bounds
            sdfTexture.size = size;
            sdfTexture.resolution = resolution;

            // Create 3D RenderTexture (RHalf) for SDF and assign to SDFTexture
            Vector3Int res = sdfTexture.voxelResolution;
            RenderTexture sdfRT = new RenderTexture(res.x, res.y, 0, RenderTextureFormat.RHalf);
            sdfRT.dimension = TextureDimension.Tex3D;
            sdfRT.volumeDepth = res.z;
            sdfRT.enableRandomWrite = true;
            sdfRT.name = mesh.name + " (SDF)";
            sdfRT.Create();
            sdfTexture.sdf = sdfRT;

            // Wire MeshToSDF
            meshToSDF.sdfTexture = sdfTexture;
            meshToSDF.updateMode = MeshToSDF.UpdateMode.Explicit;
            meshToSDF.floodFillIterations = 64;
            meshToSDF.floodFillQuality = MeshToSDF.FloodFillQuality.Ultra;

            // Run MeshToSDF once
            CommandBuffer cmd = CommandBufferPool.Get("MarchingCubesConverter");
            meshToSDF.UpdateSDF(cmd);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            // Convert SDF (RHalf) to float RT for MarchingCubesRenderer
            ComputeShader copyCS = Resources.Load<ComputeShader>("CopySDFToFloat");
            if (copyCS == null)
            {
                Debug.LogError("MarchingCubesConverter: Could not load CopySDFToFloat compute shader from Resources.", this);
                sdfRT.Release();
                DestroyImmediate(sdfRT);
                return;
            }

            RenderTexture floatRT = new RenderTexture(res.x, res.y, 0, RenderTextureFormat.RFloat);
            floatRT.dimension = TextureDimension.Tex3D;
            floatRT.volumeDepth = res.z;
            floatRT.enableRandomWrite = true;
            floatRT.name = mesh.name + " (Density Float)";
            floatRT.Create();

            int kernel = copyCS.FindKernel("CopySDFToFloat");
            copyCS.SetTexture(kernel, "SourceSDF", sdfRT);
            copyCS.SetTexture(kernel, "TargetFloat", floatRT);
            copyCS.SetInts("textureSize", res.x, res.y, res.z);
            int tx = (res.x + 7) / 8;
            int ty = (res.y + 7) / 8;
            int tz = (res.z + 7) / 8;
            copyCS.Dispatch(kernel, tx, ty, tz);

            // Assign to MarchingCubesRenderer and run
            mcr.densityMap = floatRT;
            mcr.densityTexture3D = null;
            mcr.RecomputeMesh();

            // Leave sdfRT assigned to SDFTexture.sdf so it stays visible in the inspector
        }

        bool AssignMeshToSDFCompute(MeshToSDF meshToSDF)
        {
#if UNITY_EDITOR
            ComputeShader packageCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.unity.demoteam.mesh-to-sdf/Runtime/MeshToSDF.compute");
            if (packageCompute != null)
            {
                UnityEditor.SerializedObject so = new UnityEditor.SerializedObject(meshToSDF);
                UnityEditor.SerializedProperty prop = so.FindProperty("m_Compute");
                if (prop != null)
                {
                    prop.objectReferenceValue = packageCompute;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    return true;
                }
            }
#endif
            ComputeShader fromResources = Resources.Load<ComputeShader>("MeshToSDF");
            if (fromResources != null)
            {
#if UNITY_EDITOR
                UnityEditor.SerializedObject so = new UnityEditor.SerializedObject(meshToSDF);
                UnityEditor.SerializedProperty prop = so.FindProperty("m_Compute");
                if (prop != null)
                {
                    prop.objectReferenceValue = fromResources;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    return true;
                }
#else
                // At runtime we can only set via reflection if no public setter
                var field = typeof(MeshToSDF).GetField("m_Compute", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    field.SetValue(meshToSDF, fromResources);
                    return true;
                }
#endif
            }
            return false;
        }
    }
}
