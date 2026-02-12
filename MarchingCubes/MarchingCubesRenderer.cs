using UnityEngine;

namespace MarchingCubes
{
[ExecuteAlways]
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class MarchingCubesRenderer : MonoBehaviour
{
    [Range(-1f, 1f)]
    public float isoLevel;

    [Header("Scale")]
    [Tooltip("World size per voxel. A 128×64×64 volume is rendered as 128×voxelSize by 64×voxelSize by 64×voxelSize units.")]
    public float voxelSize = 1f;

    [Header("Level of Detail")]
    [Range(1, 8)]
    [Tooltip("Resolution factor for level of detail. 1 = use every voxel, 2 = average 2x2x2 voxels, etc.")]
    public int resolution = 1;

    [Header("Editor Preview")]
    [Tooltip("Show marching cubes mesh in Scene view when game is not running")]
    public bool renderInEditMode = true;

    [Header("References")]
    [Tooltip("3D RenderTexture containing the density map (takes priority if both are set)")]
    public RenderTexture densityMap;

    [Tooltip("3D Texture containing the density map (automatically converted to RenderTexture and assigned to densityMap when set)")]
    public Texture3D densityTexture3D;

    MarchingCubesCore _core;
    ComputeShader _texture3DToRenderTextureCS;
    ComputeShader _downsampleDensityMapCS;
    Texture3D _lastConvertedTexture3D;

    RenderTexture _cachedDownsampled;

    RenderTexture _cachedDownsampledFromRT;
    RenderTexture _cachedDownsampleSourceRT;
    int _cachedDownsampleResRT = -1;

    float _lastIsoLevel = float.MinValue;
    RenderTexture _lastDensityMap;
    Texture3D _lastDensityTexture3D;
    int _lastResolution = -1;
    bool _lastRenderInEditMode = true;
    float _lastVoxelSize = float.MinValue;

    void OnEnable()
    {
        _texture3DToRenderTextureCS = Resources.Load<ComputeShader>("Texture3DToRenderTexture");
        if (_texture3DToRenderTextureCS == null)
            Debug.LogError("MarchingCubesRenderer: Could not load Texture3DToRenderTexture compute shader from Resources.");

        _downsampleDensityMapCS = Resources.Load<ComputeShader>("DownsampleDensityMap");
        if (_downsampleDensityMapCS == null)
            Debug.LogError("MarchingCubesRenderer: Could not load DownsampleDensityMap compute shader from Resources.");

        _core = new MarchingCubesCore();

        _lastIsoLevel = isoLevel;
        _lastDensityMap = densityMap;
        _lastDensityTexture3D = densityTexture3D;
        _lastResolution = resolution;
        _lastRenderInEditMode = renderInEditMode;
        _lastVoxelSize = voxelSize;

        if (Application.isPlaying || renderInEditMode)
            RecomputeMesh();
        else
            ClearMesh();
    }

    void OnDisable()
    {
        _core?.Release();
        _core = null;
    }

    string GetMeshName()
    {
        string baseName = densityTexture3D != null ? densityTexture3D.name : densityMap != null ? densityMap.name : "MarchingCubes";
        return baseName + " (Mesh)";
    }

    void AssignMeshToFilter()
    {
        if (_core == null) return;
        Mesh mesh = _core.Mesh;
        if (mesh != null)
        {
            mesh.name = GetMeshName();
            MeshFilter mf = GetComponent<MeshFilter>();
            if (mf != null)
                mf.sharedMesh = mesh;
        }
    }

    void ClearMesh()
    {
        MeshFilter mf = GetComponent<MeshFilter>();
        if (mf != null)
            mf.sharedMesh = null;
    }

    void Update()
    {
        if (_core != null && _core.TryCompleteReadback())
        {
            Mesh mesh = _core.Mesh;
            if (mesh != null)
            {
                mesh.name = GetMeshName();
                MeshFilter mf = GetComponent<MeshFilter>();
                if (mf != null)
                    mf.sharedMesh = mesh;
            }
        }
        if (_core != null && _core.NeedsRecompute)
        {
            _core.ClearNeedsRecompute();
            RecomputeMesh();
        }
    }

    void OnValidate()
    {
        ConvertTexture3DToRenderTexture();

        bool needsRecompute = _core != null && (
            isoLevel != _lastIsoLevel ||
            densityMap != _lastDensityMap ||
            densityTexture3D != _lastDensityTexture3D ||
            resolution != _lastResolution ||
            renderInEditMode != _lastRenderInEditMode ||
            voxelSize != _lastVoxelSize
        );

        if (needsRecompute)
        {
            if (densityMap != _lastDensityMap || densityTexture3D != _lastDensityTexture3D || resolution != _lastResolution)
                ReleaseCachedRTs();
            if (Application.isPlaying || renderInEditMode)
                RecomputeMesh();
            else
                ClearMesh();
            _lastIsoLevel = isoLevel;
            _lastDensityMap = densityMap;
            _lastDensityTexture3D = densityTexture3D;
            _lastResolution = resolution;
            _lastRenderInEditMode = renderInEditMode;
            _lastVoxelSize = voxelSize;
        }
    }

    void ReleaseCachedRTs()
    {
        if (_cachedDownsampledFromRT != null)
        {
            _cachedDownsampledFromRT.Release();
            DestroyImmediate(_cachedDownsampledFromRT);
            _cachedDownsampledFromRT = null;
        }
        _cachedDownsampleSourceRT = null;
        _cachedDownsampleResRT = -1;
        if (_cachedDownsampled != null)
        {
            _cachedDownsampled.Release();
            DestroyImmediate(_cachedDownsampled);
            _cachedDownsampled = null;
        }
    }

    /// <summary>
    /// Invalidates the cached downsampled density map so the next RecomputeMesh() will rebuild from the current densityMap contents.
    /// Call this after editing the densityMap in place (e.g. from MarchingCubesEditor).
    /// </summary>
    public void InvalidateDensityCache()
    {
        ReleaseCachedRTs();
    }

    /// <summary>
    /// Recomputes the mesh from the current density texture and iso level, and assigns it to the MeshFilter.
    /// </summary>
    public void RecomputeMesh()
    {
        if (_core == null) return;

        ConvertTexture3DToRenderTexture();
        if (densityMap == null) return;

        if (resolution > 1)
        {
            bool cacheValid = _cachedDownsampledFromRT != null && _cachedDownsampleSourceRT == densityMap && _cachedDownsampleResRT == resolution;
            if (!cacheValid)
            {
                if (_cachedDownsampledFromRT != null)
                {
                    _cachedDownsampledFromRT.Release();
                    DestroyImmediate(_cachedDownsampledFromRT);
                    _cachedDownsampledFromRT = null;
                }
                _cachedDownsampledFromRT = DownsampleDensityMap(densityMap, resolution);
                _cachedDownsampleSourceRT = densityMap;
                _cachedDownsampleResRT = resolution;
            }
            _core.Run(_cachedDownsampledFromRT, isoLevel, voxelSize * resolution);
            AssignMeshToFilter();
        }
        else
        {
            _core.Run(densityMap, isoLevel, voxelSize);
            AssignMeshToFilter();
        }
    }

    void OnDestroy()
    {
        ReleaseCachedRTs();
        _core?.Release();
    }

    void ConvertTexture3DToRenderTexture()
    {
        if (densityTexture3D == null)
        {
            _lastConvertedTexture3D = null;
            return;
        }

        if (densityTexture3D == _lastConvertedTexture3D)
            return;

        if (densityTexture3D.width <= 0 || densityTexture3D.height <= 0 || densityTexture3D.depth <= 0)
        {
            Debug.LogWarning("MarchingCubesRenderer: Invalid Texture3D dimensions. Cannot convert to RenderTexture.");
            return;
        }

        if (_texture3DToRenderTextureCS == null)
        {
            _texture3DToRenderTextureCS = Resources.Load<ComputeShader>("Texture3DToRenderTexture");
            if (_texture3DToRenderTextureCS == null)
            {
                Debug.LogError("MarchingCubesRenderer: Could not load Texture3DToRenderTexture compute shader from Resources.");
                return;
            }
        }

        if (densityMap != null)
        {
            densityMap.Release();
            DestroyImmediate(densityMap);
        }

        densityMap = new RenderTexture(densityTexture3D.width, densityTexture3D.height, 0, RenderTextureFormat.RFloat);
        densityMap.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        densityMap.volumeDepth = densityTexture3D.depth;
        densityMap.enableRandomWrite = true;
        densityMap.name = densityTexture3D.name + " (Render Texture)";
        densityMap.Create();

        int kernelIndex = _texture3DToRenderTextureCS.FindKernel("ConvertTexture3D");
        _texture3DToRenderTextureCS.SetTexture(kernelIndex, "SourceTexture", densityTexture3D);
        _texture3DToRenderTextureCS.SetTexture(kernelIndex, "TargetTexture", densityMap);
        _texture3DToRenderTextureCS.SetInts("textureSize", densityTexture3D.width, densityTexture3D.height, densityTexture3D.depth);

        int tx = Mathf.CeilToInt((float)densityTexture3D.width / 8f);
        int ty = Mathf.CeilToInt((float)densityTexture3D.height / 8f);
        int tz = Mathf.CeilToInt((float)densityTexture3D.depth / 8f);
        _texture3DToRenderTextureCS.Dispatch(kernelIndex, tx, ty, tz);

        _lastConvertedTexture3D = densityTexture3D;
    }

    RenderTexture DownsampleDensityMap(RenderTexture source, int resolution)
    {
        if (source == null || resolution <= 1) return source;
        if (_downsampleDensityMapCS == null)
        {
            Debug.LogError("MarchingCubesRenderer: DownsampleDensityMap compute shader not loaded.");
            return source;
        }

        int dw = Mathf.Max(1, source.width / resolution);
        int dh = Mathf.Max(1, source.height / resolution);
        int dd = Mathf.Max(1, source.volumeDepth / resolution);

        RenderTexture downsampled = new RenderTexture(dw, dh, 0, RenderTextureFormat.RFloat);
        downsampled.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        downsampled.volumeDepth = dd;
        downsampled.enableRandomWrite = true;
        downsampled.name = source.name + " (Downsampled x" + resolution + ")";
        downsampled.Create();

        int kernelIndex = _downsampleDensityMapCS.FindKernel("DownsampleDensityMap");
        _downsampleDensityMapCS.SetTexture(kernelIndex, "SourceTexture", source);
        _downsampleDensityMapCS.SetTexture(kernelIndex, "TargetTexture", downsampled);
        _downsampleDensityMapCS.SetInts("sourceSize", source.width, source.height, source.volumeDepth);
        _downsampleDensityMapCS.SetInts("targetSize", dw, dh, dd);
        _downsampleDensityMapCS.SetInt("resolution", resolution);

        int tx = Mathf.CeilToInt((float)dw / 8f);
        int ty = Mathf.CeilToInt((float)dh / 8f);
        int tz = Mathf.CeilToInt((float)dd / 8f);
        _downsampleDensityMapCS.Dispatch(kernelIndex, tx, ty, tz);

        return downsampled;
    }
}
}
