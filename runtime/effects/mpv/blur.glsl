//!PARAM blur_radius
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 16.0
4.0

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow blur (13-tap disc)

const vec2 K[12] = vec2[](vec2(-0.326,-0.406), vec2(-0.840,-0.074), vec2(-0.696,0.457), vec2(-0.203,0.621), vec2(0.962,-0.195), vec2(0.473,-0.480),
                          vec2(0.519,0.767), vec2(0.185,-0.893), vec2(0.507,0.064), vec2(0.896,0.412), vec2(-0.322,-0.933), vec2(-0.792,-0.598));
vec4 hook() {
    vec4 acc = HOOKED_texOff(0);
    vec2 r = HOOKED_pt * blur_radius * (HOOKED_size.y / 1080.0) * 2.0;
    for (int i = 0; i < 12; i++) acc += HOOKED_tex(HOOKED_pos + K[i] * r);
    return acc / 13.0;
}
