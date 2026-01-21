using System;
using UnityEngine;
using System.Linq;
using System.Runtime.InteropServices;

public class MarchingCubes
{
    readonly ComputeShader marchingCubesCS;
    readonly ComputeBuffer lutBuffer;
    ComputeBuffer triangleBuffer;
    
    // Cached dimensions to avoid unnecessary buffer recreation
    int cachedWidth = -1;
    int cachedHeight = -1;
    int cachedDepth = -1;

    public MarchingCubes()
    {
        marchingCubesCS = Resources.Load<ComputeShader>("MarchingCubes");
        string lutString = Resources.Load<TextAsset>("MarchingCubesLUT").text;
        int[] lutVals = lutString.Trim().Split(',').Select(x => int.Parse(x)).ToArray();
        lutBuffer = new ComputeBuffer(lutVals.Length, sizeof(int), ComputeBufferType.Default);
        lutBuffer.SetData(lutVals);

    }

    void ApplyComputeSettings(RenderTexture densityMap, float isoLevel, ComputeBuffer triangleBuffer)
    {
        marchingCubesCS.SetBuffer(0, "triangles", triangleBuffer);
        marchingCubesCS.SetBuffer(0, "lut", lutBuffer);

        marchingCubesCS.SetTexture(0, "DensityMap", densityMap);
        marchingCubesCS.SetInts("densityMapSize", densityMap.width, densityMap.height, densityMap.volumeDepth);
        marchingCubesCS.SetFloat("isoLevel", isoLevel);
    }
    
    void ApplyComputeSettings(Texture3D densityMap, float isoLevel, ComputeBuffer triangleBuffer)
    {
        marchingCubesCS.SetBuffer(0, "triangles", triangleBuffer);
        marchingCubesCS.SetBuffer(0, "lut", lutBuffer);

        marchingCubesCS.SetTexture(0, "DensityMap", densityMap);
        marchingCubesCS.SetInts("densityMapSize", densityMap.width, densityMap.height, densityMap.depth);
        marchingCubesCS.SetFloat("isoLevel", isoLevel);
    }

    public ComputeBuffer Run(RenderTexture densityTexture, float isoLevel)
    {
        CreateTriangleBuffer(densityTexture.width, densityTexture.height, densityTexture.volumeDepth);
        ApplyComputeSettings(densityTexture, isoLevel, triangleBuffer);

        int numVoxelsPerX = densityTexture.width - 1;
        int numVoxelsPerY = densityTexture.height - 1;
        int numVoxelsPerZ = densityTexture.volumeDepth - 1;
        marchingCubesCS.Dispatch(0, numVoxelsPerX, numVoxelsPerY, numVoxelsPerZ);

        return triangleBuffer;
    }
    
    public ComputeBuffer Run(Texture3D densityTexture, float isoLevel)
    {
        CreateTriangleBuffer(densityTexture.width, densityTexture.height, densityTexture.depth);
        ApplyComputeSettings(densityTexture, isoLevel, triangleBuffer);

        int numVoxelsPerX = densityTexture.width - 1;
        int numVoxelsPerY = densityTexture.height - 1;
        int numVoxelsPerZ = densityTexture.depth - 1;
        marchingCubesCS.Dispatch(0, numVoxelsPerX, numVoxelsPerY, numVoxelsPerZ);

        return triangleBuffer;
    }

    void CreateTriangleBuffer(int width, int height, int depth)
    {
        // Always recreate buffer to reset append counter (needed even if dimensions unchanged)
        // Dimension caching is maintained for potential future optimizations
        triangleBuffer?.Release();
        int numVoxelsPerX = width - 1;
        int numVoxelsPerY = height - 1;
        int numVoxelsPerZ = depth - 1;
        int numVoxels = numVoxelsPerX * numVoxelsPerY * numVoxelsPerZ;
        int maxTriangleCount = numVoxels * 5;
        int stride = Marshal.SizeOf<Triangle>();
        const uint maxBytes = 2147483648;
        uint maxEntries = maxBytes / (uint)stride;

        triangleBuffer = new ComputeBuffer(Math.Min((int)maxEntries, maxTriangleCount), stride, ComputeBufferType.Append);
        
        // Cache dimensions
        cachedWidth = width;
        cachedHeight = height;
        cachedDepth = depth;
    }

    public void Release()
    {
        triangleBuffer?.Release();
        lutBuffer?.Release();
    }

    public struct Vertex
    {
        public Vector3 position;
        public Vector3 normal;
    }

    public struct Triangle
    {
        public Vertex vertexA;
        public Vertex vertexB;
        public Vertex vertexC;
    }
}