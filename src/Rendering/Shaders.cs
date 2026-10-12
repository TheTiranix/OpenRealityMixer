namespace OpenRealityMixer.Rendering;

/// <summary>HLSL embebido: un triangulo a pantalla completa y un pixel shader que compone la caja.</summary>
internal static class Shaders
{
    public const string Source = """
        cbuffer BoxCB : register(b0)
        {
            row_major float4x4 WorldToClip;  // mundo -> clip de la camara (modo Aligned)
            float4 BoxCenter;                // xyz: centro de la caja en el mundo
            float4 BoxRight;                 // xyz: eje derecho * ancho total
            float4 BoxUp;                    // xyz: eje arriba * alto total
            float4 CamRect;                  // u0, v0, du, dv: subregion de la imagen de la camara
            float4 KeyColor;                 // rgb: color a quitar, w: 1 = chroma activo
            float4 Params1;                  // x: similitud, y: suavidad, z: spill, w: suavidad de borde
            float4 Params2;                  // x: brillo, y: contraste, z: saturacion, w: forma (0 rect, 1 elipse)
            float4 Flags;                    // x: modo (0 aligned, 1 flat), y: flipU, z: flipV, w: swap R/B
            float4 Flags2;                   // x: espejo horizontal (modo flat), y: escala de FOV
            float4 PassColor;                // rgb: color solido del modo PassthroughKey
        };

        Texture2D<float4> CamTex : register(t0);
        Texture2D<float> ShapeTex : register(t1);   // mascara de la forma dibujada a mano (modo Params2.w = 2)
        SamplerState Samp : register(s0);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VS(uint id : SV_VertexID)
        {
            float2 p = float2((id << 1) & 2, id & 2);
            VSOut o;
            o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = p;               // (0,0) arriba-izquierda, v crece hacia abajo
            return o;
        }

        float2 ChromaYZ(float3 c)
        {
            return float2(dot(c, float3(-0.169, -0.331, 0.5)), dot(c, float3(0.5, -0.419, -0.081)));
        }

        float ShapeMask(float2 uv)
        {
            if (Params2.w > 1.5) return ShapeTex.SampleLevel(Samp, uv, 0);
            float2 q = (uv - 0.5) * 2;
            float edge = Params1.w;
            if (Params2.w < 0.5) return 1 - smoothstep(1 - edge, 1, max(abs(q.x), abs(q.y)));
            return 1 - smoothstep(1 - edge, 1, length(q));
        }

        float4 PS(VSOut i) : SV_Target
        {
            float2 uv = i.uv;
            float2 suv;
            float valid = 1;

            // Modo passthrough por color: la forma se rellena con el color clave y el visor lo convierte en ventana.
            if (Flags.x > 1.5)
                return float4(PassColor.rgb, ShapeMask(uv));

            if (Flags.x < 0.5)
            {
                // Modo ventana: cada pixel de la caja es un punto 3D; lo proyectamos en la camara.
                float2 l = float2(uv.x - 0.5, 0.5 - uv.y);
                if (Flags2.x > 0.5) l.x = -l.x;
                float3 P = BoxCenter.xyz + l.x * BoxRight.xyz + l.y * BoxUp.xyz;
                float4 c = mul(float4(P, 1), WorldToClip);
                if (c.w <= 0.0001) { valid = 0; c.w = 1; }
                float2 ndc = c.xy / c.w;
                ndc /= max(Flags2.y, 0.01);
                if (Flags.y > 0.5) ndc.x = -ndc.x;
                if (Flags.z > 0.5) ndc.y = -ndc.y;
                suv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
                if (any(suv < 0) || any(suv > 1)) valid = 0;
                suv = CamRect.xy + saturate(suv) * CamRect.zw;
            }
            else
            {
                float2 t = uv;
                if (Flags2.x > 0.5) t.x = 1 - t.x;
                suv = CamRect.xy + t * CamRect.zw;
            }

            float3 col = CamTex.SampleLevel(Samp, suv, 0).rgb;
            if (Flags.w > 0.5) col = col.bgr;

            // Mascara de forma con borde suave
            float a = ShapeMask(uv) * valid;

            // Chroma key sobre la crominancia (ignora la luminosidad)
            if (KeyColor.w > 0.5)
            {
                float d = distance(ChromaYZ(col), ChromaYZ(KeyColor.rgb));
                float k = smoothstep(Params1.x, Params1.x + max(Params1.y, 0.0001), d);
                float lum = dot(col, float3(0.299, 0.587, 0.114));
                // Spill: los pixeles del borde (cerca del color clave) pierden saturacion para quitar el reflejo verde.
                float spill = Params1.z * (1 - smoothstep(0, Params1.x + Params1.y + 0.15, d)) * k;
                col = lerp(col, lum.xxx, spill);
                a *= k;
            }

            // Ajustes de imagen
            col += Params2.x;
            col = (col - 0.5) * Params2.y + 0.5;
            float g = dot(col, float3(0.299, 0.587, 0.114));
            col = lerp(g.xxx, col, Params2.z);

            return float4(saturate(col), a);
        }
        """;
}
