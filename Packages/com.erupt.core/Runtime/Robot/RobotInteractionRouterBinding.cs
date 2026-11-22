using System.Collections.Generic;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Robot
{
    /// <summary>
    /// Puts one robot's <see cref="RobotInteractionController"/> under the interaction router:
    /// it hands the controller to the router's <see cref="RobotIntentDispatcher"/>, which
    /// decides which robot each intent is for. One per robot, on the robot's prefab.
    /// </summary>
    /// <remarks>
    /// This component reads no device state and contains no platform branch — the intents it
    /// connects were filtered, dead-zoned and resolved to a target in the router.
    /// Guidelines Part 3.
    /// </remarks>
    public class RobotInteractionRouterBinding : MonoBehaviour
    {
        [SerializeField] private InteractionRouter router;
        [SerializeField] private RobotInteractionController robotInteraction;

        [Tooltip("How fast the thumbstick axis turns the selected joint.")]
        [SerializeField] private float jointJogRadiansPerSecond = 0.8f;

        private RobotIntentDispatcher dispatcher;

        /// <summary>The dispatcher this robot's controller is served by; null while the binding is off.</summary>
        public RobotIntentDispatcher Dispatcher => dispatcher;

        private void Awake()
        {
            if (router == null) router = FindFirstObjectByType<InteractionRouter>();
            // The controller of the robot this binding sits under; never another robot's.
            if (robotInteraction == null)
            {
                RobotInstance robot = GetComponentInParent<RobotInstance>(true);
                robotInteraction = robot != null ? robot.Interaction : GetComponentInParent<RobotInteractionController>(true);
            }
            EnsureLimitIndicator();
        }

        // The robot shows its own joint-limit refusals: a tint on the link whose joint is at a
        // limit. One per robot, on the robot's root, fed by the same router.
        private void EnsureLimitIndicator()
        {
            if (router == null) return;
            RobotInstance robot = GetComponentInParent<RobotInstance>(true);
            GameObject host = robot != null ? robot.gameObject : gameObject;
            if (!host.TryGetComponent(out JointLimitIndicator indicator))
                indicator = host.AddComponent<JointLimitIndicator>();
            indicator.SetRouter(router);
        }

        private void OnEnable()
        {
            if (router == null || robotInteraction == null)
            {
                Debug.LogError("RobotInteractionRouterBinding: router or robotInteraction not assigned.");
                enabled = false;
                return;
            }

            dispatcher = RobotIntentDispatcher.For(router);
            dispatcher.Add(robotInteraction, GetComponentInParent<RobotInstance>(true), jointJogRadiansPerSecond);
        }

        private void OnDisable()
        {
            if (dispatcher != null) dispatcher.Remove(robotInteraction);
            dispatcher = null;
        }
    }
}
