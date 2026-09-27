//!PARAM rain_amount
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.5

//!PARAM rain_speed
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 4.0
1.0

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow rain

float h11(float p){ p = fract(p * 0.1031); p *= p + 33.33; p *= p + p; return fract(p); }
float h21(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
vec2 h22(vec2 p){ vec3 p3 = fract(vec3(p.xyx) * vec3(0.1031, 0.1030, 0.0973)); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.xx + p3.yz) * p3.zy); }
float wf_time(){ return float(frame) / 60.0; }
vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    float aspect = HOOKED_size.x / HOOKED_size.y;
    float t = wf_time() * rain_speed;
    float acc = 0.0;
    for (int l = 0; l < 3; l++) {
        float fl = float(l);
        vec2 uv = HOOKED_pos * vec2(aspect, 1.0);
        uv.x += uv.y * 0.12;                        // slant
        vec2 g = vec2(uv.x * (70.0 + fl * 40.0), uv.y * (3.0 + fl));
        float col = floor(g.x);
        float speed = 1.6 + h11(col + fl * 13.0) * 1.2;
        float y = fract(g.y + t * speed + h11(col * 7.1 + fl));
        float present = step(1.0 - rain_amount * 0.7, h11(col * 3.3 + fl * 5.0));
        float streak = smoothstep(0.0, 0.02, y) * smoothstep(0.18, 0.02, y);
        float thin = smoothstep(0.5, 0.0, abs(fract(g.x) - 0.5) * 2.0 - 0.6);
        acc += present * streak * thin * (0.5 - fl * 0.12);
    }
    vec3 c = src.rgb * (1.0 - 0.12 * rain_amount) + vec3(0.72, 0.78, 0.9) * clamp(acc, 0.0, 1.0);
    return vec4(c, src.a);
}
