Shader "Custom/GlitterGlassBead"
{
	Properties
	{
		_MainTex("Base Texture", 2D) = "white" {}
		_Tint("Glass Tint", Color) = (0.7, 0.9, 1.0, 1.0)
		_Alpha("Glass Alpha", Range(0,1)) = 0.45
		_RimColor("Rim Color", Color) = (0.8, 0.95, 1.0, 1.0)
		_RimPower("Rim Power", Range(0.5,8)) = 3
		_SpecColor("Specular Color", Color) = (1,1,1,1)
		_SpecPower("Specular Power", Range(8,128)) = 48
		_RefractionStrength("Refraction Strength", Range(0,0.2)) = 0.05
		_BeadRadius("Bead Radius (Object Space)", Range(0.1,2.0)) = 0.5
		_GlitterInsideMask("Glitter Inside Mask", Range(0,1)) = 1.0

		_GlitterScale("Glitter Density", Range(4,120)) = 40
		_GlitterSize("Glitter Size", Range(0.02,0.4)) = 0.12
		_GlitterDensity("Glitter Keep Ratio", Range(0,1)) = 0.6
		_GlitterIntensity("Glitter Intensity", Range(0,3)) = 1.4
		_GlitterSpeed("Glitter Speed", Range(0,10)) = 2.5
		_GlitterViewStrength("View Sparkle Strength", Range(0,3)) = 1.2
		_GlitterViewPower("View Sparkle Power", Range(1,8)) = 3.5

		_ColorA("Glitter Color A", Color) = (0.6, 1.0, 0.95, 1)
		_ColorB("Glitter Color B", Color) = (1.0, 0.6, 0.9, 1)
		_ColorC("Glitter Color C", Color) = (1.0, 0.95, 0.6, 1)
		_ColorBlend("Color Blend (0=A/B, 1=A/B/C)", Range(0,1)) = 1
	}

	SubShader
	{
		Tags { "Queue"="Transparent" "RenderType"="Transparent" }
		LOD 200

		Pass
		{
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
			float4 _Tint;
			float _Alpha;
			float4 _RimColor;
			float _RimPower;
			float4 _SpecColor;
			float _SpecPower;
			float _RefractionStrength;
			float _BeadRadius;
			float _GlitterInsideMask;

			float _GlitterScale;
			float _GlitterSize;
			float _GlitterDensity;
			float _GlitterIntensity;
			float _GlitterSpeed;
			float _GlitterViewStrength;
			float _GlitterViewPower;
			float4 _ColorA;
			float4 _ColorB;
			float4 _ColorC;
			float _ColorBlend;

			float hash11(float p)
			{
				p = frac(p * 0.1031);
				p *= p + 33.33;
				p *= p + p;
				return frac(p);
			}

			float hash31(float3 p)
			{
				p = frac(p * 0.1031);
				p += dot(p, p.yzx + 33.33);
				return frac((p.x + p.y) * p.z);
			}

			float3 hash33(float3 p)
			{
				p = frac(p * float3(0.1031, 0.1030, 0.0973));
				p += dot(p, p.yzx + 33.33);
				return frac((p.xxy + p.yzz) * p.zyx);
			}

			v2f vert(appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				o.objPos = v.vertex.xyz;
				UNITY_TRANSFER_FOG(o, o.vertex);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				float3 N = normalize(i.worldNormal);
				float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
				float viewDot = saturate(dot(N, V));
				float rim = pow(1.0 - viewDot, _RimPower);

				float2 refractUV = i.uv + N.xy * _RefractionStrength * (1.0 - viewDot);
				fixed4 baseCol = tex2D(_MainTex, refractUV) * _Tint;

				float3 H = normalize(V + float3(0.0, 0.0, 1.0));
				float spec = pow(saturate(dot(N, H)), _SpecPower);

				float radius = max(_BeadRadius, 0.001);
				float sphereMask = saturate(1.0 - (length(i.objPos) / radius));
				sphereMask = lerp(1.0, sphereMask, _GlitterInsideMask);

				float3 p = i.objPos * _GlitterScale;
				float3 cell = floor(p);
				float3 local = frac(p) - 0.5;
				float keep = step(hash31(cell), _GlitterDensity);
				float3 jitter = (hash33(cell) - 0.5) * 0.6;
				local -= jitter;

				float diamond = saturate((_GlitterSize - (abs(local.x) + abs(local.y) + abs(local.z))) / _GlitterSize);
				float phase = hash31(cell + 13.71);
				float sparkle = 0.5 + 0.5 * sin(_Time.y * _GlitterSpeed + phase * 6.2831853);
				float viewSparkle = 1.0 + _GlitterViewStrength * pow(1.0 - viewDot, _GlitterViewPower);

				float mixAB = frac(hash31(cell + 2.11) + 0.35 * sin(_Time.y * 0.7 + phase));
				float3 colAB = lerp(_ColorA.rgb, _ColorB.rgb, mixAB);
				float mixC = step(0.5, _ColorBlend) * hash31(cell + 7.31);
				float3 glitterColor = lerp(colAB, _ColorC.rgb, mixC);

				float glitterMask = keep * diamond * sparkle * viewSparkle * sphereMask;
				float3 glitter = glitterColor * glitterMask * _GlitterIntensity;

				float3 outCol = baseCol.rgb + glitter;
				outCol += _RimColor.rgb * rim;
				outCol += _SpecColor.rgb * spec * 0.5;

				return fixed4(saturate(outCol), _Alpha);
			}
			ENDCG
		}
	}
}
