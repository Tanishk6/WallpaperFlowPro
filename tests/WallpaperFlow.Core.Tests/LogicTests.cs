using System.Text;
using WallpaperFlow.Core.AI;
using WallpaperFlow.Core.Audio;
using WallpaperFlow.Core.Data;
using WallpaperFlow.Core.Detection;
using WallpaperFlow.Core.Input;
using WallpaperFlow.Core.Media;
using WallpaperFlow.Core.Models;
using WallpaperFlow.Core.Rendering;
using WallpaperFlow.Core.Scheduling;
using Xunit;

namespace WallpaperFlow.Core.Tests;

public class SchedulerTests
{
    private static List<WallpaperItem> Items(params string[] titles) => titles.Select(t => new WallpaperItem { Id = t.ToLowerInvariant(), Title = t }).ToList();

    [Fact]
    public void Hourly_rotation_is_due_after_an_hour()
    {
        var s = new SchedulerSettings { Enabled = true, Interval = ScheduleInterval.Hourly, LastRotation = new DateTime(2026, 1, 1, 10, 0, 0) };
        Assert.False(Scheduler.IsDue(s, new DateTime(2026, 1, 1, 10, 59, 0)));
        Assert.True(Scheduler.IsDue(s, new DateTime(2026, 1, 1, 11, 0, 0)));
    }

    [Fact]
    public void Daily_rotation_happens_once_after_the_set_time()
    {
        var s = new SchedulerSettings { Enabled = true, Interval = ScheduleInterval.Daily, DailyAt = new TimeSpan(9, 0, 0), LastRotation = new DateTime(2026, 1, 1, 9, 1, 0) };
        Assert.False(Scheduler.IsDue(s, new DateTime(2026, 1, 2, 8, 59, 0)));
        Assert.True(Scheduler.IsDue(s, new DateTime(2026, 1, 2, 9, 0, 0)));
    }

    [Fact]
    public void Weekly_rotation_targets_the_chosen_day()
    {
        // 2026-01-05 is a Monday.
        var s = new SchedulerSettings { Enabled = true, Interval = ScheduleInterval.Weekly, WeeklyOn = DayOfWeek.Monday, DailyAt = new TimeSpan(8, 0, 0), LastRotation = new DateTime(2026, 1, 5, 8, 0, 1) };
        Assert.False(Scheduler.IsDue(s, new DateTime(2026, 1, 11, 23, 0, 0)));
        Assert.True(Scheduler.IsDue(s, new DateTime(2026, 1, 12, 8, 0, 0)));
    }

    [Fact]
    public void Disabled_scheduler_is_never_due()
    {
        Assert.False(Scheduler.IsDue(new SchedulerSettings { Enabled = false, Interval = ScheduleInterval.Hourly }, DateTime.Now));
    }

    [Fact]
    public void Sequential_order_wraps_around()
    {
        var items = Items("Alpha", "Bravo", "Charlie");
        Assert.Equal("bravo", Scheduler.PickNext(items, ScheduleOrder.Sequential, "alpha", new Random(1))!.Id);
        Assert.Equal("alpha", Scheduler.PickNext(items, ScheduleOrder.Sequential, "charlie", new Random(1))!.Id);
    }

    [Fact]
    public void Random_order_never_repeats_the_current_wallpaper()
    {
        var items = Items("A", "B", "C", "D");
        var rng = new Random(42);
        for (int i = 0; i < 200; i++) Assert.NotEqual("b", Scheduler.PickNext(items, ScheduleOrder.Random, "b", rng)!.Id);
    }
}

public class ImageTests
{
    private static byte[] Gradient(int w, int h, bool inverted = false)
    {
        var b = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var v = (byte)(x * 255 / (w - 1));
                if (inverted) v = (byte)(255 - v);
                var i = (y * w + x) * 3;
                b[i] = b[i + 1] = b[i + 2] = v;
            }
        return b;
    }

    [Fact]
    public void DHash_is_stable_and_discriminates()
    {
        var a = ImageAnalysis.DHash(ImageAnalysis.Gray(Gradient(96, 54), 96, 54), 96, 54);
        var b = ImageAnalysis.DHash(ImageAnalysis.Gray(Gradient(96, 54), 96, 54), 96, 54);
        var c = ImageAnalysis.DHash(ImageAnalysis.Gray(Gradient(96, 54, inverted: true), 96, 54), 96, 54);
        Assert.Equal(a, b);
        Assert.True(ImageAnalysis.Hamming(a, c) > 32);
    }

    [Fact]
    public void Sharpness_detects_detail()
    {
        int w = 32, h = 32;
        var flat = new double[w * h];
        var checker = new double[w * h];
        for (int i = 0; i < checker.Length; i++) checker[i] = ((i % w) + (i / w)) % 2 == 0 ? 255 : 0;
        Assert.Equal(0, ImageAnalysis.Sharpness(flat, w, h), 3);
        Assert.True(ImageAnalysis.Sharpness(checker, w, h) > 1000);
    }

    [Fact]
    public void Colours_name_the_dominant_hue()
    {
        var blue = Enumerable.Range(0, 100).SelectMany(_ => new byte[] { 20, 60, 230 }).ToArray();
        var stats = ImageAnalysis.Colors(blue);
        Assert.Equal("blue", stats.Hues.OrderByDescending(kv => kv.Value).First().Key);
        Assert.Equal("red", ImageAnalysis.HueName(2));
        Assert.Equal("pink", ImageAnalysis.HueName(320));
    }
}

public class ValidationTests
{
    private static byte[] Bytes(string ascii, int offset = 0)
    {
        var b = new byte[16];
        Encoding.ASCII.GetBytes(ascii).CopyTo(b, offset);
        return b;
    }

    [Fact]
    public void Sniff_recognises_containers_by_content()
    {
        Assert.Equal(WallpaperType.Video, FileValidator.Sniff(Bytes("ftypisom", 4)));
        Assert.Equal(WallpaperType.Video, FileValidator.Sniff(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 }));
        Assert.Equal(WallpaperType.Video, FileValidator.Sniff(Encoding.ASCII.GetBytes("RIFF\0\0\0\0AVI LIST")));
        Assert.Equal(WallpaperType.Gif, FileValidator.Sniff(Bytes("GIF89a")));
        Assert.Equal(WallpaperType.Slideshow, FileValidator.Sniff(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
    }

    [Fact]
    public void Sniff_rejects_executables_and_unknown_data()
    {
        Assert.Null(FileValidator.Sniff(Bytes("MZ\x90\0")));
        Assert.Null(FileValidator.Sniff(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    }

    [Fact]
    public void Blocked_extensions_cover_native_code()
    {
        foreach (var ext in new[] { ".exe", ".dll", ".bat", ".ps1", ".lnk", ".scr" }) Assert.Contains(ext, FileValidator.BlockedExtensions);
        Assert.DoesNotContain(".js", FileValidator.BlockedExtensions);
    }

    [Fact]
    public void Search_text_becomes_a_safe_fts_query()
    {
        Assert.Null(LibraryRepository.BuildFtsQuery("   "));
        Assert.Equal("\"neon\"* AND \"city\"*", LibraryRepository.BuildFtsQuery("neon city"));
        // Operators and quotes are neutralised: every token is quoted, so "OR" is just a word.
        Assert.Equal("\"drop\"* AND \"OR\"* AND \"table\"*", LibraryRepository.BuildFtsQuery("\"drop\" OR* (table)-"));
    }

    [Fact]
    public void Tags_are_normalised()
    {
        Assert.Equal("lo-fi beats", LibraryRepository.NormalizeTag("  Lo-Fi Beats!! "));
    }
}

public class ParserTests
{
    [Fact]
    public void Ffprobe_json_is_parsed()
    {
        const string json = """
        {"streams":[{"codec_type":"video","codec_name":"h264","width":3840,"height":2160,"avg_frame_rate":"60000/1001","duration":"12.5","bit_rate":"40000000"},
                    {"codec_type":"audio","codec_name":"aac"}],
         "format":{"format_name":"mov,mp4,m4a","duration":"12.52"}}
        """;
        var m = MediaProbe.Parse(json)!;
        Assert.Equal(3840, m.Width);
        Assert.Equal(2160, m.Height);
        Assert.InRange(m.Fps, 59.9, 60.0);
        Assert.Equal(12.5, m.Duration, 2);
        Assert.Equal("mov", m.Container);
        Assert.True(m.HasAudio);
    }

    [Fact]
    public void Rotated_phone_video_swaps_dimensions()
    {
        const string json = """{"streams":[{"codec_type":"video","width":1920,"height":1080,"avg_frame_rate":"30/1","side_data_list":[{"rotation":-90}]}],"format":{}}""";
        var m = MediaProbe.Parse(json)!;
        Assert.Equal(1080, m.Width);
        Assert.Equal(1920, m.Height);
    }

    [Fact]
    public void Cloud_tagger_reads_fenced_json()
    {
        const string resp = """{"content":[{"type":"text","text":"```json\n{\"tags\":[\"neon\",\"city\",\"rain\"],\"category\":\"Cyberpunk\",\"description\":\"A rainy street.\"}\n```"}]}""";
        var r = CloudVisionTagger.Parse(resp)!;
        Assert.Equal(new[] { "neon", "city", "rain" }, r.Tags);
        Assert.Equal("Cyberpunk", r.Category);
        Assert.Null(CloudVisionTagger.Parse("{\"content\":[]}"));
    }
}

public class AudioTests
{
    [Fact]
    public void Fft_peaks_at_the_input_frequency()
    {
        const int n = 1024, bin = 37;
        var fft = new Fft(n);
        var input = Enumerable.Range(0, n).Select(i => (float)Math.Sin(2 * Math.PI * bin * i / n)).ToArray();
        var mags = new float[n / 2];
        fft.Magnitudes(input, new float[n], new float[n], mags);
        Assert.Equal(bin, Array.IndexOf(mags, mags.Max()));
    }

    [Fact]
    public void Spectrum_stays_normalised()
    {
        var a = new SpectrumAnalyzer(48000, 64);
        var tone = Enumerable.Range(0, a.FftSize).Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / 48000.0)).ToArray();
        float level = 0;
        for (int i = 0; i < 10; i++) level = a.Process(tone, tone);
        var l = new float[64]; var r = new float[64];
        a.CopyTo(l, r);
        Assert.All(l, v => Assert.InRange(v, 0f, 1f));
        Assert.InRange(level, 0f, 1f);
        Assert.True(l.Max() > 0.5f);
    }

    [Fact]
    public void Fft_rejects_non_power_of_two() => Assert.Throws<ArgumentException>(() => new Fft(1000));
}

public class MiscTests
{
    [Fact]
    public void Duplicates_group_exact_and_near_matches()
    {
        var a = new WallpaperItem { Title = "a", ContentHash = "x" };
        var b = new WallpaperItem { Title = "b", ContentHash = "x" };
        var c = new WallpaperItem { Title = "c", ContentHash = "y", PerceptualHash = 0b1111_0000UL, Duration = 10 };
        var d = new WallpaperItem { Title = "d", ContentHash = "z", PerceptualHash = 0b1111_0011UL, Duration = 10.4 };
        var e = new WallpaperItem { Title = "e", ContentHash = "w", PerceptualHash = ~0UL, Duration = 10 };
        var groups = DuplicateDetector.Find(new[] { a, b, c, d, e });
        Assert.Equal(2, groups.Count);
        Assert.Contains(groups, g => g.Exact && g.Items.Count == 2);
        Assert.Contains(groups, g => !g.Exact && g.Items.Select(i => i.Title).OrderBy(t => t).SequenceEqual(new[] { "c", "d" }));
    }

    [Fact]
    public void Video_cache_only_transcodes_when_it_saves_work()
    {
        var uhd = new WallpaperItem { Type = WallpaperType.Video, Width = 3840, Height = 2160, Fps = 60 };
        var hd = new WallpaperItem { Type = WallpaperType.Video, Width = 1920, Height = 1080, Fps = 30 };
        Assert.True(VideoCacheService.Worthwhile(uhd, 1920, 1080, 0));
        Assert.False(VideoCacheService.Worthwhile(hd, 1920, 1080, 30));
        Assert.False(VideoCacheService.Worthwhile(hd, 1920, 1080, 0));
        Assert.Equal((1920, 1080), VideoCacheService.TargetSize(3840, 2160, 1920, 1080));
        Assert.Equal((2560, 1440), VideoCacheService.TargetSize(2560, 1440, 3840, 2160)); // never upscale
    }

    [Fact]
    public void Local_tagger_finds_technical_tags_and_category()
    {
        var w = new WallpaperItem { Title = "Neon city night drive", Type = WallpaperType.Video, Width = 3840, Height = 2160, Fps = 60, Duration = 12 };
        var r = LocalTagger.Tag(w, Array.Empty<ImageAnalysis.ColorStats>());
        Assert.Contains("4k", r.Tags);
        Assert.Contains("60fps", r.Tags);
        Assert.Contains("short loop", r.Tags);
        Assert.Contains(r.Category, new[] { "City", "Cyberpunk", "Cars" });
    }

    [Fact]
    public void Quality_flags_low_resolution_and_compression()
    {
        var w = new WallpaperItem { Type = WallpaperType.Video, Width = 640, Height = 360, Fps = 30, Bitrate = 200_000, Codec = "h264" };
        var q = QualityAnalyzer.Analyze(w, null);
        Assert.True(q.IsLow);
        Assert.Contains("very low resolution", q.Flags);
    }

    [Fact]
    public void Fullscreen_classification()
    {
        Assert.Equal(FullscreenKind.Game, DetectionService.Classify(3, "anything.exe", "", null));
        Assert.Equal(FullscreenKind.Game, DetectionService.Classify(5, "x.exe", "UnityWndClass", null));
        Assert.Equal(FullscreenKind.Game, DetectionService.Classify(5, "x.exe", "Foo", @"D:\SteamLibrary\steamapps\common\X\x.exe"));
        Assert.Equal(FullscreenKind.Video, DetectionService.Classify(5, "vlc.exe", "Qt5QWindowIcon", null));
        Assert.Equal(FullscreenKind.Presentation, DetectionService.Classify(5, "POWERPNT.EXE", "screenClass", null));
        Assert.Equal(FullscreenKind.Other, DetectionService.Classify(5, "notepad.exe", "Notepad", null));
    }

    [Fact]
    public void Process_rules_match_the_most_restrictive()
    {
        var rules = new[]
        {
            new ProcessRule { ProcessName = "obs64.exe", Action = PolicyAction.Pause, Enabled = true },
            new ProcessRule { ProcessName = "bdcam.exe", Action = PolicyAction.Stop, Enabled = true },
            new ProcessRule { ProcessName = "off.exe", Action = PolicyAction.Stop, Enabled = false },
        };
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OBS64.EXE", "bdcam.exe", "off.exe" };
        Assert.Equal("bdcam.exe", ProcessWatcher.Match(rules, running)!.ProcessName);
        Assert.Null(ProcessWatcher.Match(rules, new HashSet<string>()));
    }

    [Fact]
    public void Key_names_follow_the_dom()
    {
        Assert.Equal("a", InputRouter.KeyName(0x41));
        Assert.Equal("7", InputRouter.KeyName(0x37));
        Assert.Equal("ArrowLeft", InputRouter.KeyName(0x25));
    }
}

public class EffectsTests
{
    private static string RepoRoot()
    {
        if (Environment.GetEnvironmentVariable("WALLPAPERFLOW_ROOT") is { Length: > 0 } env) return env;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "WallpaperFlow.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static EffectsProfile All()
    {
        var p = new EffectsProfile();
        foreach (var k in Enum.GetValues<EffectKind>()) p.Items.Add(new EffectSetting { Kind = k, Enabled = true, Intensity = 0.5 });
        return p;
    }

    [Fact]
    public void Heavy_effects_are_dropped_when_not_allowed()
    {
        var dir = Path.Combine(RepoRoot(), "runtime", "effects", "mpv");
        var full = EffectsCompiler.ForMpv(All(), heavyAllowed: true, dir);
        var light = EffectsCompiler.ForMpv(All(), heavyAllowed: false, dir);
        Assert.Contains(full.Shaders, s => s.EndsWith("bloom.glsl"));
        Assert.DoesNotContain(light.Shaders, s => s.EndsWith("bloom.glsl") || s.EndsWith("blur.glsl"));
        Assert.NotNull(full.VideoFilter);
        Assert.Null(light.VideoFilter);
    }

    [Fact]
    public void Every_shader_parameter_exists_in_its_glsl_file()
    {
        var dir = Path.Combine(RepoRoot(), "runtime", "effects", "mpv");
        var plan = EffectsCompiler.ForMpv(All(), heavyAllowed: true, dir);
        var sources = string.Join("\n", plan.Shaders.Select(File.ReadAllText));
        foreach (var name in plan.ShaderOpts.Keys) Assert.Contains("//!PARAM " + name, sources);
    }

    [Fact]
    public void Web_effects_use_camel_case_keys()
    {
        var map = EffectsCompiler.ForWeb(All(), heavyAllowed: true);
        Assert.Contains("colorGrade", map.Keys);
        Assert.Contains("motionBlur", map.Keys);
    }
}
