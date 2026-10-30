using UnityEngine;

/// <summary>
/// Gently pulses a SpriteRenderer's scale and alpha forever — a "beacon" aura that draws
/// the eye. Used behind the player (to keep attention on it) and around the exit.
/// Runs on unscaled time so it keeps breathing during hitstop/rewind freezes.
/// </summary>
[RequireComponent(typeof(SpriteRenderer))]
public class PulseGlow : MonoBehaviour
{
    private SpriteRenderer _sr;
    private Color _rgb;
    private float _baseScale, _scaleAmp, _baseAlpha, _alphaAmp, _speed, _phase;

    public void Configure(Color color, float baseScale, float scaleAmp, float baseAlpha, float alphaAmp, float speed)
    {
        _sr = GetComponent<SpriteRenderer>();
        _rgb = color;
        _baseScale = baseScale; _scaleAmp = scaleAmp;
        _baseAlpha = baseAlpha; _alphaAmp = alphaAmp;
        _speed = speed;
        _phase = Random.Range(0f, Mathf.PI * 2f);
    }

    /// <summary>Re-tint without restarting the pulse (the earned sonar colours).</summary>
    public void SetColor(Color color) => _rgb = color;

    private void Update()
    {
        if (_sr == null) return;
        float s = Mathf.Sin(Time.unscaledTime * _speed + _phase);
        transform.localScale = Vector3.one * (_baseScale + _scaleAmp * s);
        var c = _rgb;
        c.a = Mathf.Clamp01(_baseAlpha + _alphaAmp * (0.5f + 0.5f * s));
        _sr.color = c;
    }
}
