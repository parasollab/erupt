using NUnit.Framework;
using UnityEngine;
using RosMessageTypes.Geometry;

namespace Erupt.Ros.Tests
{
    /// <summary>
    /// The scene's ROS ↔ Unity convention: ROS (x, y, z) is Unity (x, z, y) in the planning frame.
    /// Swapping two axes mirrors space, so a rotation has to be converted to match: a ROS
    /// orientation must turn a converted vector exactly as the Unity rotation turns the vector.
    /// </summary>
    public class RosUnityConversionTests
    {
        // Rotations that sit at or next to Euler-angle singularities (a cylinder laid on its
        // side is a 90° pitch), where a conversion through Euler angles loses precision.
        private static readonly Vector3[] AwkwardEulers =
        {
            new(0f, 0f, 0f), new(90f, 0f, 0f), new(-90f, 0f, 0f), new(90f, 30f, 0f), new(-90f, 0f, 45f),
            new(89.99f, 10f, 20f), new(270f, 200f, 100f), new(0f, 180f, 0f), new(0f, 0f, 180f), new(180f, 0f, 0f),
            new(90f, 90f, 90f), new(45f, 45f, 45f), new(1e-3f, 1e-3f, 1e-3f)
        };

        // q v q*, written out so no engine convention is involved (ROS quaternions are Hamilton).
        private static Vector3 RosRotate(QuaternionMsg q, Vector3 v)
        {
            var u = new Vector3((float)q.x, (float)q.y, (float)q.z);
            return v + 2f * Vector3.Cross(u, Vector3.Cross(u, v) + (float)q.w * v);
        }

        private static Vector3 ToVector(PointMsg p) => new Vector3((float)p.x, (float)p.y, (float)p.z);

        private static void AssertConsistent(Quaternion unity, string what)
        {
            QuaternionMsg ros = RosUnityConversion.UnityToRosQuaternion(unity);
            double norm = System.Math.Sqrt(ros.x * ros.x + ros.y * ros.y + ros.z * ros.z + ros.w * ros.w);
            Assert.AreEqual(1.0, norm, 1e-4, $"{what}: the ROS quaternion is a unit quaternion.");

            foreach (Vector3 v in new[] { Vector3.right, Vector3.up, Vector3.forward, new Vector3(0.3f, -0.5f, 0.8f) })
            {
                Vector3 turnedThenConverted = ToVector(RosUnityConversion.UnityToRosPosition(unity * v));
                Vector3 convertedThenTurned = RosRotate(ros, ToVector(RosUnityConversion.UnityToRosPosition(v)));
                Assert.Less((turnedThenConverted - convertedThenTurned).magnitude, 2e-4f,
                    $"{what}: the ROS orientation turns {v} differently from the Unity rotation.");
            }

            Quaternion back = RosUnityConversion.RosToUnityQuaternion(ros);
            Assert.Less(Quaternion.Angle(unity, back), 0.02f, $"{what}: Unity → ROS → Unity is the identity.");
        }

        [Test]
        public void Orientation_TurnsVectorsLikeThePositionMapping_AtAwkwardAngles()
        {
            foreach (Vector3 euler in AwkwardEulers) AssertConsistent(Quaternion.Euler(euler), $"Euler {euler}");
        }

        [Test]
        public void Orientation_TurnsVectorsLikeThePositionMapping_ForRandomRotations()
        {
            var random = new System.Random(20261121);
            for (int i = 0; i < 500; i++)
            {
                var q = new Quaternion((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f,
                                       (float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f).normalized;
                AssertConsistent(q, $"random rotation {i} {q}");
            }
        }

        [Test]
        public void Position_SwapsYAndZ_BothWays()
        {
            PointMsg ros = RosUnityConversion.UnityToRosPosition(new Vector3(1f, 2f, 3f));
            Assert.AreEqual(new Vector3(1f, 3f, 2f), ToVector(ros));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), RosUnityConversion.RosToUnityPosition(ros));
        }

        [Test]
        public void RosYaw_IsAUnityTurnAboutY_TheOtherWay()
        {
            // +90° about ROS z takes ROS x to ROS y, which is Unity x to Unity z.
            QuaternionMsg ros = RosUnityConversion.UnityToRosQuaternion(Quaternion.Euler(0f, -90f, 0f));
            Assert.AreEqual(0.0, ros.x, 1e-5);
            Assert.AreEqual(0.0, ros.y, 1e-5);
            Assert.AreEqual(Mathf.Sin(Mathf.PI / 4f), System.Math.Abs(ros.z), 1e-5);
            Assert.AreEqual(1.0, System.Math.Sign(ros.z) * System.Math.Sign(ros.w), "a positive rotation about ROS z");
            Vector3 unityForward = RosUnityConversion.RosToUnityQuaternion(new QuaternionMsg(0, 0, Mathf.Sin(Mathf.PI / 4f), Mathf.Cos(Mathf.PI / 4f))) * Vector3.right;
            Assert.Less((unityForward - Vector3.forward).magnitude, 1e-5f);
        }
    }
}
