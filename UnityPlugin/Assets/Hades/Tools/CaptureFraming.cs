// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using UnityEngine;

namespace Hades.Tools
{
    /// <summary>
    /// DESIGN-02 (docs/design/DESIGN-02-visual-feedback.md) §3.5's deterministic framing, isolated
    /// as a pure static function with no Unity object graph at all - no Camera, no GameObject, no
    /// scene, not even a Transform. This is the design's own "single best testability decision"
    /// (§3.5, §11.1): every assertion CaptureFramingTests.cs makes runs under this repo's own
    /// '-nographics' EditMode runner, because nothing in this file ever touches a graphics device.
    ///
    /// Places a camera along the fixed direction <see cref="Direction"/> from <c>bounds</c>'s
    /// centre, backed off far enough that the whole bounding sphere sits inside the frustum with
    /// <c>margin</c> to spare, then looks back at the centre. Same bounds, same aspect/fov/margin
    /// -&gt; same pose, exactly - the ONLY source of DESIGN-02 §8.1's "same scene, same call -&gt;
    /// same framing" determinism contract; nothing downstream of this call perturbs it.
    /// </summary>
    public static class CaptureFraming
    {
        /// <summary>DESIGN-02 §3.5's fixed direction, normalized once. The camera sits at
        /// <c>center + Direction * distance</c> and looks back toward <c>center</c>.</summary>
        public static readonly Vector3 Direction = new Vector3(-1f, -0.7f, -1f).normalized;

        public const float DefaultFieldOfView = 60f;
        public const float DefaultMargin = 1.4f;

        /// <summary>A <c>bounds</c> with zero (or near-zero) extents - a bare Transform, per
        /// DESIGN-02 §3.5 step 2's own fallback chain - is floored to this effective bounding-sphere
        /// radius rather than collapsing the camera onto the target (distance 0, near == far) or
        /// dividing by zero. Not part of DESIGN-02's literal formula (which assumes a real bounds),
        /// but required for <see cref="Compute"/> to stay total over every input §11.1 lists,
        /// including "a degenerate zero-extent bounds".</summary>
        public const float MinEffectiveRadius = 0.5f;

        /// <summary>
        /// DESIGN-02 §11.1's signature: <c>CaptureFraming.Compute(Bounds bounds, float aspect, float
        /// fov, float margin) -&gt; (Vector3 position, Quaternion rotation, float near, float far)</c>.
        /// <paramref name="aspect"/> is width/height.
        ///
        /// The distance formula is exactly DESIGN-02 §3.5's <c>extents.magnitude / tan(fov/2) *
        /// margin</c> whenever <paramref name="aspect"/> &gt;= 1 - true for every real caller
        /// (16:9 for 'scene'/'prefab') - because the vertical half-angle is then the tighter
        /// constraint. For <paramref name="aspect"/> &lt; 1 the tighter of the vertical/horizontal
        /// half-angles is used instead, so the bounding sphere stays fully contained in every case
        /// §11.1 asks for ("the target is inside the frustum, asserted geometrically"); this
        /// generalisation is inert for every current caller and only changes behaviour for an
        /// aspect the shipped tool never actually requests.
        /// </summary>
        public static (Vector3 position, Quaternion rotation, float near, float far) Compute(
            Bounds bounds, float aspect, float fieldOfView = DefaultFieldOfView, float margin = DefaultMargin)
        {
            var center = bounds.center;
            var radius = Mathf.Max(bounds.extents.magnitude, MinEffectiveRadius);

            var verticalHalfAngle = fieldOfView * 0.5f * Mathf.Deg2Rad;
            var horizontalHalfAngle = Mathf.Atan(Mathf.Tan(verticalHalfAngle) * Mathf.Max(aspect, 0.0001f));
            var effectiveHalfAngle = Mathf.Min(verticalHalfAngle, horizontalHalfAngle);

            var distance = (radius / Mathf.Tan(effectiveHalfAngle)) * margin;

            var position = center + Direction * distance;
            var rotation = Quaternion.LookRotation((center - position).normalized, Vector3.up);

            var near = Mathf.Max(0.01f, distance - radius);
            var far = distance + radius;

            return (position, rotation, near, far);
        }
    }
}
