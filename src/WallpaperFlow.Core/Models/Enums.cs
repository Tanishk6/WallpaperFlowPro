namespace WallpaperFlow.Core.Models;

public enum WallpaperType { Video, Gif, Apng, Html, Url, Shader, Slideshow, Clock }

public enum LayoutMode { SameOnAll, PerMonitor, Span }

public enum ScalingMode { Fill, Fit, Stretch, Center }

/// <summary>Scaler/quality preset handed to the renderer. Auto lets the governor decide.</summary>
public enum QualityPreset { Auto, Performance, Balanced, Quality }

public enum PlaybackState { Idle, Loading, Playing, Paused, Stopped, Faulted }

/// <summary>What happens to a wallpaper when a rule fires (gaming, streaming, ...).</summary>
public enum PolicyAction { Continue, ReduceFps, Pause, Stop }

public enum ThemeMode { System, Dark, Light }

public enum BackdropKind { Mica, Acrylic, Solid }

/// <summary>The app window's own background (not the desktop wallpaper).</summary>
public enum BackgroundScene { Aurora, Neon, Sakura, Wallpaper, Glass }

public enum AudioSource { SystemAudio, Microphone }

public enum ScheduleInterval { Off, Hourly, Daily, Weekly, Custom }

public enum ScheduleOrder { Sequential, Random }

public enum ScheduleSource { Library, Favorites, Collection, Folder }

public enum SortField { DateAdded, Title, FileSize, Resolution, Usage, LastUsed, Duration }

public enum HardwareDecoding { Auto, D3D11, Nvdec, Off }

/// <summary>Kinds of process rules the ProcessWatcher evaluates.</summary>
public enum RuleKind { Streaming, Recording, Game, Custom }

public enum EffectKind { Blur, Bloom, MotionBlur, Snow, Rain, Fog, Fire, Particles, Lightning, Glow, ColorGrade }

/// <summary>Why the foreground app is covering a monitor.</summary>
public enum FullscreenKind { None, Game, Video, Presentation, Other }

public enum LogSeverity { Debug, Info, Warn, Error }
