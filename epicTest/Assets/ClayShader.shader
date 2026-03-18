Shader "Custom/Clay"
{
	Properties
	{
		_MainTex("Base Texture", 2D) = "white" {}
		_ClayColor("Clay Color", Color) = (0.76, 0.65, 0.52, 1.0)
		_ColorVariation("Color Variation", Range(0, 0.2)) = 0.06
		_ColorNoiseScale("Color Noise Scale", Range(1, 40)) = 12.0

		[Header(Surface Roughness)]
		_BumpScale("Fingerprint Depth", Range(0, 0.15)) = 0.05
		_BumpNoiseScale("Fingerprint Scale", Range(2, 60)) = 18.0
		_ToolMarkScale("Tool Mark Scale", Range(0.5, 10)) = 3.5
		_ToolMarkStrength("Tool Mark Strength", Range(0, 0.1)) = 0.03
		_SurfaceRoughness("Surface Roughness", Range(0, 1)) = 0.85

		[Header(Lighting)]
		_WrapAmount("Wrap Lighting", Range(0, 1)) = 0.45
		_DarkEdge("Edge Darkening", Range(0, 1)) = 0.3
		_DarkEdgePower("Edge Darkening Power", Range(1, 6)) = 2.0
		_AmbientBoost("Ambient Boost", Range(0, 0.5)) = 0.2
		_ShadowSoftness("Shadow Softness", Range(0, 1)) = 0.6

		[Header(Clay Wobble)]
		_WobbleAmount("Wobble Amount", Range(0, 0.15)) = 0.02
		_WobbleSpeed("Wobble Speed", Range(0, 8)) = 2.0
		_WobbleFrequency("Wobble Frequency", Range(0.5, 10)) = 3.0
		_SquishAmount("Squish Amount", Range(0, 0.1)) = 0.015
		_SquishSpeed("Squish Speed", Range(0, 6)) = 1.2
	}

	SubShader
	{
		Tags { "Queue"="Geometry" "RenderType"="Opaque" }
		LOD 200

		Pass
		{
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
			float4 _ClayColor;
			float _ColorVariation;
			float _ColorNoiseScale;

			float _BumpScale;
			float _BumpNoiseScale;
			float _ToolMarkScale;
			float _ToolMarkStrength;
			float _SurfaceRoughness;

			float _WrapAmount;
			float _DarkEdge;
			float _DarkEdgePower;
			float _AmbientBoost;
			float _ShadowSoftness;

			float _WobbleAmount;
			float _WobbleSpeed;
			float _WobbleFrequency;
			float _SquishAmount;
			float _SquishSpeed;

			// ── Noise functions ──

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

			// Fractional Brownian Motion for layered detail
			float fbm3(float3 p)
			{
				float v = 0.0;
				float a = 0.5;
				float3 shift = float3(100, 100, 100);
				for (int i = 0; i < 4; i++)
				{
					v += a * noise3(p);
					p = p * 2.0 + shift;
					a *= 0.5;
				}
				return v;
			}

			v2f vert(appdata v)
			{
				v2f o;

				float t = _Time.y;

				// Per-object phase from world origin
				float3 worldOrig = mul(unity_ObjectToWorld, float4(0,0,0,1)).xyz;
				float phase = dot(worldOrig, float3(1.17, 2.31, 0.73));

				// Position-based displacement (no normal dependency for hard-edge mesh compatibility)
				float3 posDir = normalize(v.vertex.xyz + float3(0.001, 0.001, 0.001));

				float3 offset = float3(0,0,0);
				offset.x = sin(t * _WobbleSpeed + v.vertex.y * _WobbleFrequency + v.vertex.z * _WobbleFrequency * 0.7 + phase) * _WobbleAmount * posDir.x;
				offset.y = sin(t * _WobbleSpeed * 1.1 + v.vertex.x * _WobbleFrequency * 0.8 + v.vertex.z * _WobbleFrequency * 0.6 + phase + 1.57) * _WobbleAmount * 0.5;
				offset.z = sin(t * _WobbleSpeed * 0.9 + v.vertex.y * _WobbleFrequency * 1.2 + v.vertex.x * _WobbleFrequency * 0.5 + phase + 3.14) * _WobbleAmount * posDir.z;

				// Breathing squish
				float squish = sin(t * _SquishSpeed + phase) * _SquishAmount;
				offset.y -= squish * v.vertex.y;
				offset.x += squish * 0.5 * v.vertex.x;
				offset.z += squish * 0.5 * v.vertex.z;

				// Fingerprint/tool mark vertex displacement
				// Use posDir instead of v.normal to avoid face separation on hard-edged meshes
				float3 bumpPos = v.vertex.xyz * _BumpNoiseScale;
				float bump = (noise3(bumpPos) - 0.5) * _BumpScale;
				// Tool marks: stretched noise along one axis
				float toolMark = (noise3(v.vertex.xyz * _ToolMarkScale + float3(77.7, 0, 0)) - 0.5) * _ToolMarkStrength;
				offset += posDir * (bump + toolMark);

				float4 displaced = v.vertex + float4(offset, 0);
				o.vertex = UnityObjectToClipPos(displaced);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				o.worldPos = mul(unity_ObjectToWorld, displaced).xyz;
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				o.objPos = v.vertex.xyz;
				UNITY_TRANSFER_FOG(o, o.vertex);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				float3 N = normalize(i.worldNormal);
				float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
				float3 L = normalize(_WorldSpaceLightPos0.xyz);
				float NdotV = saturate(dot(N, V));
				float NdotL = dot(N, L);

				// ── Base color with hand-made variation ──
				float3 baseCol = tex2D(_MainTex, i.uv).rgb * _ClayColor.rgb;

				// Color variation: subtle hue/value shifts across surface
				float3 colorNoisePos = i.objPos * _ColorNoiseScale;
				float cn1 = noise3(colorNoisePos) - 0.5;
				float cn2 = noise3(colorNoisePos * 1.7 + float3(31.7, 0, 13.1)) - 0.5;
				float cn3 = noise3(colorNoisePos * 0.6 + float3(0, 47.3, 0)) - 0.5;
				baseCol += float3(cn1, cn2, cn3) * _ColorVariation;

				// Fingerprint surface detail in fragment (adds visual texture)
				float3 detailPos = i.objPos * _BumpNoiseScale * 1.5;
				float surfaceDetail = fbm3(detailPos);
				// Mix detail into color as subtle lightness variation
				baseCol *= lerp(0.92, 1.08, surfaceDetail);

				// ── Wrap diffuse lighting (soft, clay-like) ──
				float wrap = (NdotL + _WrapAmount) / (1.0 + _WrapAmount);
				float diffuse = saturate(wrap);
				// Soften shadow transition
				diffuse = lerp(diffuse, smoothstep(0.0, 1.0, diffuse), _ShadowSoftness);

				// ── Edge darkening (clay edges are slightly darker) ──
				float edgeDark = 1.0 - _DarkEdge * pow(1.0 - NdotV, _DarkEdgePower);

				// ── Very subtle matte specular (clay is not shiny but has faint sheen) ──
				float3 H = normalize(V + L);
				float NdotH = saturate(dot(N, H));
				// Wide, dim highlight - inverse of roughness controls width
				float specWidth = lerp(8.0, 2.0, _SurfaceRoughness);
				float spec = pow(NdotH, specWidth) * (1.0 - _SurfaceRoughness) * 0.15;

				// ── Ambient / fill light ──
				float ambient = _AmbientBoost + 0.12;
				// Slight sky bounce on top-facing surfaces
				float skyBounce = saturate(N.y * 0.5 + 0.5) * 0.08;

				// ── Combine ──
				float3 finalCol = baseCol * (diffuse * edgeDark + ambient + skyBounce) + spec;

				UNITY_APPLY_FOG(i.fogCoord, finalCol);
				return fixed4(saturate(finalCol), 1.0);
			}
			ENDCG
		}
	}
}
