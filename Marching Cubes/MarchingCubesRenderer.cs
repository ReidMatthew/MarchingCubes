using UnityEngine;

public class MarchingCubesRenderer : MonoBehaviour
{
    public float isoLevel;
    public Color col;

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
    
    // Track last values to prevent redundant recomputations
    float lastIsoLevel = float.MinValue;
    RenderTexture lastDensityMap;
    Texture3D lastDensityTexture3D;

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
        
        marchingCubes = new MarchingCubes();
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
            densityTexture3D != lastDensityTexture3D
        );
        
        if (needsRecompute)
        {
            RecomputeMesh();
            lastIsoLevel = isoLevel;
            lastDensityMap = densityMap;
            lastDensityTexture3D = densityTexture3D;
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
            triangleBuffer = marchingCubes.Run(densityMap, isoLevel);
        }
        else if (densityTexture3D != null)
        {
            triangleBuffer = marchingCubes.Run(densityTexture3D, isoLevel);
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

}

