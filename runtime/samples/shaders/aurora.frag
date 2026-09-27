// Aurora — audio reactive through iChannel0 (row 0 = spectrum). Paste into Upload → Shader.
float hash(vec2 p) { return fract(sin(dot(p, vec2(41.3, 289.1))) * 43758.5453); }
float noise(vec2 p) { vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y); }
void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord / iResolution.xy;
    float bass = texture(iChannel0, vec2(0.05, 0.25)).r;
    vec3 col = mix(vec3(0.01, 0.02, 0.06), vec3(0.04, 0.05, 0.12), uv.y);
    for (int i = 0; i < 3; i++) {
        float fi = float(i);
        float y = 0.55 + 0.12 * fi + 0.08 * sin(uv.x * (3.0 + fi) + iTime * (0.15 + 0.05 * fi)) + 0.05 * noise(vec2(uv.x * 4.0, iTime * 0.1 + fi));
        float band = exp(-pow((uv.y - y) * (14.0 - fi * 3.0), 2.0)) * (0.6 + bass * 0.8);
        vec3 tint = mix(vec3(0.2, 1.0, 0.6), vec3(0.5, 0.45, 1.0), fi / 2.0);
        col += tint * band * (0.5 + 0.5 * noise(vec2(uv.x * 12.0 + iTime * 0.3, fi)));
    }
    float stars = step(0.997, hash(floor(fragCoord / 2.0))) * (0.5 + 0.5 * sin(iTime * 3.0 + hash(fragCoord) * 10.0));
    col += stars * (1.0 - uv.y * 0.4) * 0.8;
    fragColor = vec4(col, 1.0);
}
