using System;
using System.Collections.Generic;
using Avalonia;

namespace maccy.Services;

public sealed class MouseShakeDetector
{
    private readonly List<(DateTimeOffset t, double x)> _samples = new();

    private DateTimeOffset _lastDirectionChangeAt = DateTimeOffset.MinValue;
    private int _directionChanges;
    private int _lastDir;

    public TimeSpan Window { get; set; } = TimeSpan.FromMilliseconds(750);

    public double MinDeltaX { get; set; } = 7;

    public int RequiredDirectionChanges { get; set; } = 2;

    public void Reset()
    {
        _samples.Clear();
        _directionChanges = 0;
        _lastDir = 0;
        _lastDirectionChangeAt = DateTimeOffset.MinValue;
    }

    public bool Update(Point localPoint)
    {
        var now = DateTimeOffset.UtcNow;
        _samples.Add((now, localPoint.X));

        while (_samples.Count > 0 && (now - _samples[0].t) > Window)
            _samples.RemoveAt(0);

        if (_samples.Count < 3)
            return false;

        var prev = _samples[^2];
        var cur = _samples[^1];
        var dx = cur.x - prev.x;
        if (Math.Abs(dx) < MinDeltaX)
            return false;

        var dir = dx > 0 ? 1 : -1;
        if (_lastDir == 0)
        {
            _lastDir = dir;
            return false;
        }

        if (dir != _lastDir)
        {
            if ((now - _lastDirectionChangeAt) < TimeSpan.FromMilliseconds(40))
            {
                _lastDir = dir;
                return false;
            }

            _directionChanges++;
            _lastDirectionChangeAt = now;
            _lastDir = dir;

            if (_directionChanges >= RequiredDirectionChanges)
                return true;
        }

        return false;
    }
}
