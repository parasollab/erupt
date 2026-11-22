using System;
using UnityEngine;

namespace Erupt.Plugins.Ar
{
    /// <summary>
    /// The arithmetic of marker tracking and marker placement, free of the camera and of the
    /// detector so it can be tested without a headset.
    /// </summary>
    /// <remarks>
    /// Frames. The detector reports a tag in the <b>tag frame</b>: x to the tag's right, y up
    /// in the tag's plane (towards the top edge as printed), z into the tag. Offsets are
    /// easier to measure in the <b>marker frame</b>, which lies on the same spot turned so
    /// that it reads like any Unity object lying on a table: x to the tag's right, y out of
    /// the tag (up, for a tag on a table), z towards the tag's top edge.
    /// </remarks>
    public static class MarkerMath
    {
        /// <summary>The rotation from the tag frame to the marker frame.</summary>
        public static readonly Quaternion TagToMarker = Quaternion.Euler(-90f, 0f, 0f);

        /// <summary>The marker frame of a tag whose pose (tag frame) is <paramref name="tag"/>.</summary>
        public static Pose MarkerFrame(Pose tag) => new Pose(tag.position, tag.rotation * TagToMarker);

        /// <summary>
        /// A transform laid on a tag: at the tag's centre, turned from the tag frame by
        /// <paramref name="tagToObjectEuler"/> (<c>(-90, 0, 0)</c> gives the marker frame).
        /// </summary>
        public static Pose ObjectOnTag(Pose tag, Vector3 tagToObjectEuler) =>
            new Pose(tag.position, tag.rotation * Quaternion.Euler(tagToObjectEuler));

        /// <summary>
        /// One step of the low-pass filter on a tag pose: <paramref name="coefficient"/> 0 takes
        /// the new observation as it is, 1 never moves, values between blend (position
        /// linearly, rotation along the shortest arc).
        /// </summary>
        public static Pose Filter(Pose previous, Pose observed, float coefficient)
        {
            float t = Mathf.Clamp01(coefficient);
            return new Pose(
                Vector3.Lerp(observed.position, previous.position, t),
                Quaternion.Slerp(observed.rotation, previous.rotation, t));
        }

        /// <summary>
        /// The camera-local position of a tag whose real size differs from the size the
        /// detection pass assumed. The pose estimator solved for corners at ± assumed / 2;
        /// corners at ± real / 2 project to the same pixels exactly when the translation is
        /// scaled by real / assumed, and the rotation does not depend on the size. One
        /// detection pass therefore serves tags of any size.
        /// </summary>
        public static Vector3 RescaleToTagSize(Vector3 cameraLocalPosition, float realSize, float assumedSize)
        {
            if (assumedSize <= 0f) throw new ArgumentOutOfRangeException(nameof(assumedSize), "The assumed tag size must be positive.");
            return cameraLocalPosition * (realSize / assumedSize);
        }

        /// <summary>A pose given relative to the camera, as a world pose, for the camera pose the image was taken at.</summary>
        public static Pose ToWorld(Pose camera, Vector3 localPosition, Quaternion localRotation) =>
            new Pose(camera.position + camera.rotation * localPosition, camera.rotation * localRotation);

        /// <summary>
        /// Copy an image of <paramref name="width"/> × <paramref name="height"/> pixels with its
        /// rows in reverse order (top row last). The detector expects Unity's bottom-up row
        /// order; a vertically mirrored tag never decodes.
        /// </summary>
        public static void FlipRows<T>(T[] source, T[] destination, int width, int height)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "The image has no pixels.");
            if (source.Length < width * height || destination.Length < width * height)
                throw new ArgumentException("An image buffer is smaller than width × height.");
            if (ReferenceEquals(source, destination)) throw new ArgumentException("Flipping in place is not supported.", nameof(destination));
            for (int row = 0; row < height; row++)
                Array.Copy(source, row * width, destination, (height - 1 - row) * width, width);
        }

        /// <summary>
        /// The pinhole intrinsics of an image that is a centred crop of the camera sensor,
        /// scaled to <paramref name="width"/> × <paramref name="height"/> (how the Quest
        /// delivers a passthrough image at a resolution other than the sensor's). False for a
        /// sensor or an image without pixels.
        /// </summary>
        public static bool TryScaleIntrinsics(Vector2 sensorResolution, Vector2 focalLength, Vector2 principalPoint,
            int width, int height, out float fx, out float fy, out float cx, out float cy)
        {
            fx = fy = cx = cy = 0f;
            if (sensorResolution.x <= 0f || sensorResolution.y <= 0f || width <= 0 || height <= 0) return false;

            // The largest crop of the sensor with the image's aspect ratio, centred.
            var scale = new Vector2(width / sensorResolution.x, height / sensorResolution.y);
            scale /= Mathf.Max(scale.x, scale.y);
            float cropX = sensorResolution.x * (1f - scale.x) * 0.5f;
            float cropY = sensorResolution.y * (1f - scale.y) * 0.5f;
            float cropWidth = sensorResolution.x * scale.x;
            float cropHeight = sensorResolution.y * scale.y;

            float sx = width / cropWidth;
            float sy = height / cropHeight;
            fx = focalLength.x * sx;
            fy = focalLength.y * sy;
            cx = (principalPoint.x - cropX) * sx;
            cy = (principalPoint.y - cropY) * sy;
            return true;
        }

        /// <summary>
        /// The marker frame with its tilt removed: at the same spot, y straight up, heading
        /// kept. A printed tag on a table is horizontal, and what the detector reports of its
        /// tilt is mostly noise that would lean the robot and swing a long offset.
        /// </summary>
        public static Pose Level(Pose markerFrame)
        {
            Vector3 forward = Vector3.ProjectOnPlane(markerFrame.rotation * Vector3.forward, Vector3.up);
            if (forward.sqrMagnitude < 1e-6f)
            {
                // The tag stands upright (its top edge points up or down): take the heading from its right edge.
                Vector3 right = Vector3.ProjectOnPlane(markerFrame.rotation * Vector3.right, Vector3.up);
                if (right.sqrMagnitude < 1e-6f) return new Pose(markerFrame.position, Quaternion.identity);
                forward = Vector3.Cross(right.normalized, Vector3.up);
            }
            return new Pose(markerFrame.position, Quaternion.LookRotation(forward.normalized, Vector3.up));
        }

        /// <summary>
        /// Where a robot's base goes for a detected tag: the base pose relative to the marker
        /// frame (<paramref name="baseInMarker"/>, what a <see cref="MarkerAnchor"/> holds)
        /// applied to the tag's marker frame, levelled first when <paramref name="level"/> is set.
        /// </summary>
        public static Pose ProposeBase(Pose tag, Pose baseInMarker, bool level)
        {
            Pose marker = MarkerFrame(tag);
            if (level) marker = Level(marker);
            return new Pose(
                marker.position + marker.rotation * baseInMarker.position,
                (marker.rotation * baseInMarker.rotation).normalized);
        }

        /// <summary>
        /// The inverse of <see cref="ProposeBase"/> without levelling: the base pose relative to
        /// the marker frame for a robot at <paramref name="basePose"/> and a tag at
        /// <paramref name="tag"/>: the offset to enter on a <see cref="MarkerAnchor"/> when the robot and
        /// the tag are both where they should be.
        /// </summary>
        public static Pose BaseInMarker(Pose tag, Pose basePose)
        {
            Pose marker = MarkerFrame(tag);
            Quaternion inverse = Quaternion.Inverse(marker.rotation);
            return new Pose(inverse * (basePose.position - marker.position), (inverse * basePose.rotation).normalized);
        }

        /// <summary>Whether two poses agree to within a distance (m) and an angle (degrees).</summary>
        public static bool Agree(Pose a, Pose b, float distance, float angleDegrees) =>
            (a.position - b.position).sqrMagnitude <= distance * distance && Quaternion.Angle(a.rotation, b.rotation) <= angleDegrees;
    }
}
