using NUnit.Framework;
using UnityEngine;
using Erupt.UiBindings;

namespace Erupt.Environment.Tests
{
    /// <summary>
    /// The size arithmetic behind the Scene tab and the resize widget. Unity's cylinder
    /// and capsule meshes are 2 m tall at Y scale 1, and every number here has to agree
    /// with the MoveIt publisher, which doubles Y back into a height.
    /// </summary>
    public class ObstacleResizeTests
    {
        [Test]
        public void DefaultScale_Cube_IsUniform()
        {
            Assert.AreEqual(new Vector3(0.3f, 0.3f, 0.3f), ScenePlacementTab.DefaultScaleFor(PrimitiveType.Cube, 0.3f));
            Assert.AreEqual(new Vector3(0.3f, 0.3f, 0.3f), ScenePlacementTab.DefaultScaleFor(PrimitiveType.Sphere, 0.3f));
        }

        [Test]
        public void DefaultScale_Cylinder_HalvesY_SoHeightMatchesDiameter()
        {
            Assert.AreEqual(new Vector3(0.3f, 0.15f, 0.3f), ScenePlacementTab.DefaultScaleFor(PrimitiveType.Cylinder, 0.3f));
            Assert.AreEqual(new Vector3(0.3f, 0.15f, 0.3f), ScenePlacementTab.DefaultScaleFor(PrimitiveType.Capsule, 0.3f));
        }

        [Test]
        public void Axes_FollowShape()
        {
            CollectionAssert.AreEqual(new[] { ResizeAxis.Width, ResizeAxis.Height, ResizeAxis.Depth }, ObstacleResizeWidget.AxesFor(PrimitiveType.Cube));
            CollectionAssert.AreEqual(new[] { ResizeAxis.Height, ResizeAxis.Diameter }, ObstacleResizeWidget.AxesFor(PrimitiveType.Cylinder));
            CollectionAssert.AreEqual(new[] { ResizeAxis.Size }, ObstacleResizeWidget.AxesFor(PrimitiveType.Sphere));
            CollectionAssert.AreEqual(new[] { ResizeAxis.Size }, ObstacleResizeWidget.AxesFor(null), "unknown meshes scale uniformly");
        }

        [Test]
        public void ScaleDelta_Cube_MovesOneAxis()
        {
            Assert.AreEqual(new Vector3(0.02f, 0f, 0f), ObstacleResizeWidget.ScaleDeltaFor(PrimitiveType.Cube, ResizeAxis.Width, 0.02f));
            Assert.AreEqual(new Vector3(0f, 0.02f, 0f), ObstacleResizeWidget.ScaleDeltaFor(PrimitiveType.Cube, ResizeAxis.Height, 0.02f));
            Assert.AreEqual(new Vector3(0f, 0f, -0.02f), ObstacleResizeWidget.ScaleDeltaFor(PrimitiveType.Cube, ResizeAxis.Depth, -0.02f));
        }

        [Test]
        public void ScaleDelta_Cylinder_HeightIsHalfInScaleUnits_DiameterIsXZ()
        {
            Assert.AreEqual(new Vector3(0f, 0.01f, 0f), ObstacleResizeWidget.ScaleDeltaFor(PrimitiveType.Cylinder, ResizeAxis.Height, 0.02f));
            Assert.AreEqual(new Vector3(0.02f, 0f, 0.02f), ObstacleResizeWidget.ScaleDeltaFor(PrimitiveType.Cylinder, ResizeAxis.Diameter, 0.02f));
        }

        [Test]
        public void ScaleDelta_Sphere_IsUniform()
        {
            Assert.AreEqual(new Vector3(0.02f, 0.02f, 0.02f), ObstacleResizeWidget.ScaleDeltaFor(PrimitiveType.Sphere, ResizeAxis.Size, 0.02f));
        }

        [Test]
        public void Clamp_KeepsEveryVisibleDimensionAboveMinimum()
        {
            Assert.AreEqual(new Vector3(0.02f, 0.5f, 0.02f), ObstacleResizeWidget.ClampScale(new Vector3(-1f, 0.5f, 0f), PrimitiveType.Cube, 0.02f));
            // A cylinder's Y scale is half its height, so the floor is half too.
            Assert.AreEqual(new Vector3(0.02f, 0.01f, 0.02f), ObstacleResizeWidget.ClampScale(Vector3.zero, PrimitiveType.Cylinder, 0.02f));
        }
    }
}
