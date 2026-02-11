using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MarchingCubes
{
    /// <summary>
    /// When placed on a GameObject with a MeshFilter and mesh, sets up MeshToSDF and SDFTexture,
    /// bakes the mesh to an SDF, runs MarchingCubesRenderer to produce a new mesh. The MeshFilter
    /// will hold the marching cubes result. When conversion finishes, the SDF texture, SDFTexture,
    /// MeshToSDF, and this converter are removed; MarchingCubesRenderer and the result mesh remain.
    /// </summary>
    [ExecuteAlways]
    [RequireComponent(typeof(MeshFilter))]
    public class MarchingCubesConverter : MonoBehaviour
    {
        [Tooltip("SDF voxel resolution along the longest axis. Y and Z are derived from size proportions.")]
        [Range(16, 256)]
        public int resolution = 128;

        [Tooltip("Extra space added to mesh bounds before voxelization.")]
        public float padding = 0f;

#if UNITY_EDITOR
        [Tooltip("Project path for the saved Texture3D (e.g. Assets/SDFTextures/MyVolume.asset). If set, the Texture3D is saved to this path; if empty, it is not saved.")]
        public string texture3DSavePath = "";
#endif

        const float MinSize = 0.001f;

        void OnEnable()
        {
        }

        /// <summary>
        /// Runs the conversion pipeline: bakes mesh to SDF, runs marching cubes, adds MarchingCubesRenderer,
        /// then removes SDF/MeshToSDF/this converter. Call from the Inspector Generate button or from code.
        /// </summary>
        public void Generate()
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
            size.x += padding;
            size.y += padding;
            size.z += padding;

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
            // SDFTexture uses resolution as X-axis and scales Y,Z by size; we want resolution = largest dimension
            float sizeMax = Mathf.Max(size.x, size.y, size.z);
            int resolutionForSDF = Mathf.Max(1, Mathf.RoundToInt(resolution * size.x / sizeMax));
            sdfTexture.resolution = resolutionForSDF;

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
            floatRT.name = mesh.name;
            floatRT.Create();

            int kernel = copyCS.FindKernel("CopySDFToFloat");
            copyCS.SetTexture(kernel, "SourceSDF", sdfRT);
            copyCS.SetTexture(kernel, "TargetFloat", floatRT);
            copyCS.SetInts("textureSize", res.x, res.y, res.z);
            int tx = (res.x + 7) / 8;
            int ty = (res.y + 7) / 8;
            int tz = (res.z + 7) / 8;
            copyCS.Dispatch(kernel, tx, ty, tz);

            // Copy float RT to Texture3D and assign to renderer
            Texture3D tex3D = CopyRenderTextureToTexture3D(floatRT, mesh.name + " (Density)");
            if (tex3D == null)
            {
                CleanupAfterConversion(sdfRT, floatRT, sdfTexture, meshToSDF);
                return;
            }
            mcr.densityTexture3D = tex3D;
            mcr.densityMap = null;
            mcr.voxelSize = (size.x + (padding / 2)) / res.x;
            mcr.RecomputeMesh();

#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(texture3DSavePath))
            {
                AssetDatabase.CreateAsset(tex3D, texture3DSavePath);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
#endif

            // Clean up: remove SDF texture, float RT, SDFTexture, MeshToSDF, and this converter (deferred so we're not destroying during OnEnable)
            CleanupAfterConversion(sdfRT, floatRT, sdfTexture, meshToSDF);
        }

        /// <summary>
        /// Copies a 3D RenderTexture into a new Texture3D (slice-by-slice readback). Returns null if rt is not Tex3D.
        /// </summary>
        static Texture3D CopyRenderTextureToTexture3D(RenderTexture rt, string nameForTexture)
        {
            if (rt == null)
                return null;
            if (rt.dimension != TextureDimension.Tex3D)
            {
                Debug.LogError($"MarchingCubesConverter: RenderTexture must be 3D. Current: {rt.dimension}");
                return null;
            }

            int w = rt.width, h = rt.height, d = rt.volumeDepth;
            var format = TextureFormat.RFloat;
            var tex3D = new Texture3D(w, h, d, format, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = nameForTexture
            };

            RenderTexture tmp2D = RenderTexture.GetTemporary(w, h, 0, rt.graphicsFormat);
            RenderTexture prev = RenderTexture.active;
            var pixels = new Color[w * h * d];

            for (int z = 0; z < d; z++)
            {
                Graphics.CopyTexture(rt, z, 0, tmp2D, 0, 0);
                RenderTexture.active = tmp2D;
                var tex2D = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
                tex2D.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex2D.Apply(false, false);
                Color[] slice = tex2D.GetPixels();
                System.Array.Copy(slice, 0, pixels, z * (w * h), w * h);
                DestroyImmediate(tex2D);
            }

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(tmp2D);

            tex3D.SetPixels(pixels);
            tex3D.Apply(false, false);
            return tex3D;
        }

        void CleanupAfterConversion(RenderTexture sdfRT, RenderTexture floatRT, SDFTexture sdfTexture, MeshToSDF meshToSDF)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                var rtSdf = sdfRT;
                var rtFloat = floatRT;
                var sdf = sdfTexture;
                var meshToSdf = meshToSDF;
                var self = this;
                EditorApplication.delayCall += () =>
                {
                    if (sdf != null) sdf.sdf = null;
                    if (rtSdf != null) { rtSdf.Release(); DestroyImmediate(rtSdf); }
                    if (rtFloat != null) { rtFloat.Release(); DestroyImmediate(rtFloat); }
                    if (sdf != null) DestroyImmediate(sdf);
                    if (meshToSdf != null) DestroyImmediate(meshToSdf);
                    if (self != null) DestroyImmediate(self);
                };
                return;
            }
#endif
            sdfTexture.sdf = null;
            if (sdfRT != null) { sdfRT.Release(); Destroy(sdfRT); }
            if (floatRT != null) { floatRT.Release(); Destroy(floatRT); }
            Destroy(sdfTexture);
            Destroy(meshToSDF);
            Destroy(this);
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
