namespace Lunalyr.Render.Scene;

/// <summary>
/// A background that leaves what is beneath the wallpaper visible ("Your wallpaper"): the engine gives its surfaces a
/// see-through swap chain while one is on screen, and a crossfade into it fades out what it replaces instead of leaving
/// it drawn underneath (the Mac's <c>BackgroundCrossfade</c> with <c>isOpaque</c> false). Every other layer is opaque.
/// </summary>
public interface ISeeThroughLayer;

/// <summary>
/// A background with nothing to draw until something has loaded (a picture decoding, a video's first frame): the
/// crossfade into it holds at its start until <see cref="HasFirstFrame"/> turns true, or a few seconds pass, so it
/// never fades in from black (the Mac's <c>onFirstFrame</c>).
/// </summary>
public interface IFirstFrameLayer
{
    /// <summary>Whether the layer has drawn, or can draw, its first frame. Read on the render thread.</summary>
    bool HasFirstFrame { get; }
}
