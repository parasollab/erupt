# Several robot arms

ERUPT works with any number of arms in one scene, of the same model or of different ones. Each
arm is an independent robot on the ROS side, in its own namespace, with its own MoveIt. In the
headset they share one scene: an obstacle you place is known to every arm's planner.

<figure class="guide-shot">
  <div class="namespace-map">
    <img width="1600" height="1000" src="../images/multi-arm-fr3.webp" alt="Three Franka arms in MultiArmFR3, standing around two obstacles. The arm on the left uses /third, the arm at the back uses /right, and the arm on the right uses /left.">
    <span aria-hidden="true" style="--arm-x: 22%; --arm-y: 31%">/third</span>
    <span aria-hidden="true" style="--arm-x: 50%; --arm-y: 29%">/right</span>
    <span aria-hidden="true" style="--arm-x: 76%; --arm-y: 32%">/left</span>
  </div>
  <figcaption>Each label shows the arm’s ROS namespace in <code>MultiArmFR3</code>. Selecting an arm switches the controls to its planner.</figcaption>
</figure>

## 1. Start one ROS stack per arm

Give every arm a namespace and say which robot it is:

```bash
ARMS="left=fr3 right=ur5e third=xarm7" \
  docker compose -f docker/compose.yaml --profile arms up arms
```

For the three Franka arms in the bundled `MultiArmFR3` scene, use
`ARMS="left=fr3 right=fr3 third=fr3"` in the same command.

Without Docker, the same list goes to the launch file:

```bash
ros2 launch erupt_bringup arms.launch.py arms:="left=fr3 right=ur5e third=xarm7"
```

There is one connection to Unity for all arms, on port 10000 as before. You do not tell the
ROS side where the arms stand.

## 2. Put the arms into the scene

1. Drag a robot prefab into the scene for every arm (see [Robots](robots.md)) and place it
   where the arm stands.
2. Select each robot and set, on its **Robot Instance** component:

   | Field | Value |
   | --- | --- |
   | **Id** | A name of your choice, shown in the headset, for example `left` |
   | **Ros Namespace** | The namespace you gave that arm in step 1, for example `left` |

<figure class="guide-shot">
  <a href="../images/editor-robot-instance.webp"><img width="1040" height="990" src="../images/editor-robot-instance.webp" alt="The Inspector for the robot named left in MultiArmFR3. Its Robot Instance component has Id left, Ros Namespace left and the FR3 profile." loading="lazy"></a>
  <figcaption>The <strong>Robot Instance</strong> component of the arm <code>left</code> in <code>MultiArmFR3</code>. The fields below <strong>Profile</strong> are filled in by the prefab.</figcaption>
</figure>

That is all. The plugins find every robot in the scene on their own.

`Assets/Scenes/MultiArmFR3` is a finished example with three arms.

In the editor, open the scene and press Play. A headset build starts in the first scene of
the scene list: open **File > Build Profiles**, drag your scene (or `MultiArmFR3`) to the top
of the list, and build.

## How it behaves in the headset

- **You act on the arm you touch.** Selecting an arm's end effector, a joint or one of its plans
  makes that arm the active one. Its name appears in the menu header and on the tabs.
- **Two hands, two arms.** You can drag two arms at the same time, one with each hand.
- **Obstacles are shared.** Every obstacle goes to every arm's planning scene, each time
  expressed relative to that arm.
- **Plans, tasks and demonstrations belong to one arm.** Each arm has its own start, goal,
  plans, pick-and-place tasks and recordings.

<div class="shot-pair">
  <figure class="guide-shot">
    <a href="../images/multi-arm-active.webp"><img width="1600" height="1000" src="../images/multi-arm-active.webp" alt="The end effector of the arm on the right is selected. Its menu is headed End Effector, left." loading="lazy"></a>
    <figcaption>The menu names the arm it acts on: <strong>End Effector · left</strong>.</figcaption>
  </figure>
  <figure class="guide-shot">
    <a href="../images/panel-moveit-left.webp"><img width="1055" height="750" src="../images/panel-moveit-left.webp" alt="The panel with the tabs Scene, MoveIt left, MTC left, Demonstrations left and Settings." loading="lazy"></a>
    <figcaption>So do the tabs that belong to one arm.</figcaption>
  </figure>
</div>

<figure class="guide-shot">
  <a href="../images/multi-arm-plans.webp"><img width="1600" height="1000" src="../images/multi-arm-plans.webp" alt="Two of the three arms each have a green start copy, an orange goal copy and a blue copy playing a plan. The third arm stands still." loading="lazy"></a>
  <figcaption>Two arms with a plan each, previewing at the same time. Both arms have been turned aside at the base so that their copies can be seen: green for the start, orange for the goal, blue for the motion.</figcaption>
</figure>

## What to know

- **The arms do not avoid each other.** Each arm plans as if it were alone with the obstacles.
  Keep their workspaces apart, or execute one at a time when they overlap.
- **An arm can be moved.** Every robot carries its own planning frame, so moving a robot in
  the scene needs no change on the ROS side.
- **Namespaces must match exactly.** If an arm stays in its start pose and does not follow
  its ROS robot, compare the **Ros Namespace** field with the name in the `ARMS` list.

## When the ROS side needs one common frame

If other ROS software needs all arms in one frame, the arms can share a planning frame instead.
Start each arm with its base pose (`left=fr3@x,y,z,yaw`, in metres and radians), and in Unity
assign the same transform to the **Planning Frame** field of each of those robots, with the
robots placed at the same poses relative to it. Only robots whose description leaves the base
free take a pose this way (`fr3`, `iiwa14`).
