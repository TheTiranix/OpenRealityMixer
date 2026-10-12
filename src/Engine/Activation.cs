using System.Numerics;
using OpenRealityMixer.Core;

namespace OpenRealityMixer.Engine;

/// <summary>Estado de fundido y activacion de una caja entre fotogramas.</summary>
public sealed class ActivationState
{
    public float Alpha;
    public float GazeTimer;
    public bool GazeActive;
    public bool ButtonOn;
    public bool PrevButtonDown;
    public bool Initialized;
}

/// <summary>Decide cuanto se ve una caja segun el modo de activacion. Logica pura, sin VR, facil de probar.</summary>
public static class Activation
{
    /// <summary>Fraccion extra de distancia sobre la que se funde en el modo proximidad.</summary>
    public const float ProximityBand = 0.25f;
    /// <summary>Margen para que la mirada no parpadee en el limite del angulo.</summary>
    public const float GazeHysteresis = 1.15f;

    /// <summary>Visibilidad objetivo (0..1) antes de aplicar el tiempo de fundido.</summary>
    public static float Target(CameraBox b, ActivationState s, Vector3 headPos, Vector3 headForward,
        Vector3 boxPos, bool buttonDown, float dt)
    {
        if (!s.Initialized)
        {
            s.Initialized = true;
            s.ButtonOn = b.ButtonStartsVisible;
            s.PrevButtonDown = buttonDown;
        }

        switch (b.Activation)
        {
            case ActivationMode.Always:
                return 1f;

            case ActivationMode.Proximity:
            {
                float d = Vector3.Distance(headPos, boxPos);
                float far = b.ProximityDistance * (1f + ProximityBand);
                return 1f - VrMath.SmoothStep(b.ProximityDistance, far, d);
            }

            case ActivationMode.Gaze:
            {
                var to = boxPos - headPos;
                float len = to.Length();
                float angle = len < 1e-4f ? 0f : MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(headForward), to / len), -1f, 1f)) * 180f / MathF.PI;
                float limit = s.GazeActive ? b.GazeAngle * GazeHysteresis : b.GazeAngle;
                if (angle <= limit)
                {
                    s.GazeTimer += dt;
                    if (s.GazeTimer >= b.GazeDelay) s.GazeActive = true;
                }
                else
                {
                    s.GazeTimer = 0;
                    s.GazeActive = false;
                }
                return s.GazeActive ? 1f : 0f;
            }

            case ActivationMode.Button:
            {
                if (b.ButtonHold)
                {
                    s.PrevButtonDown = buttonDown;
                    return buttonDown ? 1f : 0f;
                }
                if (buttonDown && !s.PrevButtonDown) s.ButtonOn = !s.ButtonOn;
                s.PrevButtonDown = buttonDown;
                return s.ButtonOn ? 1f : 0f;
            }
        }
        return 1f;
    }

    /// <summary>Acerca el alfa actual al objetivo con una rampa lineal que tarda <c>fadeSeconds</c> en completarse.</summary>
    public static float Step(float current, float target, float fadeSeconds, float dt)
    {
        if (fadeSeconds <= 0.001f) return target;
        float step = dt / fadeSeconds;
        if (current < target) return MathF.Min(target, current + step);
        return MathF.Max(target, current - step);
    }
}
