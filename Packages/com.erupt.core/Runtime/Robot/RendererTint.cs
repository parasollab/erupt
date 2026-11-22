using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Robot
{
    /// <summary>
    /// Colour overrides on renderers, in layers, as property blocks: no material is
    /// instantiated, so nothing leaks and shared materials stay shared. Several things tint the
    /// same link (it belongs to the active robot, its joint is selected, its joint is at a
    /// limit); each sets and clears its own layer in any order and the highest layer that is
    /// set shows. When the last layer goes, the renderer has the property block it had before.
    /// </summary>
    public static class RendererTint
    {
        /// <summary>Who is tinting, lowest priority first.</summary>
        public enum Layer
        {
            /// <summary>The colour an object has when nothing else applies (a handle's idle colour).</summary>
            Base = 0,
            /// <summary>The object belongs to the robot the user is working with.</summary>
            ActiveRobot = 1,
            /// <summary>The object is selected or being dragged.</summary>
            Selected = 2,
            /// <summary>The joint that moves the object is at a limit.</summary>
            JointLimit = 3
        }

        private const int LayerCount = 4;

        private sealed class State
        {
            public MaterialPropertyBlock original;   // the block the renderer had before any layer, or null
            public readonly Color?[] layers = new Color?[LayerCount];
        }

        private static readonly Dictionary<Renderer, State> states = new();
        private static readonly List<Renderer> dead = new();
        private static MaterialPropertyBlock scratch;

        // Static state survives entering play mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay() => states.Clear();

        /// <summary>Show <paramref name="color"/> on a renderer for one layer.</summary>
        public static void Set(Renderer renderer, Layer layer, Color color)
        {
            if (renderer == null) return;
            if (!states.TryGetValue(renderer, out State state))
            {
                Prune();
                state = new State();
                if (renderer.HasPropertyBlock())
                {
                    state.original = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(state.original);
                }
                states[renderer] = state;
            }
            state.layers[(int)layer] = color;
            Apply(renderer, state);
        }

        /// <summary>Stop tinting a renderer for one layer; what the lower layers (or the renderer itself) show comes back.</summary>
        public static void Clear(Renderer renderer, Layer layer)
        {
            if (renderer == null || !states.TryGetValue(renderer, out State state)) return;
            state.layers[(int)layer] = null;
            Apply(renderer, state);
        }

        /// <summary>Whether a layer is set on a renderer.</summary>
        public static bool Has(Renderer renderer, Layer layer) =>
            renderer != null && states.TryGetValue(renderer, out State state) && state.layers[(int)layer].HasValue;

        /// <summary>The colour a material shows without any override (its shared material's base colour).</summary>
        public static Color SharedColor(Renderer renderer)
        {
            Material material = renderer != null ? renderer.sharedMaterial : null;
            return material != null ? MaterialColor.Get(material) : Color.white;
        }

        private static void Apply(Renderer renderer, State state)
        {
            Color? top = null;
            for (int i = LayerCount - 1; i >= 0 && top == null; i--) top = state.layers[i];

            // Start from what the renderer had, so properties other than the colour survive.
            renderer.SetPropertyBlock(state.original);
            if (top == null)
            {
                states.Remove(renderer);
                return;
            }
            scratch ??= new MaterialPropertyBlock();
            renderer.GetPropertyBlock(scratch);
            MaterialColor.Set(scratch, top.Value);
            renderer.SetPropertyBlock(scratch);
        }

        // Renderers destroyed while tinted leave their entry behind; dropped now and then.
        private static void Prune()
        {
            if (states.Count < 64) return;
            dead.Clear();
            foreach (Renderer renderer in states.Keys)
                if (renderer == null) dead.Add(renderer);
            foreach (Renderer renderer in dead) states.Remove(renderer);
        }
    }
}
