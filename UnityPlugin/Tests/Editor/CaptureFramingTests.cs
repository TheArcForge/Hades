// C# 9 only in this file - see the file banner in Contract/MiniJson.cs.
using Hades.Tools;
using NUnit.Framework;
using UnityEngine;

namespace Hades.Tests.Editor
{
    /// <summary>
    /// DESIGN-02 §11.1: <see cref="CaptureFraming.Compute"/> is a pure static function with no
    /// Unity object graph, so every case here runs under this repo's own '-nographics' EditMode
    /// runner with no camera, no RenderTexture, and no scene at all - the design's own "single best
    /// testability decision".
    /// </summary>
    [TestFixture]
    public sealed class CaptureFramingTests
    {
        const float Aspect16x9 = 16f / 9f;

        /// <summary>DESIGN-02 §11.1's own property: "the target is inside the frustum, asserted
        /// geometrically rather than by eyeballing numbers." Builds the exact view-projection matrix
        /// a real Camera configured with <paramref name="position"/>/<paramref name="rotation"/>/
        /// <paramref name="near"/>/<paramref name="far"/>/<paramref name="fov"/>/<paramref name="aspect"/>
        /// would render with, and uses Unity's own frustum-culling utility to test containment - no
        /// GPU, no Camera object, involved.</summary>
        static void AssertBoundsInsideFrustum(Bounds bounds, Vector3 position, Quaternion rotation, float near, float far, float fov, float aspect)
        {
            // Matrix4x4.TRS(position, rotation, Vector3.one).inverse alone is NOT the same matrix
            // Camera.worldToCameraMatrix would produce: Unity's world space is left-handed (camera
            // "forward" == +Z), but view space follows the OpenGL convention the projection matrix
            // expects, where the camera looks down -Z (right-handed). The standard fix - and the one
            // Camera.worldToCameraMatrix itself uses - is to negate the Z scale before inverting.
            var view = Matrix4x4.TRS(position, rotation, new Vector3(1f, 1f, -1f)).inverse;
            var proj = Matrix4x4.Perspective(fov, aspect, near, far);
            var viewProj = proj * view;
            var planes = GeometryUtility.CalculateFrustumPlanes(viewProj);

            Assert.IsTrue(GeometryUtility.TestPlanesAABB(planes, bounds),
                "bounds " + bounds + " is not fully inside the frustum computed from position=" + position
                + " rotation=" + rotation.eulerAngles + " near=" + near + " far=" + far);
        }

        [Test]
        public void Compute_UnitCubeAtOrigin_FramesItInsideTheFrustum()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            var (position, rotation, near, far) = CaptureFraming.Compute(bounds, Aspect16x9);

            AssertBoundsInsideFrustum(bounds, position, rotation, near, far, CaptureFraming.DefaultFieldOfView, Aspect16x9);
        }

        [Test]
        public void Compute_OffOriginBounds_FramesItInsideTheFrustum()
        {
            var bounds = new Bounds(new Vector3(37f, -12f, 5f), new Vector3(2f, 3f, 4f));
            var (position, rotation, near, far) = CaptureFraming.Compute(bounds, Aspect16x9);

            AssertBoundsInsideFrustum(bounds, position, rotation, near, far, CaptureFraming.DefaultFieldOfView, Aspect16x9);
        }

        [Test]
        public void Compute_DegenerateZeroExtentBounds_ABareTransform_StaysFiniteAndFramesIt()
        {
            var bounds = new Bounds(new Vector3(1f, 2f, 3f), Vector3.zero);
            var (position, rotation, near, far) = CaptureFraming.Compute(bounds, Aspect16x9);

            Assert.IsFalse(float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z), "position must be finite");
            Assert.Greater(far, near, "far must be strictly greater than near for a degenerate bounds");
            Assert.Greater(Vector3.Distance(position, bounds.center), 0f, "camera must not collapse onto the target");

            AssertBoundsInsideFrustum(bounds, position, rotation, near, far, CaptureFraming.DefaultFieldOfView, Aspect16x9);
        }

        [Test]
        public void Compute_VeryWideFlatBounds_AGroundPlane_Against16x9_FramesItInsideTheFrustum()
        {
            var bounds = new Bounds(Vector3.zero, new Vector3(500f, 0.01f, 500f));
            var (position, rotation, near, far) = CaptureFraming.Compute(bounds, Aspect16x9);

            AssertBoundsInsideFrustum(bounds, position, rotation, near, far, CaptureFraming.DefaultFieldOfView, Aspect16x9);
        }

        [Test]
        public void Compute_NearIsLessThanFar_AndBothArePositive_ForEveryCaseAbove()
        {
            var cases = new[]
            {
                new Bounds(Vector3.zero, Vector3.one),
                new Bounds(new Vector3(37f, -12f, 5f), new Vector3(2f, 3f, 4f)),
                new Bounds(new Vector3(1f, 2f, 3f), Vector3.zero),
                new Bounds(Vector3.zero, new Vector3(500f, 0.01f, 500f)),
            };

            foreach (var bounds in cases)
            {
                var (_, _, near, far) = CaptureFraming.Compute(bounds, Aspect16x9);
                Assert.Greater(near, 0f, "near must be positive for bounds " + bounds);
                Assert.Greater(far, near, "far must exceed near for bounds " + bounds);
            }
        }

        [Test]
        public void Compute_SameBoundsAndAspect_AlwaysReturnsTheSamePose()
        {
            var bounds = new Bounds(new Vector3(4f, 5f, 6f), new Vector3(1f, 2f, 3f));

            var first = CaptureFraming.Compute(bounds, Aspect16x9);
            var second = CaptureFraming.Compute(bounds, Aspect16x9);

            Assert.AreEqual(first.position, second.position);
            Assert.AreEqual(first.rotation, second.rotation);
            Assert.AreEqual(first.near, second.near);
            Assert.AreEqual(first.far, second.far);
        }

        [Test]
        public void Compute_LooksBackTowardTheBoundsCentre()
        {
            var bounds = new Bounds(new Vector3(10f, 0f, 0f), Vector3.one);
            var (position, rotation, _, _) = CaptureFraming.Compute(bounds, Aspect16x9);

            var expectedForward = (bounds.center - position).normalized;
            var actualForward = rotation * Vector3.forward;

            Assert.Less(Vector3.Angle(expectedForward, actualForward), 0.01f, "camera must look back at the bounds centre");
        }
    }
}
