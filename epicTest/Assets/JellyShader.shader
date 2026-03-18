Shader "Custom/Jelly"
{
	Properties
	{
		_MainTex("Base Texture", 2D) = "white" {}
		_JellyColor("Jelly Color", Color) = (0.2, 0.8, 0.5, 1.0)
		_Alpha("Jelly Alpha", Range(0,1)) = 0.7

		[Header(Jelly Wobble)]
		_WobbleAmount("Wobble Amount", Range(0, 0.3)) = 0.08
		_WobbleSpeed("Wobble Speed", Range(0, 10)) = 3.0
		_WobbleFrequency("Wobble Frequency", Range(0.5, 10)) = 2.5
		_SquishAmount("Squish Amount", Range(0, 0.15)) = 0.04
		_SquishSpeed("Squish Speed", Range(0, 8)) = 1.8

		[Header(Subsurface Scattering)]
		_SSSColor("Subsurface Color", Color) = (1.0, 0.4, 0.2, 1.0)
		_SSSIntensity("Subsurface Intensity", Range(0, 3)) = 1.2
		_SSSPower("Subsurface Power", Range(1, 16)) = 4.0
		_SSSDistortion("Subsurface Distortion", Range(0, 1)) = 0.3
		_Thickness("Thickness Map Bias", Range(0, 1)) = 0.5

		[Header(Rim and Specular)]
		_RimColor("Rim Color", Color) = (0.6, 1.0, 0.8, 1.0)
		_RimPower("Rim Power", Range(0.5, 8)) = 2.5
		_RimIntensity("Rim Intensity", Range(0, 2)) = 0.8
		_SpecColor("Specular Color", Color) = (1, 1, 1, 1)
		_SpecPower("Specular Power", Range(8, 256)) = 64
		_SpecIntensity("Specular Intensity", Range(0, 2)) = 0.6

		[Header(Internal Refraction)]
		_RefractionStrength("Refraction Strength", Range(0, 0.15)) = 0.04
		_InternalNoiseScale("Internal Noise Scale", Range(1, 30)) = 8.0
		_InternalNoiseSpeed("Internal Noise Speed", Range(0, 3)) = 0.5
		_InternalNoiseIntensity("Internal Noise Intensity", Range(0, 1)) = 0.15
	}

	SubShader
	{
		Tags { "Queue"="Transparent" "RenderType"="Transparent" }
		LOD 200

		// Back face pass for depth illusion
		Pass
		{
			Cull Front
			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment fragBack
			#pragma multi_compile_fog
			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float3 normal : NORMAL;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float4 vertex : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float3 worldNormal : TEXCOORD2;
				float3 objPos : TEXCOORD3;
				UNITY_FOG_COORDS(4)
			};

			float4 _JellyColor;
			float _Alpha;
			float _WobbleAmount;
			float _WobbleSpeed;
			float _WobbleFrequency;
			float _SquishAmount;
			float _SquishSpeed;

			v2f vert(appdata v)
			{
				v2f o;

				// Jelly wobble displacement - position-only based (no normal dependency)
				// This ensures vertices at the same position get identical offsets
				// even on hard-edged meshes like cubes where normals differ per face.
				float3 worldOrig = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
				float phase = dot(worldOrig, float3(1.17, 2.31, 0.73));
				float t = _Time.y;

				// Use normalized object position as displacement direction
				float3 posDir = normalize(v.vertex.xyz + float3(0.001, 0.001, 0.001));

				float3 offset = float3(0,0,0);
				offset.x = sin(t * _WobbleSpeed + v.vertex.y * _WobbleFrequency + v.vertex.z * _WobbleFrequency * 0.7 + phase) * _WobbleAmount * posDir.x;
				offset.y = sin(t * _WobbleSpeed * 1.1 + v.vertex.x * _WobbleFrequency * 0.8 + v.vertex.z * _WobbleFrequency * 0.6 + phase + 1.57) * _WobbleAmount * 0.5;
				offset.z = sin(t * _WobbleSpeed * 0.9 + v.vertex.y * _WobbleFrequency * 1.2 + v.vertex.x * _WobbleFrequency * 0.5 + phase + 3.14) * _WobbleAmount * posDir.z;

				// Breathing squish (compress Y, expand XZ)
				float squish = sin(t * _SquishSpeed + phase) * _SquishAmount;
				offset.y -= squish * v.vertex.y;
				offset.x += squish * 0.5 * v.vertex.x;
				offset.z += squish * 0.5 * v.vertex.z;

				float4 displaced = v.vertex + float4(offset, 0);
				o.vertex = UnityObjectToClipPos(displaced);
				o.uv = v.uv;
				o.worldPos = mul(unity_ObjectToWorld, displaced).xyz;
				o.worldNormal = -UnityObjectToWorldNormal(v.normal);
				o.objPos = displaced.xyz;
				UNITY_TRANSFER_FOG(o, o.vertex);
				return o;
			}

			fixed4 fragBack(v2f i) : SV_Target
			{
				float3 col = _JellyColor.rgb * 0.4;
				return fixed4(col, _Alpha * 0.3);
			}
			ENDCG
		}

		// Front face pass - main jelly rendering
		Pass
		{
			Cull Back
			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_fog
			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float3 normal : NORMAL;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float4 vertex : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float3 worldNormal : TEXCOORD2;
				float3 objPos : TEXCOORD3;
				UNITY_FOG_COORDS(4)
			};

			sampler2D _MainTex;
			float4 _MainTex_ST;
			float4 _JellyColor;
			float _Alpha;

			float _WobbleAmount;
			float _WobbleSpeed;
			float _WobbleFrequency;
			float _SquishAmount;
			float _SquishSpeed;

			float4 _SSSColor;
			float _SSSIntensity;
			float _SSSPower;
			float _SSSDistortion;
			float _Thickness;

			float4 _RimColor;
			float _RimPower;
			float _RimIntensity;
			float4 _SpecColor;
			float _SpecPower;
			float _SpecIntensity;

			float _RefractionStrength;
			float _InternalNoiseScale;
			float _InternalNoiseSpeed;
			float _InternalNoiseIntensity;

			float hash31(float3 p)
			{
				p = frac(p * 0.1031);
				p += dot(p, p.yzx + 33.33);
				return frac((p.x + p.y) * p.z);
			}

			float noise3(float3 p)
			{
				float3 i = floor(p);
				float3 f = frac(p);
				float3 u = f * f * (3.0 - 2.0 * f);

				float n000 = hash31(i + float3(0,0,0));
				float n100 = hash31(i + float3(1,0,0));
				float n010 = hash31(i + float3(0,1,0));
				float n110 = hash31(i + float3(1,1,0));
				float n001 = hash31(i + float3(0,0,1));
				float n101 = hash31(i + float3(1,0,1));
				float n011 = hash31(i + float3(0,1,1));
				float n111 = hash31(i + float3(1,1,1));

				float nx00 = lerp(n000, n100, u.x);
				float nx10 = lerp(n010, n110, u.x);
				float nx01 = lerp(n001, n101, u.x);
				float nx11 = lerp(n011, n111, u.x);
				float nxy0 = lerp(nx00, nx10, u.y);
				float nxy1 = lerp(nx01, nx11, u.y);
				return lerp(nxy0, nxy1, u.z);
			}

			v2f vert(appdata v)
			{
				v2f o;

				// Jelly wobble displacement - position-only based (no normal dependency)
				// This ensures vertices at the same position get identical offsets
				// even on hard-edged meshes like cubes where normals differ per face.
				float3 worldOrig = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
				float phase = dot(worldOrig, float3(1.17, 2.31, 0.73));
				float t = _Time.y;

				// Use normalized object position as displacement direction
				float3 posDir = normalize(v.vertex.xyz + float3(0.001, 0.001, 0.001));

				float3 offset = float3(0,0,0);
				offset.x = sin(t * _WobbleSpeed + v.vertex.y * _WobbleFrequency + v.vertex.z * _WobbleFrequency * 0.7 + phase) * _WobbleAmount * posDir.x;
				offset.y = sin(t * _WobbleSpeed * 1.1 + v.vertex.x * _WobbleFrequency * 0.8 + v.vertex.z * _WobbleFrequency * 0.6 + phase + 1.57) * _WobbleAmount * 0.5;
				offset.z = sin(t * _WobbleSpeed * 0.9 + v.vertex.y * _WobbleFrequency * 1.2 + v.vertex.x * _WobbleFrequency * 0.5 + phase + 3.14) * _WobbleAmount * posDir.z;

				// Breathing squish (compress Y, expand XZ)
				float squish = sin(t * _SquishSpeed + phase) * _SquishAmount;
				offset.y -= squish * v.vertex.y;
				offset.x += squish * 0.5 * v.vertex.x;
				offset.z += squish * 0.5 * v.vertex.z;

				float4 displaced = v.vertex + float4(offset, 0);
				o.vertex = UnityObjectToClipPos(displaced);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				o.worldPos = mul(unity_ObjectToWorld, displaced).xyz;
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				o.objPos = displaced.xyz;
				UNITY_TRANSFER_FOG(o, o.vertex);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				float3 N = normalize(i.worldNormal);
				float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
				float NdotV = saturate(dot(N, V));

				// Light direction (main directional light)
				float3 L = normalize(_WorldSpaceLightPos0.xyz);

				// Base color with refraction distortion
				float2 refractUV = i.uv + N.xy * _RefractionStrength * (1.0 - NdotV);
				fixed4 baseTex = tex2D(_MainTex, refractUV);
				float3 baseCol = baseTex.rgb * _JellyColor.rgb;

				// Internal noise pattern (simulates jelly interior)
				float3 noisePos = i.objPos * _InternalNoiseScale + _Time.y * _InternalNoiseSpeed;
				float internalNoise = noise3(noisePos) * 0.6 + noise3(noisePos * 2.3 + 5.7) * 0.4;
				baseCol = lerp(baseCol, baseCol * (0.8 + internalNoise * 0.4), _InternalNoiseIntensity);

				// Subsurface scattering approximation
				float3 sssNormal = N + L * _SSSDistortion;
				float sssDot = saturate(dot(V, -sssNormal));
				float sss = pow(sssDot, _SSSPower) * _SSSIntensity;
				float thicknessFactor = _Thickness + (1.0 - _Thickness) * (1.0 - NdotV);
				float3 sssContrib = _SSSColor.rgb * sss * thicknessFactor;

				// Diffuse lighting (soft wrap lighting for jelly)
				float NdotL = dot(N, L);
				float diffuse = saturate(NdotL * 0.5 + 0.5); // half-lambert
				float3 diffuseCol = baseCol * diffuse;

				// Fresnel rim lighting
				float rim = pow(1.0 - NdotV, _RimPower);
				float3 rimCol = _RimColor.rgb * rim * _RimIntensity;

				// Specular highlight (soft and broad for jelly)
				float3 H = normalize(V + L);
				float NdotH = saturate(dot(N, H));
				float spec = pow(NdotH, _SpecPower) * _SpecIntensity;
				// Secondary broader highlight for jelly sheen
				float specBroad = pow(NdotH, _SpecPower * 0.25) * _SpecIntensity * 0.2;
				float3 specCol = _SpecColor.rgb * (spec + specBroad);

				// Ambient
				float3 ambient = baseCol * 0.15;

				// Combine
				float3 finalCol = ambient + diffuseCol + sssContrib + rimCol + specCol;

				// Alpha: more opaque at edges (Fresnel), base alpha in center
				float edgeAlpha = lerp(_Alpha, min(_Alpha + 0.2, 1.0), rim);

				UNITY_APPLY_FOG(i.fogCoord, finalCol);
				return fixed4(saturate(finalCol), edgeAlpha);
			}
			ENDCG
		}
	}
}
