Shader "Custom/LenticularHologram"
{
	Properties
	{
		[Header(Lenticular Images)]
		_ImageA("Image A (Front)", 2D) = "white" {}
		_ImageB("Image B (Angled)", 2D) = "white" {}
		_Tint("Tint", Color) = (0.7, 0.9, 1.0, 1.0)
		_Alpha("Alpha", Range(0,1)) = 0.85

		[Header(Lenticular Swap)]
		_SwapAngle("Swap Center Angle", Range(-1,1)) = 0.0
		_SwapRange("Swap Transition Width", Range(0.01,1)) = 0.25
		_SwapAxis("Swap Axis (X=0 Y=1)", Range(0,1)) = 0.0
		_MicroLensScale("Micro Lens Scale", Range(0,500)) = 200
		_MicroLensBlend("Micro Lens Visibility", Range(0,0.3)) = 0.05

		[Header(Rainbow Prism)]
		_RainbowStrength("Rainbow Strength", Range(0,3)) = 1.2
		_RainbowScale("Rainbow Scale", Range(0.5,20)) = 6.0
		_RainbowSpeed("Rainbow Speed", Range(0,3)) = 0.4
		_PrismSpread("Prism Chromatic Spread", Range(0,0.05)) = 0.015
		_PrismIntensity("Prism Intensity", Range(0,2)) = 0.8
		_FresnelRainbow("Fresnel Rainbow Boost", Range(0,3)) = 1.5

		[Header(Hologram)]
		_HoloShift("Holo Color Shift", Range(0,2)) = 0.6
		_HoloSpeed("Holo Shift Speed", Range(0,3)) = 0.8

		[Header(Rim and Specular)]
		_RimColor("Rim Color", Color) = (0.6, 1.0, 1.0, 1.0)
		_RimPower("Rim Power", Range(0.5,8)) = 3.0
		_SpecColor2("Specular Color", Color) = (1,1,1,1)
		_SpecPower("Specular Power", Range(8,128)) = 64
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
				float4 tangent : TANGENT;
			};

			struct v2f
			{
				float4 vertex : SV_POSITION;
				float2 uv : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float3 worldNormal : TEXCOORD2;
				float3 worldTangent : TEXCOORD3;
				float3 worldBitangent : TEXCOORD4;
				UNITY_FOG_COORDS(5)
			};

			sampler2D _ImageA;
			float4 _ImageA_ST;
			sampler2D _ImageB;
			float4 _ImageB_ST;
			float4 _Tint;
			float _Alpha;

			float _SwapAngle;
			float _SwapRange;
			float _SwapAxis;
			float _MicroLensScale;
			float _MicroLensBlend;

			float _RainbowStrength;
			float _RainbowScale;
			float _RainbowSpeed;
			float _PrismSpread;
			float _PrismIntensity;
			float _FresnelRainbow;

			float _HoloShift;
			float _HoloSpeed;

			float4 _RimColor;
			float _RimPower;
			float4 _SpecColor2;
			float _SpecPower;

			// HSV to RGB conversion
			float3 hsv2rgb(float3 c)
			{
				float3 p = abs(frac(c.xxx + float3(1.0, 2.0/3.0, 1.0/3.0)) * 6.0 - 3.0);
				return c.z * lerp(float3(1,1,1), saturate(p - 1.0), c.y);
			}

			// Multi-layer rainbow spectrum from angle
			float3 prismRainbow(float angle, float fresnel)
			{
				// Primary rainbow band
				float hue1 = frac(angle * _RainbowScale + _Time.y * _RainbowSpeed);
				float3 rainbow1 = hsv2rgb(float3(hue1, 0.85, 1.0));

				// Secondary shifted band for depth
				float hue2 = frac(angle * _RainbowScale * 1.3 + _Time.y * _RainbowSpeed * 0.7 + 0.33);
				float3 rainbow2 = hsv2rgb(float3(hue2, 0.7, 0.9));

				// Combine bands with fresnel-weighted blending
				float3 rainbow = lerp(rainbow1, rainbow2, fresnel * 0.4);

				return rainbow;
			}

			v2f vert(appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _ImageA);
				o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				o.worldTangent = UnityObjectToWorldDir(v.tangent.xyz);
				o.worldBitangent = cross(o.worldNormal, o.worldTangent) * v.tangent.w;
				UNITY_TRANSFER_FOG(o, o.vertex);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				float3 N = normalize(i.worldNormal);
				float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
				float3 T = normalize(i.worldTangent);
				float3 B = normalize(i.worldBitangent);

				float NdotV = saturate(dot(N, V));
				float fresnel = 1.0 - NdotV;

				// === LENTICULAR IMAGE SWAP ===
				// Project view direction onto tangent plane
				// SwapAxis blends between horizontal (tangent) and vertical (bitangent) lenticular orientation
				float3 lentiDir = lerp(T, B, _SwapAxis);
				float viewAngle = dot(V, lentiDir);

				// Smooth global swap: view angle drives which image is visible
				// Like a real lenticular lens — the whole card transitions together
				float swapT = (viewAngle - _SwapAngle) / _SwapRange;
				float swapFactor = saturate(swapT * 0.5 + 0.5);
				// Smoothstep for natural easing at the transition edges
				swapFactor = smoothstep(0.0, 1.0, swapFactor);

				// Optional micro-lens surface detail (very subtle, NOT the old visible strips)
				// Simulates the tiny cylindrical lens surface texture
				float microLens = sin(i.uv.x * _MicroLensScale * 3.14159);
				microLens = microLens * microLens; // soften
				float microOffset = microLens * _MicroLensBlend * (1.0 - abs(swapFactor * 2.0 - 1.0));
				swapFactor = saturate(swapFactor + microOffset);

				// Sample both images
				fixed4 colA = tex2D(_ImageA, i.uv);
				fixed4 colB = tex2D(_ImageB, i.uv);

				// Chromatic aberration during transition zone for prism feel
				float transitionZone = 1.0 - abs(swapFactor * 2.0 - 1.0); // peaks at 0.5
				transitionZone = pow(transitionZone, 0.8);
				float2 prismDir = lerp(float2(1,0), float2(0,1), _SwapAxis);
				float2 prismOffset = prismDir * _PrismSpread * transitionZone;

				fixed4 colA_r = tex2D(_ImageA, i.uv + prismOffset);
				fixed4 colA_b = tex2D(_ImageA, i.uv - prismOffset);
				fixed4 colB_r = tex2D(_ImageB, i.uv + prismOffset);
				fixed4 colB_b = tex2D(_ImageB, i.uv - prismOffset);

				float3 imgA = float3(colA_r.r, colA.g, colA_b.b);
				float3 imgB = float3(colB_r.r, colB.g, colB_b.b);

				float3 baseA = lerp(colA.rgb, imgA, _PrismIntensity * transitionZone);
				float3 baseB = lerp(colB.rgb, imgB, _PrismIntensity * transitionZone);

				// Final lenticular swap
				float3 lentiColor = lerp(baseA, baseB, swapFactor);

				// === RAINBOW PRISM OVERLAY ===
				float viewAngleNorm = viewAngle * 0.5 + 0.5; // 0~1
				float3 rainbow = prismRainbow(viewAngleNorm + i.uv.x * 0.3 + i.uv.y * 0.15, fresnel);

				// Fresnel-driven rainbow intensity (edges glow more)
				float rainbowMask = pow(fresnel, 1.2) * _FresnelRainbow + 0.15;
				rainbowMask = saturate(rainbowMask);

				// Apply rainbow as additive + multiplicative hybrid
				float3 color = lentiColor * _Tint.rgb;
				float3 rainbowContrib = rainbow * _RainbowStrength * rainbowMask;
				color = color * (1.0 + rainbowContrib * 0.5) + rainbowContrib * 0.3;

				// === HOLOGRAPHIC COLOR SHIFT ===
				float holoPhase = viewAngle * 3.0 + i.uv.y * 2.0 + _Time.y * _HoloSpeed;
				float3 holoShift = float3(
					sin(holoPhase) * 0.5 + 0.5,
					sin(holoPhase + 2.094) * 0.5 + 0.5,  // +120 degrees
					sin(holoPhase + 4.189) * 0.5 + 0.5   // +240 degrees
				);
				color = lerp(color, color * holoShift, _HoloShift * 0.6);

				// === SCAN LINE SHIMMER (hologram detail) ===
				float scanLine = sin(i.uv.y * 400.0 + _Time.y * 2.0) * 0.5 + 0.5;
				scanLine = pow(scanLine, 8.0) * 0.15 * fresnel;
				color += scanLine * rainbow;

				// === RIM + SPECULAR ===
				float rim = pow(fresnel, _RimPower);
				float3 H = normalize(V + float3(0.0, 0.0, 1.0));
				float spec = pow(saturate(dot(N, H)), _SpecPower);

				color += _RimColor.rgb * rim;
				color += _SpecColor2.rgb * spec * 0.5;

				// Subtle rainbow tint on rim
				color += rainbow * rim * 0.3;

				UNITY_APPLY_FOG(i.fogCoord, color);
				return fixed4(saturate(color), _Alpha);
			}
			ENDCG
		}
	}
}
