//!PARAM bloom_threshold
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.72

//!PARAM bloom_strength
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 2.0
0.6

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow bloom

vec3 bright(vec2 p) { vec3 c = HOOKED_tex(p).rgb; float l = max(c.r, max(c.g, c.b)); return c * smoothstep(bloom_threshold, 1.0, l); }
vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    vec2 px = HOOKED_pt * (HOOKED_size.y / 1080.0);
    vec3 b = vec3(0.0);
    float w = 0.0;
    for (int ring = 1; ring <= 3; ring++) {
        float rad = float(ring * ring) * 6.0;
        for (int i = 0; i < 8; i++) {
            float a = float(i) * 0.785398 + float(ring) * 0.39;
            float wt = 1.0 / float(ring);
            b += bright(HOOKED_pos + vec2(cos(a), sin(a)) * px * rad) * wt;
            w += wt;
        }
    }
    return vec4(src.rgb + b / w * bloom_strength, src.a);
}
