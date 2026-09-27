//!PARAM fog_amount
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.5

//!PARAM fog_speed
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 4.0
1.0

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow fog

float h11(float p){ p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }
float h21(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 h22(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.xx + p3.yz) * p3.zy); }
float wf_time(){ return float(frame) / 60.0; }
float vnoise(vec2 p) { vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y); }
float fbm(vec2 p) { float v = 0.0, a = 0.5; for (int i = 0; i < 4; i++) { v += a * vnoise(p); p *= 2.03; a *= 0.5; } return v; }
vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    vec2 uv = HOOKED_pos * vec2(HOOKED_size.x / HOOKED_size.y, 1.0) * 3.0;
    float t = wf_time() * 0.03 * fog_speed;
    float n = fbm(uv + vec2(t, t * 0.3)) * fbm(uv * 0.5 - vec2(t * 0.6, 0.0));
    float d = clamp(n * 1.6 * fog_amount * (0.4 + HOOKED_pos.y * 0.8), 0.0, 0.85);
    return vec4(mix(src.rgb, vec3(0.82, 0.84, 0.88), d), src.a);
}
