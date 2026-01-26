using UnityEngine;

public class MarchingCubesRenderer : MonoBehaviour
{
    [Range(-1f, 1f)]
    public float isoLevel;
    public Color col;
    
    [Header("Level of Detail")]
    [Range(1, 8)]
    [Tooltip("Resolution factor for level of detail. 1 = use every voxel, 2 = average 2x2x2 voxels, etc.")]
    public int resolution = 1;

    [Header("References")]
    [Tooltip("3D RenderTexture containing the density map (takes priority if both are set)")]
    public RenderTexture densityMap;
    
    [Tooltip("3D Texture containing the density map (automatically converted to RenderTexture and assigned to densityMap when set)")]
    public Texture3D densityTexture3D;
    
    public Shader drawShader;
    public ComputeShader renderArgsCompute;

    ComputeBuffer renderArgs; 
    MarchingCubes marchingCubes;
    ComputeBuffer triangleBuffer;
    Material drawMat;
    Bounds bounds = new Bounds(Vector3.zero, Vector3.one * 1000);
    
    // Fields for automatic Texture3D to RenderTexture conversion
    Texture3D lastConvertedTexture3D;
    ComputeShader texture3DToRenderTextureCS;
    
    // Field for downsampling compute shader
    ComputeShader downsampleDensityMapCS;
    
    // Track last values to prevent redundant recomputations
    float lastIsoLevel = float.MinValue;
    RenderTexture lastDensityMap;
    Texture3D lastDensityTexture3D;
    int lastResolution = -1;

    void Start()
    {
        // Initialize material early
        if (drawShader != null)
        {
            drawMat = new Material(drawShader);
        }
        
        // Load compute shader early
        texture3DToRenderTextureCS = Resources.Load<ComputeShader>("Texture3DToRenderTexture");
        if (texture3DToRenderTextureCS == null)
        {
            Debug.LogError("MarchingCubesRenderer: Could not load Texture3DToRenderTexture compute shader from Resources.");
        }
        
        // Load downsampling compute shader
        downsampleDensityMapCS = Resources.Load<ComputeShader>("DownsampleDensityMap");
        if (downsampleDensityMapCS == null)
        {
            Debug.LogError("MarchingCubesRenderer: Could not load DownsampleDensityMap compute shader from Resources.");
        }
        
        marchingCubes = new MarchingCubes();
        
        // Initialize tracking values
        lastIsoLevel = isoLevel;
        lastDensityMap = densityMap;
        lastDensityTexture3D = densityTexture3D;
        lastResolution = resolution;
        
        RecomputeMesh();
    }

    void LateUpdate()
    {
        // Only render - recomputation is handled separately when needed
        Render();
    }
    
    void OnValidate()
    {
        // Automatically convert Texture3D to RenderTexture when assigned
        ConvertTexture3DToRenderTexture();
        
        // Only recompute if values actually changed
        bool needsRecompute = (marchingCubes != null) && (
            isoLevel != lastIsoLevel ||
            densityMap != lastDensityMap ||
            densityTexture3D != lastDensityTexture3D ||
            resolution != lastResolution
        );
        
        if (needsRecompute)
        {
            RecomputeMesh();
            lastIsoLevel = isoLevel;
            lastDensityMap = densityMap;
            lastDensityTexture3D = densityTexture3D;
            lastResolution = resolution;
        }
    }
    
    /// <summary>
    /// Recomputes the mesh based on the current density texture and iso level.
    /// Call this method when the render texture is updated/set/changed, or when the iso value is changed.
    /// </summary>
    public void RecomputeMesh()
    {
        if (marchingCubes == null)
        {
            return;
        }
        
        // Direct field checks instead of GetDensityTexture() - more efficient
        if (densityMap != null)
        {
            // Downsample if resolution > 1
            if (resolution > 1)
            {
                RenderTexture downsampled = DownsampleDensityMap(densityMap, resolution);
                triangleBuffer = marchingCubes.Run(downsampled, isoLevel);
                // Clean up temporary downsampled texture
                downsampled.Release();
                DestroyImmediate(downsampled);
            }
            else
            {
                triangleBuffer = marchingCubes.Run(densityMap, isoLevel);
            }
        }
        else if (densityTexture3D != null)
        {
            // If resolution is 1, use Texture3D directly (no conversion needed)
            if (resolution == 1)
            {
                triangleBuffer = marchingCubes.Run(densityTexture3D, isoLevel);
            }
            else
            {
                // For downsampling, we need to convert Texture3D to RenderTexture first
                RenderTexture converted = new RenderTexture(densityTexture3D.width, densityTexture3D.height, 0, RenderTextureFormat.RFloat);
                converted.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
                converted.volumeDepth = densityTexture3D.depth;
                converted.enableRandomWrite = true;
                converted.Create();
                
                // Convert Texture3D to RenderTexture
                if (texture3DToRenderTextureCS != null)
                {
                    int kernelIndex = texture3DToRenderTextureCS.FindKernel("ConvertTexture3D");
                    texture3DToRenderTextureCS.SetTexture(kernelIndex, "SourceTexture", densityTexture3D);
                    texture3DToRenderTextureCS.SetTexture(kernelIndex, "TargetTexture", converted);
                    texture3DToRenderTextureCS.SetInts("textureSize", densityTexture3D.width, densityTexture3D.height, densityTexture3D.depth);
                    
                    int threadGroupsX = Mathf.CeilToInt((float)densityTexture3D.width / 8f);
                    int threadGroupsY = Mathf.CeilToInt((float)densityTexture3D.height / 8f);
                    int threadGroupsZ = Mathf.CeilToInt((float)densityTexture3D.depth / 8f);
                    texture3DToRenderTextureCS.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
                }
                
                // Downsample the converted texture
                RenderTexture downsampled = DownsampleDensityMap(converted, resolution);
                triangleBuffer = marchingCubes.Run(downsampled, isoLevel);
                
                // Clean up temporary textures
                converted.Release();
                DestroyImmediate(converted);
                downsampled.Release();
                DestroyImmediate(downsampled);
            }
        }
        // No redundant buffer release - handled in MarchingCubes.CreateTriangleBuffer() when dimensions change
    }
    
    void Render()
    {
        if (triangleBuffer == null || drawMat == null)
        {
            return;
        }
        
        // Each triangle contains 3 vertices: assign these all to the vertex buffer on the draw material
        drawMat.SetBuffer("VertexBuffer", triangleBuffer);
        drawMat.SetColor("col", col);
        
        // Pass the transform matrix to the shader so it can position the mesh correctly
        drawMat.SetMatrix("_ObjectToWorld", transform.localToWorldMatrix);
        
        // Create render arguments. This stores 5 values:
        // (triangle index count, instance count, sub-mesh index, base vertex index, byte offset)
        if (renderArgs == null)
        {
            renderArgs = new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments);
            renderArgsCompute.SetBuffer(0, "RenderArgs", renderArgs);
        }

        // Copy the current number of triangles from the append buffer into the render arguments.
        // (Each triangle contains 3 vertices, so we then need to multiply this value by 3 with another dispatch)
        ComputeBuffer.CopyCount(triangleBuffer, renderArgs, 0);
        renderArgsCompute.Dispatch(0, 1, 1, 1);
        
        // Update bounds to be centered at transform position for proper culling
        bounds.center = transform.position;
        bounds.size = Vector3.one * 1000;
        
        // Draw the mesh using ProceduralIndirect to avoid having to read any data back to the CPU
        Graphics.DrawProceduralIndirect(drawMat, bounds, MeshTopology.Triangles, renderArgs);
    }

    private void OnDestroy()
    {
        Release();
    }

    void Release()
    {
        renderArgs?.Release();
        marchingCubes?.Release();
    }
    
    /// <summary>
    /// Automatically converts Texture3D to RenderTexture when densityTexture3D is assigned.
    /// This happens automatically in the inspector via OnValidate().
    /// </summary>
    void ConvertTexture3DToRenderTexture()
    {
        // Check if densityTexture3D is assigned and different from last converted
        if (densityTexture3D == null)
        {
            lastConvertedTexture3D = null;
            return;
        }
        
        // Skip if this Texture3D was already converted
        if (densityTexture3D == lastConvertedTexture3D)
        {
            return;
        }
        
        // Validate texture dimensions
        if (densityTexture3D.width <= 0 || densityTexture3D.height <= 0 || densityTexture3D.depth <= 0)
        {
            Debug.LogWarning("MarchingCubesRenderer: Invalid Texture3D dimensions. Cannot convert to RenderTexture.");
            return;
        }
        
        // Load compute shader if not already loaded (handles case where OnValidate is called before Start)
        if (texture3DToRenderTextureCS == null)
        {
            texture3DToRenderTextureCS = Resources.Load<ComputeShader>("Texture3DToRenderTexture");
            if (texture3DToRenderTextureCS == null)
            {
                Debug.LogError("MarchingCubesRenderer: Could not load Texture3DToRenderTexture compute shader from Resources.");
                return;
            }
        }
        
        // Clean up old RenderTexture if it exists and is being replaced
        if (densityMap != null)
        {
            densityMap.Release();
            DestroyImmediate(densityMap);
        }
        
        // Create new RenderTexture with matching dimensions
        densityMap = new RenderTexture(densityTexture3D.width, densityTexture3D.height, 0, RenderTextureFormat.RFloat);
        densityMap.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        densityMap.volumeDepth = densityTexture3D.depth;
        densityMap.enableRandomWrite = true;
        densityMap.name = densityTexture3D.name + " (Render Texture)";
        densityMap.Create();
        
        // Set compute shader parameters
        int kernelIndex = texture3DToRenderTextureCS.FindKernel("ConvertTexture3D");
        texture3DToRenderTextureCS.SetTexture(kernelIndex, "SourceTexture", densityTexture3D);
        texture3DToRenderTextureCS.SetTexture(kernelIndex, "TargetTexture", densityMap);
        texture3DToRenderTextureCS.SetInts("textureSize", densityTexture3D.width, densityTexture3D.height, densityTexture3D.depth);
        
        // Dispatch compute shader
        // Thread group size is 8x8x8, so calculate number of groups needed
        int threadGroupsX = Mathf.CeilToInt((float)densityTexture3D.width / 8f);
        int threadGroupsY = Mathf.CeilToInt((float)densityTexture3D.height / 8f);
        int threadGroupsZ = Mathf.CeilToInt((float)densityTexture3D.depth / 8f);
        
        texture3DToRenderTextureCS.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
        
        // Track that we've converted this Texture3D
        lastConvertedTexture3D = densityTexture3D;
        
        Debug.Log($"MarchingCubesRenderer: Automatically converted Texture3D ({densityTexture3D.width}x{densityTexture3D.height}x{densityTexture3D.depth}) to RenderTexture.");
    }
    
    /// <summary>
    /// Downsamples a RenderTexture by averaging resolution^3 voxels into each output voxel.
    /// Returns a new RenderTexture with dimensions divided by resolution.
    /// </summary>
    RenderTexture DownsampleDensityMap(RenderTexture source, int resolution)
    {
        if (source == null || resolution <= 1)
        {
            return source;
        }
        
        if (downsampleDensityMapCS == null)
        {
            Debug.LogError("MarchingCubesRenderer: DownsampleDensityMap compute shader not loaded.");
            return source;
        }
        
        // Calculate downsampled dimensions
        int downsampledWidth = Mathf.Max(1, source.width / resolution);
        int downsampledHeight = Mathf.Max(1, source.height / resolution);
        int downsampledDepth = Mathf.Max(1, source.volumeDepth / resolution);
        
        // Create downsampled RenderTexture
        RenderTexture downsampled = new RenderTexture(downsampledWidth, downsampledHeight, 0, RenderTextureFormat.RFloat);
        downsampled.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        downsampled.volumeDepth = downsampledDepth;
        downsampled.enableRandomWrite = true;
        downsampled.name = source.name + " (Downsampled x" + resolution + ")";
        downsampled.Create();
        
        // Set compute shader parameters
        int kernelIndex = downsampleDensityMapCS.FindKernel("DownsampleDensityMap");
        downsampleDensityMapCS.SetTexture(kernelIndex, "SourceTexture", source);
        downsampleDensityMapCS.SetTexture(kernelIndex, "TargetTexture", downsampled);
        downsampleDensityMapCS.SetInts("sourceSize", source.width, source.height, source.volumeDepth);
        downsampleDensityMapCS.SetInts("targetSize", downsampledWidth, downsampledHeight, downsampledDepth);
        downsampleDensityMapCS.SetInt("resolution", resolution);
        
        // Dispatch compute shader
        // Thread group size is 8x8x8, so calculate number of groups needed
        int threadGroupsX = Mathf.CeilToInt((float)downsampledWidth / 8f);
        int threadGroupsY = Mathf.CeilToInt((float)downsampledHeight / 8f);
        int threadGroupsZ = Mathf.CeilToInt((float)downsampledDepth / 8f);
        
        downsampleDensityMapCS.Dispatch(kernelIndex, threadGroupsX, threadGroupsY, threadGroupsZ);
        
        return downsampled;
    }

}

