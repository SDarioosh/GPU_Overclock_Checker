namespace GpuOcChecker.Core.Stress.OpenCl;

internal static class ClKernels
{
    public const string Source = """
inline uint mix32(uint x)
{
    x ^= x >> 16; x *= 0x7feb352dU;
    x ^= x >> 15; x *= 0x846ca68bU;
    x ^= x >> 16;
    return x;
}

/* ---------------------------------------------------------------------------------------------
   Core stability check.
   Chaotic logistic-map iterations in four independent float4 chains (keeps the FMA pipes full)
   mixed into an integer hash every outer iteration. Chaos amplifies any single-bit computation
   error, and the hash makes it permanent, so a wrong result is detectable by comparing the output
   against a reference run. Output for a given gid only depends on (gid, iters, seed), so a
   "light" dispatch (fewer work-items) must reproduce the prefix of a "heavy" dispatch.
   --------------------------------------------------------------------------------------------- */
__kernel void compute_check(__global uint* out, uint iters, uint seed)
{
    uint gid = get_global_id(0);
    uint s = mix32(gid * 0x9E3779B9U ^ seed);
    float4 x0 = 0.05f + 0.9f * (float4)((float)(s & 0xFF), (float)((s >> 8) & 0xFF),
                                        (float)((s >> 16) & 0xFF), (float)(s >> 24)) / 255.0f;
    float4 x1 = 0.97f - 0.9f * x0;
    float4 x2 = 0.5f * x0 + 0.21f;
    float4 x3 = 0.5f * x1 + 0.17f;
    const float4 r = (float4)(3.9901f, 3.9923f, 3.9947f, 3.9969f);
    uint acc = s;
    for (uint i = 0; i < iters; i++)
    {
        for (int j = 0; j < 8; j++)
        {
            x0 = r * x0 * (1.0f - x0);
            x1 = r * x1 * (1.0f - x1);
            x2 = r * x2 * (1.0f - x2);
            x3 = r * x3 * (1.0f - x3);
        }
        acc = mix32(acc ^ as_uint(x0.x) ^ rotate(as_uint(x1.y), 7U)
                        ^ rotate(as_uint(x2.z), 13U) ^ rotate(as_uint(x3.w), 19U)) + i;
        /* re-seed lanes that collapsed into a fixed point so every lane keeps working */
        x0 = select(x0, (float4)(0.3141f), isless(x0, (float4)(1e-6f)));
        x1 = select(x1, (float4)(0.2718f), isless(x1, (float4)(1e-6f)));
        x2 = select(x2, (float4)(0.1414f), isless(x2, (float4)(1e-6f)));
        x3 = select(x3, (float4)(0.1732f), isless(x3, (float4)(1e-6f)));
    }
    out[gid] = acc ^ as_uint(x0.y) ^ as_uint(x1.z) ^ as_uint(x2.w) ^ as_uint(x3.x);
}

/* ---------------------------------------------------------------------------------------------
   VRAM pattern test (moving inversions). Patterns depend on the absolute index so address/bank
   decoding errors are caught as well as bit errors.
   --------------------------------------------------------------------------------------------- */
inline uint4 vram_pattern(ulong idx, uint pass)
{
    uint lo = (uint)idx;
    uint hi = (uint)(idx >> 32);
    switch (pass & 3U)
    {
        case 0: {
            uint h = mix32(lo ^ mix32(hi + pass * 0x632BE5ABU));
            return (uint4)(h, mix32(h + 1U), mix32(h + 2U), mix32(h + 3U));
        }
        case 1:
            return (lo & 1U) ? (uint4)(0xAAAAAAAAU, 0x55555555U, 0xAAAAAAAAU, 0x55555555U)
                             : (uint4)(0x55555555U, 0xAAAAAAAAU, 0x55555555U, 0xAAAAAAAAU);
        case 2: {
            uint b = (lo + pass) & 31U;
            return (uint4)(1U << b, 1U << ((b + 8U) & 31U), 1U << ((b + 16U) & 31U), 1U << ((b + 24U) & 31U));
        }
        default: {
            uint h = mix32(lo * 0x85EBCA6BU + pass);
            return (uint4)(lo, ~lo, h, ~h);
        }
    }
}

__kernel void vram_fill(__global uint4* buf, uint pass, ulong base)
{
    size_t i = get_global_id(0);
    buf[i] = vram_pattern(base + i, pass);
}

__kernel void vram_invert(__global uint4* buf)
{
    size_t i = get_global_id(0);
    buf[i] = ~buf[i];
}

/* errs[0] = mismatching 16-byte words; then up to 16 (index, xor-of-bits) pairs */
__kernel void vram_check(__global const uint4* buf, uint pass, ulong base, uint inverted, __global uint* errs)
{
    size_t i = get_global_id(0);
    uint4 expect = vram_pattern(base + i, pass);
    if (inverted) expect = ~expect;
    uint4 d = buf[i] ^ expect;
    if ((d.x | d.y | d.z | d.w) != 0U)
    {
        uint n = atomic_inc(&errs[0]);
        if (n < 16U)
        {
            errs[1 + n * 2] = (uint)(base + i);
            errs[2 + n * 2] = d.x | d.y | d.z | d.w;
        }
    }
}

/* ---------------------------------------------------------------------------------------------
   Bandwidth: plain streaming copy over buffers far larger than Infinity Cache / L2.
   --------------------------------------------------------------------------------------------- */
__kernel void bw_copy(__global const uint4* src, __global uint4* dst)
{
    size_t i = get_global_id(0);
    dst[i] = src[i];
}
""";
}
