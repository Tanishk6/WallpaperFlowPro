//!PARAM cg_amount
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
1.0

//!PARAM cg_brightness
//!TYPE DYNAMIC float
//!MINIMUM -0.5
//!MAXIMUM 0.5
0.0

//!PARAM cg_contrast
//!TYPE DYNAMIC float
//!MINIMUM 0.5
//!MAXIMUM 1.5
1.05

//!PARAM cg_saturation
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 2.0
1.15

//!PARAM cg_temperature
//!TYPE DYNAMIC float
//!MINIMUM -1.0
//!MAXIMUM 1.0
0.0

//!PARAM cg_vignette
//!TYPE DYNAMIC float
//!MINIMUM 0.0
//!MAXIMUM 1.0
0.25

//!HOOK OUTPUT
//!BIND HOOKED
//!DESC WallpaperFlow colour grade

vec4 hook() {
    vec4 src = HOOKED_texOff(0);
    vec3 c = src.rgb + cg_brightness;
    c = (c - 0.5) * cg_contrast + 0.5;
    float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
    c = mix(vec3(l), c, cg_saturation);
    c += vec3(0.08, 0.02, -0.08) * cg_temperature;
    vec2 d = HOOKED_pos - 0.5;
    c *= 1.0 - cg_vignette * smoothstep(0.35, 0.85, length(d * vec2(1.0, 0.8)) * 1.2);
    return vec4(mix(src.rgb, clamp(c, 0.0, 1.0), cg_amount), src.a);
}
