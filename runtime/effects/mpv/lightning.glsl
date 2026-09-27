//!PARAM lightning_amount
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.5

//!PARAM lightning_rate
//!TYPE DYNAMIC float
//!MINIMUM 0.1
//!MAXIMUM 4.0
1.0

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow lightning flashes

float h11(float p){ p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }
float h21(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 h22(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.xx + p3.yz) * p3.zy); }
float wf_time(){ return float(frame) / 60.0; }
vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    float t = wf_time() * lightning_rate;
    float bucket = floor(t * 0.5);
    float strike = step(0.82, h11(bucket));                 // roughly one storm burst every ~10 s at rate 1
    float local = fract(t * 0.5) * 2.0;
    float flicker = strike * (exp(-local * 18.0) + 0.6 * exp(-abs(local - 0.18) * 30.0));
    vec2 p = HOOKED_pos;
    float x0 = 0.2 + 0.6 * h11(bucket * 3.7);
    float jag = (h11(floor(p.y * 24.0) + bucket) - 0.5) * 0.05;
    float bolt = strike * smoothstep(0.004, 0.0, abs(p.x - x0 - jag - (p.y * 0.1))) * step(p.y, 0.3 + 0.5 * h11(bucket * 9.1)) * exp(-local * 8.0);
    vec3 c = src.rgb * (1.0 - 0.15 * lightning_amount) + vec3(0.85, 0.9, 1.0) * (flicker * 0.5 + bolt * 1.5) * lightning_amount;
    return vec4(c, src.a);
}
