using UnityEngine;

/// <summary>
/// Tracks whether the character is standing on anything, from real collision contacts.
///
/// WHY NOT A SPHERECAST
/// A cast has to know how far down the ground should be, which means knowing the shape. A
/// pancake sits closer to the ground than the character's own radius, so a cast starts
/// already overlapping - and SphereCast ignores anything it starts inside, so it reports
/// nothing. A noodle has the opposite problem: the ground is further away than the cast
/// reaches. Any single radius is wrong for some shape.
///
/// Contacts sidestep all of it. The solver has already worked out what is being touched and
/// which way each surface faces, so this costs nothing extra and is correct for any shape,
/// including composites that change on every morph.
///
/// Lives on the Rigidbody's GameObject, since that is where Unity delivers collision
/// messages - not on the child colliders.
/// </summary>
[DefaultExecutionOrder(-45)]
public class ClayGroundContact : MonoBehaviour
{
    [Tooltip("How flat a surface has to be to count as ground, in degrees from horizontal.")]
    [Range(0f, 80f)] public float maxSlope = 50f;

    [Tooltip("How long the character keeps counting as grounded after the last contact.\n\n" +
             "Contacts flicker as a lumpy assembly rolls, and without a little grace a jump " +
             "can be refused in the gap between one contact ending and the next beginning.")]
    public float graceTime = 0.12f;

    /// <summary>True while standing on something flat enough.</summary>
    public bool IsGrounded => Time.time - lastContactTime <= graceTime;

    /// <summary>Normal of the flattest surface being stood on. Up when there is none.</summary>
    public Vector3 GroundNormal { get; private set; } = Vector3.up;

    /// <summary>How fast the body was falling just before it landed, for impact reactions.</summary>
    public float LastImpactSpeed { get; private set; }

    Rigidbody body;
    float lastContactTime = -999f;
    float minNormalDot;
    bool wasGrounded;
    float previousVerticalSpeed;

    void Awake()
    {
        body = GetComponent<Rigidbody>();
        minNormalDot = Mathf.Cos(maxSlope * Mathf.Deg2Rad);
    }

    void OnValidate()
    {
        minNormalDot = Mathf.Cos(maxSlope * Mathf.Deg2Rad);
    }

    void FixedUpdate()
    {
        bool grounded = IsGrounded;

        // Sampled from the PREVIOUS step: by the time a contact is reported, the solver has
        // already cancelled the fall, so reading it now says every landing was gentle.
        if (grounded && !wasGrounded) LastImpactSpeed = Mathf.Max(-previousVerticalSpeed, 0f);

        wasGrounded = grounded;
        previousVerticalSpeed = body != null ? body.linearVelocity.y : 0f;
    }

    void OnCollisionEnter(Collision collision) => Inspect(collision);
    void OnCollisionStay(Collision collision) => Inspect(collision);

    void Inspect(Collision collision)
    {
        int count = collision.contactCount;
        float bestDot = minNormalDot;
        Vector3 best = Vector3.zero;

        for (int i = 0; i < count; i++)
        {
            // GetContact avoids the array allocation that collision.contacts performs.
            Vector3 normal = collision.GetContact(i).normal;

            float dot = Vector3.Dot(normal, Vector3.up);
            if (dot > bestDot) { bestDot = dot; best = normal; }
        }

        if (best == Vector3.zero) return;

        lastContactTime = Time.time;
        GroundNormal = best;
    }
}
