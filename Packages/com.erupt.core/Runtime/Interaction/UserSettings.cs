using System.Collections.Generic;
using UnityEngine;

namespace Erupt.Interaction
{
    /// <summary>Where the user's settings are kept between sessions.</summary>
    public interface IUserSettingsStore
    {
        bool TryGetFloat(string key, out float value);
        void SetFloat(string key, float value);
        /// <summary>Write what was set to where it survives the app being closed.</summary>
        void Save();
    }

    /// <summary>
    /// The user's settings: what they changed on the Settings tab, remembered between
    /// sessions. A component reads its setting with its serialized value as the default
    /// (<see cref="GetFloat"/>, <see cref="GetBool"/>) and writes it when the user changes it.
    /// </summary>
    /// <remarks>
    /// Kept in <see cref="PlayerPrefs"/> by default. Tests put a <see cref="MemoryUserSettingsStore"/>
    /// in <see cref="Store"/> so that they neither read nor change what the person at this
    /// machine has set.
    /// </remarks>
    public static class UserSettings
    {
        private static IUserSettingsStore store = new PlayerPrefsUserSettingsStore();

        /// <summary>The store in use; assigning null goes back to <see cref="PlayerPrefs"/>.</summary>
        public static IUserSettingsStore Store
        {
            get => store;
            set => store = value ?? new PlayerPrefsUserSettingsStore();
        }

        public static float GetFloat(string key, float fallback) =>
            store.TryGetFloat(key, out float value) ? value : fallback;

        public static void SetFloat(string key, float value) => store.SetFloat(key, value);

        public static bool GetBool(string key, bool fallback) =>
            store.TryGetFloat(key, out float value) ? value > 0.5f : fallback;

        public static void SetBool(string key, bool value) => store.SetFloat(key, value ? 1f : 0f);

        public static void Save() => store.Save();
    }

    /// <summary>Settings in <see cref="PlayerPrefs"/>: per user and per app, on the headset and in the editor.</summary>
    public sealed class PlayerPrefsUserSettingsStore : IUserSettingsStore
    {
        public bool TryGetFloat(string key, out float value)
        {
            bool has = PlayerPrefs.HasKey(key);
            value = has ? PlayerPrefs.GetFloat(key) : 0f;
            return has;
        }

        public void SetFloat(string key, float value) => PlayerPrefs.SetFloat(key, value);

        public void Save() => PlayerPrefs.Save();
    }

    /// <summary>Settings that live as long as the object: for tests.</summary>
    public sealed class MemoryUserSettingsStore : IUserSettingsStore
    {
        private readonly Dictionary<string, float> values = new();

        /// <summary>How often <see cref="Save"/> was called.</summary>
        public int Saves { get; private set; }

        public bool TryGetFloat(string key, out float value) => values.TryGetValue(key, out value);
        public void SetFloat(string key, float value) => values[key] = value;
        public void Save() => Saves++;
        public bool Has(string key) => values.ContainsKey(key);
    }
}
