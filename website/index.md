---
_layout: landing
title: ERUPT Documentation
description: Plan your first robot motion in Unity, connect ROS 2, and build with the Extended Reality Universal Programming Toolkit.
---

<section class="field-hero" aria-labelledby="erupt">
  <div class="field-hero-inner">
    <div class="field-intro">
      <p class="field-label">ERUPT Documentation</p>
      <h1 id="erupt">Robot programming<br> in extended reality.</h1>
      <p class="field-summary">Pose a robot arm, preview its motion, and run the plan. Start with a mouse in Unity or step into the scene with a Meta Quest headset.</p>
      <div class="field-actions">
        <a class="field-button" href="guides/quick-start.md">Get started</a>
        <a class="field-source" href="https://github.com/parasollab/erupt">View on GitHub</a>
      </div>
    </div>
    <figure class="field-view">
      <img width="1600" height="1000" src="images/home-plan-preview.webp" alt="A Franka arm on a kitchen counter with three translucent copies of itself. A green copy over a pot marks the start, an orange copy over a bowl marks the goal, and a blue copy between them plays the planned motion. The plan's menu offers Execute and Stop Preview.">
      <figcaption><span>KitchenFR3</span><span class="start-key">Start</span><span class="goal-key">Goal</span><span class="motion-key">Motion preview</span><span class="handle-key">Plan handle</span></figcaption>
    </figure>
  </div>
</section>

<div class="field-body">
  <section aria-labelledby="start-here">
    <h2 id="start-here">Explore the toolkit</h2>
    <div class="field-paths">
      <section>
        <i class="bi bi-bounding-box-circles" aria-hidden="true"></i>
        <h3>Operate the robot</h3>
        <p>Set a goal, place obstacles, and inspect a plan before the arm moves.</p>
        <ul>
          <li><a href="guides/using-the-app.md">Controls and motion planning</a></li>
          <li><a href="guides/demonstrations.md">Record a demonstration</a></li>
          <li><a href="guides/ar.md">Place the robot in AR</a></li>
        </ul>
      </section>
      <section>
        <i class="bi bi-layers" aria-hidden="true"></i>
        <h3>Set up a workspace</h3>
        <p>Connect Unity to MoveIt 2 and choose the arms for your scene.</p>
        <ul>
          <li><a href="guides/ros-setup.md">Connect ROS 2</a></li>
          <li><a href="guides/robots.md">Choose a robot</a></li>
          <li><a href="guides/multi-arm.md">Run several arms</a></li>
        </ul>
      </section>
      <section>
        <i class="bi bi-braces" aria-hidden="true"></i>
        <h3>Build with ERUPT</h3>
        <p>Bring the toolkit into a Unity project or add your own planning method.</p>
        <ul>
          <li><a href="guides/install-package.md">Install the packages</a></li>
          <li><a href="guides/add-a-robot.md">Add a robot from URDF</a></li>
          <li><a href="guides/write-a-plugin.md">Write a plugin</a></li>
        </ul>
      </section>
    </div>
  </section>
  <div class="field-notes">
    <section aria-labelledby="how-it-is-put-together">
      <h2 id="how-it-is-put-together">Inside ERUPT</h2>
      <p>The Extended Reality Universal Programming Toolkit is a set of Unity packages. Core handles input, inverse kinematics, joint limits, and ROS transport. Plugins add motion planning, pick-and-place tasks, demonstrations, and AR placement.</p>
      <p><a href="../Packages/com.erupt.core/Documentation~/architecture.md">Architecture reference</a> <span aria-hidden="true">/</span> <a href="api/index.md">API reference</a></p>
    </section>
    <section aria-labelledby="requirements">
      <h2 id="requirements">What you’ll need</h2>
      <p>Use Unity 6 and Docker to try the simulated robot. For a headset session, use a Meta Quest 3. An existing ROS 2 Jazzy and MoveIt 2 setup can also supply the robot.</p>
      <p><a href="guides/troubleshooting.md">Troubleshoot your setup</a></p>
    </section>
  </div>
</div>
