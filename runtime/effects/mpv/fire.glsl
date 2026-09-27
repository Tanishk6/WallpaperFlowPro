//!PARAM fire_amount
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.5

//!PARAM fire_height
//!TYPE DYNAMIC float
//!MINIMUM 0.05
//!MAXIMUM 1.0
0.3

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow fire / embers

float h11(float p){ p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }
float h21(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 h22(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.xx + p3.yz) * p3.zy); }
float wf_time(){ return float(frame) / 60.0; }
float vnoise(vec2 p) { vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(h21(i), h21(i + vec2(1, 0)), f.x), mix(h21(i + vec2(0, 1)), h21(i + vec2(1, 1)), f.x), f.y); }
vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    float aspect = HOOKED_size.x / HOOKED_size.y;
    float t = wf_time();
    vec2 uv = HOOKED_pos * vec2(aspect, 1.0);
    float h = 1.0 - HOOKED_pos.y;                          // 0 at bottom
    float n = vnoise(vec2(uv.x * 6.0, h * 4.0 - t * 1.6)) * 0.6 + vnoise(vec2(uv.x * 13.0, h * 9.0 - t * 3.1)) * 0.4;
    float flame = smoothstep(fire_height, 0.0, h - n * fire_height * 0.6) * fire_amount;
    vec3 col = mix(vec3(0.9, 0.2, 0.02), vec3(1.0, 0.8, 0.3), flame * flame);
    // embers
    vec2 eg = vec2(uv.x * 30.0, h * 12.0 - t * 2.0);
    vec2 cell = floor(eg), f = fract(eg);
    float ember = step(0.93, h21(cell)) * smoothstep(0.12, 0.0, length(f - h22(cell))) * smoothstep(fire_height * 2.5, 0.0, h) * fire_amount;
    return vec4(src.rgb + col * flame * 0.9 + vec3(1.0, 0.55, 0.2) * ember, src.a);
}
