// Default shader: slow gradient waves. Replace with any Shadertoy-style mainImage().
void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord / iResolution.xy;
    float t = iTime * 0.15;
    vec3 a = vec3(0.05, 0.07, 0.14), b = vec3(0.48, 0.55, 1.0), c = vec3(1.0, 0.72, 0.42);
    float w = sin(uv.x * 3.0 + t * 2.0 + sin(uv.y * 4.0 - t)) * 0.5 + 0.5;
    vec3 col = mix(a, b, smoothstep(0.2, 0.9, uv.y + w * 0.25));
    col = mix(col, c, smoothstep(0.85, 1.0, w) * 0.25);
    fragColor = vec4(col, 1.0);
}
