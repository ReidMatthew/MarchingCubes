using UnityEngine;

namespace MarchingCubes
{
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class MarchingCubesRenderer : MonoBehaviour
{
    [Range(-1f, 1f)]
    public float isoLevel;

    [Header("Level of Detail")]
    [Range(1, 8)]
    [Tooltip("Resolution factor for level of detail. 1 = use every voxel, 2 = average 2x2x2 voxels, etc.")]
    public int resolution = 1;

    [Header("References")]
    [Tooltip("3D RenderTexture containing the density map (takes priority if both are set)")]
    public RenderTexture densityMap;

    [Tooltip("3D Texture containing the density map (automatically converted to RenderTexture and assigned to densityMap when set)")]
    public Texture3D densityTexture3D;

    MarchingCubesCore _core;
    ComputeShader _texture3DToRenderTextureCS;
    ComputeShader _downsampleDensityMapCS;
    Texture3D _lastConvertedTexture3D;

    RenderTexture _cachedConverted;
    RenderTexture _cachedDownsampled;
    Texture3D _cachedConvertedSource;
    int _cachedDownsampleResolution = -1;

    RenderTexture _cachedDownsampledFromRT;
    RenderTexture _cachedDownsampleSourceRT;
    int _cachedDownsampleResRT = -1;

    float _lastIsoLevel = float.MinValue;
    RenderTexture _lastDensityMap;
    Texture3D _lastDensityTexture3D;
    int _lastResolution = -1;

    void Start()
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

        RecomputeMesh();
    }

    string GetMeshName()
    {
        string baseName = densityTexture3D != null ? densityTexture3D.name : densityMap != null ? densityMap.name : "MarchingCubes";
        return baseName + " (Mesh)";
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
    }

    void OnValidate()
    {
        ConvertTexture3DToRenderTexture();

        bool needsRecompute = _core != null && (
            isoLevel != _lastIsoLevel ||
            densityMap != _lastDensityMap ||
            densityTexture3D != _lastDensityTexture3D ||
            resolution != _lastResolution
        );

        if (needsRecompute)
        {
            if (densityMap != _lastDensityMap || densityTexture3D != _lastDensityTexture3D || resolution != _lastResolution)
                ReleaseCachedRTs();
            RecomputeMesh();
            _lastIsoLevel = isoLevel;
            _lastDensityMap = densityMap;
            _lastDensityTexture3D = densityTexture3D;
            _lastResolution = resolution;
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
        if (_cachedConverted != null)
        {
            _cachedConverted.Release();
            DestroyImmediate(_cachedConverted);
            _cachedConverted = null;
        }
        _cachedConvertedSource = null;
        _cachedDownsampleResolution = -1;
    }

    /// <summary>
    /// Recomputes the mesh from the current density texture and iso level, and assigns it to the MeshFilter.
    /// </summary>
    public void RecomputeMesh()
    {
        if (_core == null) return;

        if (densityMap != null)
        {
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
                _core.RunAsync(_cachedDownsampledFromRT, isoLevel);
            }
            else
            {
                _core.RunAsync(densityMap, isoLevel);
            }
        }
        else if (densityTexture3D != null)
        {
            if (resolution == 1)
            {
                _core.RunAsync(densityTexture3D, isoLevel);
            }
            else
            {
                bool needConverted = _cachedConverted == null || _cachedConvertedSource != densityTexture3D ||
                    _cachedConverted.width != densityTexture3D.width || _cachedConverted.height != densityTexture3D.height || _cachedConverted.volumeDepth != densityTexture3D.depth;
                if (needConverted)
                {
                    ReleaseCachedRTs();
                    _cachedConverted = new RenderTexture(densityTexture3D.width, densityTexture3D.height, 0, RenderTextureFormat.RFloat);
                    _cachedConverted.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
                    _cachedConverted.volumeDepth = densityTexture3D.depth;
                    _cachedConverted.enableRandomWrite = true;
                    _cachedConverted.Create();

                    if (_texture3DToRenderTextureCS != null)
                    {
                        int kernelIndex = _texture3DToRenderTextureCS.FindKernel("ConvertTexture3D");
                        _texture3DToRenderTextureCS.SetTexture(kernelIndex, "SourceTexture", densityTexture3D);
                        _texture3DToRenderTextureCS.SetTexture(kernelIndex, "TargetTexture", _cachedConverted);
                        _texture3DToRenderTextureCS.SetInts("textureSize", densityTexture3D.width, densityTexture3D.height, densityTexture3D.depth);

                        int tx = Mathf.CeilToInt((float)densityTexture3D.width / 8f);
                        int ty = Mathf.CeilToInt((float)densityTexture3D.height / 8f);
                        int tz = Mathf.CeilToInt((float)densityTexture3D.depth / 8f);
                        _texture3DToRenderTextureCS.Dispatch(kernelIndex, tx, ty, tz);
                    }
                    _cachedConvertedSource = densityTexture3D;
                }

                bool needDownsampled = _cachedDownsampled == null || _cachedDownsampleResolution != resolution || _cachedConvertedSource != densityTexture3D;
                if (needDownsampled)
                {
                    if (_cachedDownsampled != null)
                    {
                        _cachedDownsampled.Release();
                        DestroyImmediate(_cachedDownsampled);
                        _cachedDownsampled = null;
                    }
                    _cachedDownsampled = DownsampleDensityMap(_cachedConverted, resolution);
                    _cachedDownsampleResolution = resolution;
                }

                _core.RunAsync(_cachedDownsampled, isoLevel);
            }
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
