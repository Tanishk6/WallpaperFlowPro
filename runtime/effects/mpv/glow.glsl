//!PARAM glow_strength
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 2.0
0.4

//!PARAM glow_threshold
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.45

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow soft glow

vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    vec2 px = HOOKED_pt * (HOOKED_size.y / 1080.0) * 5.0;
    vec3 g = vec3(0.0);
    for (int i = 0; i < 8; i++) {
        float a = float(i) * 0.785398;
        vec3 c = HOOKED_tex(HOOKED_pos + vec2(cos(a), sin(a)) * px).rgb;
        g += c * smoothstep(glow_threshold, 1.0, dot(c, vec3(0.333)));
    }
    g /= 8.0;
    return vec4(1.0 - (1.0 - src.rgb) * (1.0 - g * glow_strength), src.a); // screen blend
}
