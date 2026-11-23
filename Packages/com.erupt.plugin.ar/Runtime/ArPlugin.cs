using System;
using System.Text;
using TMPro;
using UnityEngine;
using Erupt.Interaction;
using Erupt.Robot;
using Erupt.Ui;

namespace Erupt.Plugins.Ar
{
    /// <summary>
    /// The AR module as an ERUPT plugin: places robots at printed AprilTags, for any number of
    /// robots. Each robot may carry a <see cref="MarkerAnchor"/> (which tag, how large, where
    /// the base stands relative to it). While a robot's tag is in view the plugin shows where
    /// the robot would go, as a translucent copy of the robot, and the user confirms with the
    /// button beside its proposed base. Nothing moves until then, and a placed robot
    /// stays where it is when its tag goes out of view.
    /// </summary>
    /// <remarks>
    /// The move itself is core's (<see cref="RobotInstance.PlaceBase(Pose, out string)"/>), so
    /// whatever holds something in the robot's planning frame hears of it
    /// (<see cref="RobotInstance.BaseMoved"/>): the MoveIt plugin sends the robot's planning
    /// scene every object again and drops the robot's goal and plans. Placing is refused, with
    /// the reason, while the robot executes or carries something, while its tag is not in view
    /// and before it was ever seen. Undo puts the base back.
    ///
    /// The plugin reads tags from the <see cref="AprilTagTracker"/>; it does not use the
    /// camera itself, so it runs (and is tested) without a headset.
    /// </remarks>
    public class ArPlugin : EruptPluginBehaviour
    {
        /// <summary>Identifier of the retired robot-menu verb.</summary>
        [Obsolete("Placement is confirmed by the world widget beside the proposed base.")]
        public const string PlaceVerb = "place-at-marker";
        public const string TabId = "ar";

        [Tooltip("Reads the printed tags. On this object or below it by default; found in the scene if empty.")]
        [SerializeField] private AprilTagTracker tracker;

        [Tooltip("A tag counts as in view for this many seconds after it was last detected.")]
        [SerializeField, Min(0.05f)] private float inViewSeconds = 2f;

        [Header("Proposal")]
        [Tooltip("No copy is shown while the robot already stands where its tag puts it, to within this distance (m)...")]
        [SerializeField, Min(0f)] private float agreementDistance = 0.02f;
        [Tooltip("...and this angle (degrees).")]
        [SerializeField, Min(0f)] private float agreementAngle = 2f;

        /// <summary>What the plugin keeps for one robot.</summary>
        public sealed class RobotSession : IDisposable
        {
            private readonly ArPlugin plugin;
            private MarkerAnchor anchor;
            private float nextAnchorLookup;
            private int trackedId = -1;
            private float trackedSize;
            private bool trackedOk;
            private bool ghostUnavailable;
            private Pose shown;
            private float nextGhostPose;
            private PlacementWidget widget;

            public RobotInstance Robot { get; }

            /// <summary>The robot's marker anchor; null for a robot that is not bound to a tag.</summary>
            public MarkerAnchor Anchor
            {
                get
                {
                    if (anchor == null && Robot != null && Time.unscaledTime >= nextAnchorLookup)
                    {
                        // A robot without an anchor is asked again now and then: one may be added while the app runs.
                        nextAnchorLookup = Time.unscaledTime + 1f;
                        anchor = MarkerAnchor.Of(Robot);
                    }
                    return anchor;
                }
            }

            /// <summary>The robot's tag is in view now, so <see cref="Proposal"/> is current.</summary>
            public bool HasProposal { get; private set; }
            /// <summary>Where the robot's base would be placed; meaningful while <see cref="HasProposal"/>.</summary>
            public Pose Proposal { get; private set; }
            /// <summary>A copy of the robot is shown at <see cref="Proposal"/>.</summary>
            public bool ShowsProposal { get; private set; }

            internal RobotSession(ArPlugin plugin, RobotInstance robot)
            {
                this.plugin = plugin;
                Robot = robot;
            }

            // The tracker looks for the anchor's tag; false when the tag's size contradicts another user's.
            internal bool TagIsTracked(AprilTagTracker tracker, MarkerAnchor from)
            {
                if (trackedId != from.TagId || !Mathf.Approximately(trackedSize, from.TagSize))
                {
                    trackedId = from.TagId;
                    trackedSize = from.TagSize;
                    trackedOk = tracker.Track(from.TagId, from.TagSize);
                }
                return trackedOk;
            }

            internal void Refresh(AprilTagTracker tracker, Camera viewer)
            {
                MarkerAnchor from = Anchor;
                HasProposal = false;
                if (from != null && tracker != null && Robot != null && TagIsTracked(tracker, from) &&
                    tracker.IsVisible(from.TagId, plugin.inViewSeconds) && tracker.TryGetTag(from.TagId, out TrackedTag tag))
                {
                    Proposal = from.ProposedBase(tag.Pose);
                    HasProposal = true;
                }

                bool show = HasProposal && !MarkerMath.Agree(Proposal, Robot.BasePose, plugin.agreementDistance, plugin.agreementAngle);
                RobotGhosts ghosts = Robot != null ? Robot.Ghosts : null;
                if (show && ghosts != null && !ghostUnavailable)
                {
                    // The copy is moved when the proposal moved (a few times a second, with the
                    // detections) and re-posed now and then for the robot's joints; in between
                    // it is left alone.
                    bool current = ghosts.PlacementGhost != null && shown.position == Proposal.position && shown.rotation == Proposal.rotation &&
                                   Time.unscaledTime < nextGhostPose;
                    if (!current)
                    {
                        // A robot without a ghost prefab is placed without a preview; said once by RobotGhosts.
                        if (!ghosts.ShowPlacementGhost(Proposal)) ghostUnavailable = true;
                        shown = Proposal;
                        nextGhostPose = Time.unscaledTime + 0.25f;
                    }
                }
                else if (ghosts != null)
                {
                    ghosts.ClearPlacementGhost();
                }
                ShowsProposal = show && ghosts != null && !ghostUnavailable;
                if (show && plugin.Context.Ui != null)
                {
                    widget ??= new PlacementWidget(plugin.Context.Ui, plugin.transform, Robot.Id, Confirm);
                    widget.Follow(Proposal.position, viewer, plugin.Sessions.Count > 1);
                }
                else
                {
                    ClearWidget();
                }
            }

            private void Confirm()
            {
                if (widget == null) return;
                Vector3 at = widget.GameObject.transform.position;
                InteractionRefusal refusal = plugin.PlaceAtMarker(Robot, Proposal);
                if (refusal.IsRefused)
                {
                    plugin.Context?.Router?.ReportRefusal(InteractionRefusal.Refuse(refusal.Code, refusal.Reason, at, refusal.Subject));
                    plugin.Refresh();
                }
            }

            private void ClearWidget()
            {
                widget?.Dispose();
                widget = null;
            }

            public void Dispose()
            {
                ClearWidget();
                HasProposal = false;
                ShowsProposal = false;
                RobotGhosts ghosts = Robot != null ? Robot.Ghosts : null;
                if (ghosts != null) ghosts.ClearPlacementGhost();
            }
        }

        private RobotSessions<RobotSession> sessions;
        private TextMeshProUGUI statusLabel;
        private float nextStatus;

        public override string Id => "ar";
        public override string DisplayName => "AR";

        /// <summary>Every robot's session, in registration order; null before registration.</summary>
        public RobotSessions<RobotSession> Sessions => sessions;
        /// <summary>A robot's session; null before registration.</summary>
        public RobotSession SessionFor(RobotInstance robot) => sessions?.For(robot);
        /// <summary>The tracker the plugin reads tags from; null when the scene has none.</summary>
        public AprilTagTracker Tracker => tracker;

        /// <summary>Read tags from this tracker (before or after registration).</summary>
        public void SetTracker(AprilTagTracker source) => tracker = source;

        protected override void OnRegister(IEruptContext context)
        {
            if (tracker == null) tracker = GetComponentInChildren<AprilTagTracker>(true);
            if (tracker == null) tracker = FindFirstObjectByType<AprilTagTracker>(FindObjectsInactive.Include);
            if (tracker == null)
                Debug.LogWarning("[ar] No AprilTagTracker in the scene; robots cannot be placed at markers.", this);

            sessions = new RobotSessions<RobotSession>(context.Robots, robot => new RobotSession(this, robot));

            IUiHost ui = context.Ui;
            if (ui == null) return;

            Erupt.Ui.PanelTab tab = ui.AddTab(TabId, DisplayName, BuildTab);
            if (tab != null) tab.Order = 40;
        }

        protected override void OnUnregister(IEruptContext context)
        {
            sessions?.Dispose();
            sessions = null;
            statusLabel = null;
            context?.Ui?.RemoveTab(TabId);
        }

        private void Update()
        {
            if (sessions == null) return;
            Refresh();
            if (statusLabel != null && Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 0.5f;
                statusLabel.text = StatusText();
            }
        }

        /// <summary>
        /// Update every proposed base, ghost and confirmation button from the tracker.
        /// </summary>
        public void Refresh()
        {
            if (sessions == null) return;
            Camera viewer = Camera.main;
            foreach (RobotSession session in sessions) session.Refresh(tracker, viewer);
        }

        /// <summary>
        /// Put a robot's base where its tag says, now. Returns a refusal with the reason when
        /// that cannot be done: the robot has no marker anchor, its tag has not been seen yet
        /// or is not in view, or the robot is executing or carrying something. The move goes
        /// on the undo stack; undo puts the base back.
        /// </summary>
        public InteractionRefusal PlaceAtMarker(RobotInstance robot) => PlaceAtMarker(robot, null);

        private InteractionRefusal PlaceAtMarker(RobotInstance robot, Pose? preview)
        {
            if (robot == null) return Refuse(null, "Select a robot to place it at its marker.");
            RobotSession session = SessionFor(robot);
            if (session == null) return Refuse(robot, "The AR plugin is not running.");

            MarkerAnchor anchor = session.Anchor;
            if (anchor == null) return Refuse(robot, $"Robot '{robot.Id}' has no marker anchor, so no tag says where it goes.");
            if (tracker == null) return Refuse(robot, "There is no AprilTag tracker in the scene.");
            if (!session.TagIsTracked(tracker, anchor))
            {
                tracker.TryGetTagSize(anchor.TagId, out float size);
                return Refuse(robot, $"Tag {anchor.TagId} is used with two sizes ({anchor.TagSize:0.###} m on robot '{robot.Id}', {size:0.###} m elsewhere); give it one size.");
            }

            if (!tracker.TryGetTag(anchor.TagId, out TrackedTag tag))
                return Refuse(robot, tracker.IsReady
                    ? $"Tag {anchor.TagId} has not been seen yet. Look at the printed tag."
                    : $"Tag {anchor.TagId} has not been seen yet: the passthrough camera is not running.");
            float age = tracker.Now - tag.LastSeenTime;
            if (age > inViewSeconds)
                return Refuse(robot, $"Tag {anchor.TagId} is not in view (last seen {age:0} s ago). Look at the printed tag.");

            if (!robot.CanPlaceBase(out string reason)) return Refuse(robot, $"{reason} It cannot be moved now.");

            Pose latest = anchor.ProposedBase(tag.Pose);
            if (preview.HasValue && !MarkerMath.Agree(preview.Value, latest, agreementDistance, agreementAngle))
                return Refuse(robot, "The marker moved. Check the new placement and press again.");

            var command = new PlaceBaseCommand(robot, preview ?? latest);
            // An undo or redo that the robot refuses later (it is executing by then) says so too.
            command.Refused += why => Context?.Router?.ReportRefusal(Refuse(robot, $"{why} It cannot be moved now."));
            UndoStack undo = Context?.Undo;
            if (undo != null) undo.Do(command);
            else command.Execute();
            Refresh();
            return InteractionRefusal.None;
        }

        private static InteractionRefusal Refuse(RobotInstance robot, string reason) =>
            InteractionRefusal.Refuse(RefusalCode.BaseNotPlaced, reason,
                robot != null ? robot.transform.position : Vector3.zero, robot != null ? robot.transform : null);

        // --- tab ---------------------------------------------------------------------------

        private void BuildTab(RectTransform content)
        {
            statusLabel = null;
            if (content == null) return;   // a headless UI host (tests) passes no content
            var column = UiBuilder.CreateColumn("AR", content);
            UiBuilder.Stretch(column.GetComponent<RectTransform>());
            statusLabel = UiBuilder.CreateLabel("Status", column.transform, StatusText(), 22f);
            statusLabel.alignment = TextAlignmentOptions.TopLeft;
        }

        /// <summary>The tracker's state and, per robot, its tag and what the user can do next.</summary>
        public string StatusText()
        {
            var text = new StringBuilder();
            text.Append("Tracker: ").Append(tracker != null ? tracker.StatusText : "none in the scene");
            if (sessions == null) return text.ToString();
            foreach (RobotSession session in sessions)
            {
                if (session.Robot == null) continue;
                text.Append('\n').Append(session.Robot.Id).Append(": ");
                MarkerAnchor anchor = session.Anchor;
                if (anchor == null) { text.Append("no marker anchor"); continue; }
                text.Append("tag ").Append(anchor.TagId);
                if (session.HasProposal)
                {
                    float distance = Vector3.Distance(session.Proposal.position, session.Robot.BasePose.position);
                    text.Append(!MarkerMath.Agree(session.Proposal, session.Robot.BasePose, agreementDistance, agreementAngle)
                        ? $" in view, {distance * 100f:0} cm from the robot. Press the placement button beside the proposed base."
                        : " in view; the robot stands where it puts it.");
                }
                else if (tracker != null && tracker.TryGetTag(anchor.TagId, out _)) text.Append(" not in view");
                else text.Append(" not seen yet");
            }
            return text.ToString();
        }
    }
}
