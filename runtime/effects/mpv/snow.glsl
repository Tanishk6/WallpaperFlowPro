//!PARAM snow_amount
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.5

//!PARAM snow_speed
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 4.0
1.0

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow snow

float h11(float p){ p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }
float h21(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 h22(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.xx + p3.yz) * p3.zy); }
float wf_time(){ return float(frame) / 60.0; }
vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    float aspect = HOOKED_size.x / HOOKED_size.y;
    float t = wf_time() * snow_speed;
    float acc = 0.0;
    for (int l = 0; l < 4; l++) {
        float fl = float(l);
        float scale = 8.0 + fl * 6.0;
        vec2 uv = HOOKED_pos * vec2(aspect, 1.0) * scale;
        uv.y -= t * (0.6 + fl * 0.25);
        uv.x += sin(t * 0.7 + fl * 1.7 + uv.y * 0.3) * 0.4;
        vec2 cell = floor(uv), f = fract(uv);
        float present = step(1.0 - snow_amount * 0.9, h21(cell + fl * 17.0));
        vec2 c = h22(cell + fl * 31.0) * 0.8 + 0.1;
        float r = (0.04 + 0.05 * h21(cell * 1.3)) * (1.0 - fl * 0.15);
        acc += present * smoothstep(r, r * 0.3, length(f - c)) * (1.0 - fl * 0.18);
    }
    return vec4(mix(src.rgb, vec3(1.0), clamp(acc, 0.0, 1.0) * 0.9), src.a);
}
