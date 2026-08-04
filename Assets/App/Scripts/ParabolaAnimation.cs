using UnityEngine;

/// <summary>
/// Moves the GameObject this component is attached to along a parabolic arc,
/// looping continuously on Update. The object rises to a configurable peak
/// height and returns over the arc duration, then pauses before the next arc.
///
/// This component only ever adds a positional offset along the chosen axis, and
/// it does so as a per-frame delta. It never writes an absolute position,
/// rotation, or scale, so it composes with other scripts that animate the
/// transform (including ones driving the same or other position axes).
/// </summary>
public class ParabolaAnimation : MonoBehaviour
{
    public enum Axis { X, Y, Z }

    [Tooltip("Which world axis the parabola travels along.")]
    public Axis axis = Axis.Y;

    [Tooltip("Peak height of the parabola, in units.")]
    public float peakHeight = 2f;

    [Tooltip("Time to complete each arc, in seconds.")]
    public float arcDuration = 0.5f;

    [Tooltip("Time to wait between each parabola, in seconds.")]
    public float delayBetweenArcs = 1.5f;

    // The offset we contributed last frame, so we can back it out and apply the
    // new one as a delta rather than stomping the whole position vector.
    private float appliedOffset;
    private float timer;

    private void OnEnable()
    {
        // Restart the arc cleanly without touching the transform.
        timer = 0f;
        appliedOffset = 0f;
    }

    private void OnDisable()
    {
        // Remove our contribution so we leave the transform as we found it.
        if (appliedOffset != 0f)
        {
            transform.position -= AxisVector() * appliedOffset;
            appliedOffset = 0f;
        }
    }

    private void Update()
    {
        timer += Time.deltaTime;

        float cycleLength = arcDuration + delayBetweenArcs;

        // Wrap the timer so the animation loops forever.
        if (cycleLength > 0f && timer >= cycleLength)
        {
            timer -= cycleLength;
        }

        float offset = 0f;

        // Only produce movement during the arc portion of the cycle.
        if (timer < arcDuration && arcDuration > 0f)
        {
            // Normalised progress through the arc (0 -> 1).
            float t = timer / arcDuration;

            // Parabola: 0 at t=0, peakHeight at t=0.5, back to 0 at t=1.
            offset = peakHeight * 4f * t * (1f - t);
        }

        // Apply only the change since last frame, so other scripts driving the
        // transform's position are preserved.
        float delta = offset - appliedOffset;
        if (delta != 0f)
        {
            transform.position += AxisVector() * delta;
        }

        appliedOffset = offset;
    }

    private Vector3 AxisVector()
    {
        switch (axis)
        {
            case Axis.X: return Vector3.right;
            case Axis.Z: return Vector3.forward;
            case Axis.Y:
            default: return Vector3.up;
        }
    }
}