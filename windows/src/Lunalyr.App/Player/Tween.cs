namespace Lunalyr.App.Player;

/// <summary>One animated number: eases out from wherever it is towards its target, so a reversal mid-way never jumps.</summary>
internal struct Tween(float value)
{
    private float _from = value, _to = value;
    private double _start, _seconds;

    public readonly float Target => _to;

    public readonly float Value(double now)
    {
        if (!(_seconds > 0) || now >= _start + _seconds) return _to;
        var t = (float)Math.Clamp((now - _start) / _seconds, 0, 1);
        var eased = 1 - (1 - t) * (1 - t) * (1 - t);
        return _from + (_to - _from) * eased;
    }

    public readonly bool Moving(double now) => _seconds > 0 && now < _start + _seconds && _from != _to;

    public void To(float target, double now, double seconds)
    {
        if (_to == target) return;
        _from = Value(now);
        _to = target;
        _start = now;
        _seconds = seconds;
    }

    public void Snap(float value)
    {
        _from = _to = value;
        _seconds = 0;
    }
}
