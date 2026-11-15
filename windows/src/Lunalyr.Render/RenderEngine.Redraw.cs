namespace Lunalyr.Render;

public sealed partial class RenderEngine
{
    /// <summary>
    /// Something a layer reads changed outside the scene (YouTube's player covered or uncovered beneath the wallpaper):
    /// every surface draws one more frame, even one frozen by "Pause wallpaper" or resting idle, so a see-through part is
    /// never left open over a player that stopped.
    /// </summary>
    public void RequestFrame() => InputChanged();
}
