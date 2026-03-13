Shader "Custom/GlitterShader"
{
	Properties
	{
		_MainTex("Base Texture", 2D) = "white" {}
		_GlitterColor("Glitter Color (Fallback)", Color) = (1,1,1,1)
		_ColorA("Glitter Color A", Color) = (0.4,0.9,1,1)
		_ColorB("Glitter Color B", Color) = (1,0.5,0.9,1)
		_ColorC("Glitter Color C", Color) = (1,0.9,0.4,1)
		_ColorBlend("Color Blend (0=A/B, 1=A/B/C)", Range(0,1)) = 1
		_ColorShiftSpeed("Color Shift Speed", Range(0,5)) = 0.8
		_CellDensity("Cell Density (Higher => smaller)", Range(4,200)) = 60
		_DiamondSize("Diamond Size (0.05-1.2)", Range(0.05,1.2)) = 0.5
		_KeepRatio("Keep Ratio (Lower => fewer)", Range(0,1)) = 0.5
		_SparkleSpeed("Sparkle Speed", Range(0,10)) = 4
		_SparkleStrength("Sparkle Strength", Range(0,2)) = 1
		_RotationRandomness("Rotation Randomness", Range(0,1)) = 1
		_DriftSpeed("Drift Speed", Range(0,1)) = 0.15
		_DriftAmount("Drift Amount", Range(0,0.5)) = 0.15
		_LenticularStrength("Lenticular Strength", Range(0,2)) = 0.8
		_LenticularPower("Lenticular Power", Range(1,8)) = 3
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
			#pragma multi_compile_fog
			#include "UnityCG.cginc"

			struct appdata
			{
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
				float3 normal : NORMAL;
			};

			struct v2f
			{
				float2 uv : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float3 worldNormal : TEXCOORD2;
				UNITY_FOG_COORDS(1)
				float4 vertex : SV_POSITION;
			};

			sampler2D _MainTex;
			float4 _MainTex_ST;
			float4 _GlitterColor;
			float4 _ColorA;
			float4 _ColorB;
			float4 _ColorC;
			float _ColorBlend;
			float _ColorShiftSpeed;
			float _CellDensity;
			float _DiamondSize;
			float _KeepRatio;
			float _SparkleSpeed;
			float _SparkleStrength;
			float _RotationRandomness;
			float _DriftSpeed;
			float _DriftAmount;
			float _LenticularStrength;
			float _LenticularPower;

			float hash21(float2 p)
			{
				p = frac(p * float2(123.34, 456.21));
				p += dot(p, p + 45.32);
				return frac(p.x * p.y);
			}

			float2 rotate2D(float2 p, float a)
			{
				float s = sin(a);
				float c = cos(a);
				return float2(c * p.x - s * p.y, s * p.x + c * p.y);
			}

			v2f vert(appdata v)
			{
				v2f o;
				o.vertex = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
				o.worldNormal = UnityObjectToWorldNormal(v.normal);
				UNITY_TRANSFER_FOG(o, o.vertex);
				return o;
			}

			fixed4 frag(v2f i) : SV_Target
			{
				fixed4 col = tex2D(_MainTex, i.uv);

				float t = _Time.y;
				float2 baseGridUv = i.uv * _CellDensity;
				float2 baseCell = floor(baseGridUv);
				float2 baseLocal = frac(baseGridUv) - 0.5;
				float baseRand = hash21(baseCell);

				float2 flowDir = normalize(float2(hash21(baseCell + 2.17) - 0.5, hash21(baseCell + 6.41) - 0.5));
				float flowPhase = hash21(baseCell + 9.77) * 6.2831853;
				float2 flow = flowDir * (sin(t * _DriftSpeed + flowPhase) * _DriftAmount * _CellDensity);

				float2 gridUv = baseGridUv + flow;
				float2 cell = floor(gridUv);
				float2 local = frac(gridUv) - 0.5;
				float rand = hash21(cell);

				cell = floor(gridUv);
				local = frac(gridUv) - 0.5;
				rand = hash21(cell);
				float keep = step(rand, _KeepRatio);

				float angle = (rand - 0.5) * 6.2831853 * _RotationRandomness;
				float2 rlocal = rotate2D(local, angle);

				float diamond = saturate((_DiamondSize - (abs(rlocal.x) + abs(rlocal.y))) / _DiamondSize);

				float phase = hash21(cell + 17.31);
				float sparkle = 0.5 + 0.5 * sin(t * _SparkleSpeed + phase * 6.2831853);
				float sparkleGain = 1.0 + _SparkleStrength * sparkle;

				float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
				float3 N = normalize(i.worldNormal);
				float viewDot = saturate(dot(N, V));
				float lenticular = pow(viewDot, _LenticularPower);
				float mask = keep * diamond * sparkleGain * (1.0 + _LenticularStrength * lenticular);
				
				float colorT = _Time.y * _ColorShiftSpeed;
				float r2 = hash21(cell + 3.71);
				float mixAB = frac(rand + 0.35 * sin(colorT + r2 * 6.2831853));
				float3 colAB = lerp(_ColorA.rgb, _ColorB.rgb, mixAB);
				float mixC = step(0.5, _ColorBlend) * hash21(cell + 9.13);
				float3 glitterColor = lerp(colAB, _ColorC.rgb, mixC);
				float3 glitter = glitterColor * mask + _GlitterColor.rgb * mask * 0.0;

				float3 outCol = saturate(col.rgb + glitter);
				return fixed4(outCol, col.a);
			}
			ENDCG
		}
	}
}
