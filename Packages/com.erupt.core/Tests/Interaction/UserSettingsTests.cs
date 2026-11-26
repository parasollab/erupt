using NUnit.Framework;
using UnityEngine;
using Erupt.Interaction;

namespace Erupt.Interaction.Tests
{
    /// <summary>
    /// The user's settings as text (the ROS address is one): read with a default, written,
    /// and forgotten again, in a store a test can replace.
    /// </summary>
    public class UserSettingsTests
    {
        private const string Key = "erupt.test.userSettings.text";
        private MemoryUserSettingsStore store;

        [SetUp]
        public void SetUp()
        {
            store = new MemoryUserSettingsStore();
            UserSettings.Store = store;
        }

        [TearDown]
        public void TearDown()
        {
            UserSettings.Store = null;
            PlayerPrefs.DeleteKey(Key);
        }

        [Test]
        public void AString_IsTheFallbackUntilItIsSet_AndTheFallbackAgainOnceRemoved()
        {
            Assert.AreEqual("default", UserSettings.GetString(Key, "default"));
            Assert.IsFalse(store.Has(Key));

            UserSettings.SetString(Key, "192.168.1.20");
            Assert.AreEqual("192.168.1.20", UserSettings.GetString(Key, "default"));
            Assert.IsTrue(store.Has(Key));
            Assert.AreEqual(0, store.Saves, "Setting does not write; Save does.");
            UserSettings.Save();
            Assert.AreEqual(1, store.Saves);

            UserSettings.Remove(Key);
            Assert.IsFalse(store.Has(Key));
            Assert.AreEqual("default", UserSettings.GetString(Key, "default"));
        }

        [Test]
        public void Remove_ForgetsANumberToo()
        {
            UserSettings.SetFloat(Key, 0.4f);
            Assert.AreEqual(0.4f, UserSettings.GetFloat(Key, 1f), 1e-6f);
            UserSettings.Remove(Key);
            Assert.AreEqual(1f, UserSettings.GetFloat(Key, 1f), 1e-6f);
            Assert.DoesNotThrow(() => UserSettings.Remove(Key), "Removing what is not there is not an error.");
        }

        [Test]
        public void ThePlayerPrefsStore_KeepsStrings()
        {
            var prefs = new PlayerPrefsUserSettingsStore();
            PlayerPrefs.DeleteKey(Key);
            Assert.IsFalse(prefs.TryGetString(Key, out string none));
            Assert.IsNull(none);

            prefs.SetString(Key, "10.0.0.7");
            Assert.IsTrue(prefs.TryGetString(Key, out string kept));
            Assert.AreEqual("10.0.0.7", kept);

            prefs.Remove(Key);
            Assert.IsFalse(prefs.TryGetString(Key, out _));
        }
    }
}
