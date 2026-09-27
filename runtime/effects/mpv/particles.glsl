//!PARAM particles_amount
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.5

//!PARAM particles_speed
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 4.0
1.0

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow floating particles

float h11(float p){ p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }
float h21(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 h22(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.xx + p3.yz) * p3.zy); }
float wf_time(){ return float(frame) / 60.0; }
vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    float aspect = HOOKED_size.x / HOOKED_size.y;
    float t = wf_time() * 0.05 * particles_speed;
    vec3 acc = vec3(0.0);
    for (int l = 0; l < 3; l++) {
        float fl = float(l);
        vec2 uv = HOOKED_pos * vec2(aspect, 1.0) * (5.0 + fl * 4.0) + vec2(t * (1.0 + fl), -t * (0.6 + fl * 0.4));
        vec2 cell = floor(uv), f = fract(uv);
        float present = step(1.0 - particles_amount * 0.6, h21(cell + fl * 11.0));
        vec2 c = 0.5 + 0.35 * sin(h22(cell) * 6.2831 + t * 6.0);
        float tw = 0.6 + 0.4 * sin(t * 40.0 + h21(cell) * 20.0);
        float d = length(f - c);
        acc += present * tw * (smoothstep(0.08, 0.0, d) + 0.3 * smoothstep(0.25, 0.0, d)) * mix(vec3(1.0, 0.85, 0.6), vec3(0.6, 0.75, 1.0), h21(cell * 2.0));
    }
    return vec4(src.rgb + acc * 0.6, src.a);
}
