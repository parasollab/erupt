# Coming soon

Actions that are planned for ERUPT, but are not available yet. They are all actions for the menu that appears on a selected thing (tier 2;
see [Using the app](using-the-app.md)), and they are listed here by what you select.

## On an obstacle

| Action | What it is for |
| --- | --- |
| **Reshape** | Change an obstacle's shape, beyond making it larger or smaller. |
| **Paint Cost** | Brush a cost onto an obstacle, so that the planner keeps the robot further from the painted parts. |
| **Lock** | Fix an obstacle so that it cannot be moved or changed by accident. |

Today an obstacle's menu has **Resize**, **Delete**, **Snap to Surface** and **Duplicate**.

## On an object the robot handles

| Action | What it is for |
| --- | --- |
| **Grasp Here** | Show where and how the gripper should hold the object. A gripper outline snaps to the surface, you adjust it and confirm, and the grasp is kept for objects of the same type. |
| **Set as Target** | Make the object the target of the robot's next task. |
| **Properties** | Look at and edit what ERUPT knows about the object. |

## On a robot link

| Action | What it is for |
| --- | --- |
| **Lock Joint** | Hold a joint at its angle while the rest of the arm moves. |

## On the end effector

| Action | What it is for |
| --- | --- |
| **Preview Grasp** | See a grasp on the gripper before the robot uses it. |

Today the end effector's menu has **Set Start**, **Set Goal**, **Plan** and
**Open/Close Gripper**.

## On a plan

| Action | What it is for |
| --- | --- |
| **Scrub** | Move back and forth through a plan along its path, at your own pace. |
| **Correct** | Show the robot a better motion than the one it planned, without starting over. |
| **Compare** | Look at two plans for the same goal side by side. |

Today a plan's menu has **Execute** and **Preview**.

How a correction should begin, and how it should relate to the plan it corrects, is still
being designed. In the meantime, record the motion you want as a demonstration from the
**Demonstrations** tab. See [Collecting demonstrations](demonstrations.md).

## On a point of a plan

A plan cannot be edited point by point yet. Once it can, a selected point will have:

| Action | What it is for |
| --- | --- |
| **Move** | Drag the point somewhere else. |
| **Delete** | Take the point out of the plan. |
| **Pin Timing** | Fix the moment at which the robot reaches the point. |
