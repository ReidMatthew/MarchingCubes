Shader "MarchingCubes/MarchingCubesDraw"
{
	Properties
	{
		_MainTex ("Texture", 2D) = "white" {}
	}
	SubShader
	{
		Tags { "RenderType"="Opaque" }
		LOD 100

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			
			struct v2f
			{
				float4 vertex : SV_POSITION;
				float3 normal : TEXCOORD0;
			};
			
			
			struct appdata
			{
				uint vertexID : SV_VertexID;
			};

			struct Vertex {
				float3 position;
				float3 normal;
			};

			StructuredBuffer<Vertex> VertexBuffer;
			float4 col;
			float4x4 _ObjectToWorld;
			
			v2f vert (appdata v)
			{
				v2f o;
				float3 vertPos = VertexBuffer[v.vertexID].position;
				float3 normal = VertexBuffer[v.vertexID].normal;
				
				// Transform position from local space to world space using the transform matrix
				float4 worldPos = mul(_ObjectToWorld, float4(vertPos, 1.0));
				o.vertex = UnityWorldToClipPos(worldPos.xyz);
				
				// Transform normal to world space
				float3 worldNormal = mul((float3x3)_ObjectToWorld, normal);
				o.normal = worldNormal;
				return o;
			}

			float4 frag (v2f i) : SV_Target
			{
				float3 lightDir = _WorldSpaceLightPos0;
				float shading = dot(lightDir, normalize(i.normal)) * 0.5 + 0.5;
				return col * shading;
			}

			ENDCG
		}
	}
}
