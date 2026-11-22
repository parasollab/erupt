using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Robotics.ROSTCPConnector;
using Unity.Robotics.ROSTCPConnector.MessageGeneration;
using RosMessageTypes.Tf2;

namespace Erupt.Ros
{
    /// <summary>Pass-through to the real ROSConnection. The default in every build.</summary>
    /// <remarks>
    /// One connection serves the app for as long as it runs. The connector creates its
    /// <c>ROSConnection</c> in whatever scene is active and never closes its socket when that
    /// object is destroyed, so a scene change (the reference app reloads its scene to reset
    /// it) used to leave the old connection's thread and socket running beside a new one, and
    /// everything the outgoing scene said on its way out (the REMOVE of each obstacle) was lost
    /// or raised an error per object. The bus therefore keeps the connection across scene loads
    /// (<c>DontDestroyOnLoad</c>); each scene's components subscribe when they start and
    /// unsubscribe when they are destroyed.
    /// </remarks>
    public sealed class LiveRosBus : IRosBus
    {
        private ROSConnection connection;
        private static bool quitting;

        // Static state survives entering play mode when domain reload is disabled.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlay()
        {
            quitting = false;
            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;
        }

        private static void OnQuitting() => quitting = true;

        /// <summary>
        /// The connection, created on first use. Lazy, matching the pre-seam behaviour where
        /// each component called GetOrCreateInstance() from its own Start().
        /// </summary>
        /// <remarks>
        /// The null check must be Unity's, not <c>??=</c>. ROSConnection is a
        /// MonoBehaviour, so once it is destroyed the reference is "fake null": Unity's
        /// overloaded == reports null while ?? and ??= see a live object and refuse to
        /// refresh it. With ??= a cached connection from a previous play session survives
        /// into the next one and every Subscribe/Publish lands on a destroyed object,
        /// silently, which is exactly what stopped the collision object listener working.
        /// Null while the application is quitting and the connection is already gone: objects
        /// are destroyed in no particular order then, and a component saying goodbye must not
        /// bring a new connection to life.
        /// </remarks>
        private ROSConnection Connection
        {
            get
            {
                if (connection == null)
                {
                    if (quitting) return null;
                    connection = ROSConnection.GetOrCreateInstance();
                    Adopt(connection);
                }
                return connection;
            }
        }

        // The connection when there is one. Never creates: reading the state of the link, taking
        // a callback back and publishing (which needs a publisher registered on that very
        // connection) have no use for a new one.
        private ROSConnection Existing => connection != null ? connection : null;

        private void Adopt(ROSConnection adopted)
        {
            // ERUPT drives its robots from joint states and never reads TF, so the connection
            // need not listen to it on its own account (a GameObject per TF frame, updated with
            // every message). Only effective before the connection's Start, which is when the
            // bus creates it; a tool that wants TF asks the connector's TFSystem itself.
            adopted.listenForTFMessages = false;
            registeredPublishers.Clear();
            registeredServices.Clear();
            if (!Application.isPlaying) return;
            if (adopted.transform.parent == null) UnityEngine.Object.DontDestroyOnLoad(adopted.gameObject);
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        // Publisher topics registered on the current connection. The connector warns when a
        // topic is registered again, which every scene after the first would do.
        private readonly HashSet<string> registeredPublishers = new HashSet<string>();
        private readonly HashSet<string> registeredServices = new HashSet<string>();

        private void OnSceneUnloaded(Scene scene)
        {
            ROSConnection c = Existing;
            if (c == null)
            {
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                return;
            }
            DropStaleTfListener(c);
        }

        // The connector's TF listener (TFSystem, a static) keeps a GameObject per TF frame,
        // created in whatever scene was active. When that scene is unloaded the objects go, the
        // listener stays subscribed on the connection that outlives the scene, and every TF
        // message after that throws. ERUPT does not use TF, but a scene may carry something
        // that does (the connector's visualization suite): the dead listener is dropped here,
        // and the next one to ask for TF gets a fresh one.
        private static void DropStaleTfListener(ROSConnection c)
        {
            TFSystem tf = TFSystem.instance;
            if (tf == null) return;
            string[] topics = c.TFTopics ?? Array.Empty<string>();
            bool stale = false;
            foreach (string topic in topics)
            {
                foreach (TFStream stream in tf.GetTransforms(topic))
                    if (stream != null && stream.GameObject == null) { stale = true; break; }
                if (stale) break;
            }
            if (!stale) return;
            // Only the listener's own callback is taken back; the endpoint keeps the
            // subscription (not every endpoint knows how to drop one) for the next listener.
            foreach (string topic in topics)
                c.Unsubscribe<TFMessageMsg>(topic, tf.GetOrCreateTFTopic(topic).ReceiveTF);
            // TFSystem.instance has a private setter and the connector offers no reset.
            typeof(TFSystem).GetProperty(nameof(TFSystem.instance), BindingFlags.Public | BindingFlags.Static)
                ?.GetSetMethod(true)?.Invoke(null, new object[] { null });
        }

        public bool HasConnectionThread
        {
            get
            {
                ROSConnection c = Existing;
                return c != null && c.HasConnectionThread;
            }
        }

        public bool HasConnectionError
        {
            get
            {
                ROSConnection c = Existing;
                return c == null || c.HasConnectionError;
            }
        }

        public void Subscribe<T>(string topic, Action<T> callback) where T : Message =>
            Connection?.Subscribe(topic, callback);

        public void Unsubscribe<T>(string topic, Action<T> callback) where T : Message =>
            Existing?.Unsubscribe(topic, callback);

        public void RegisterPublisher<T>(string topic) where T : Message
        {
            ROSConnection c = Connection;
            if (c != null && registeredPublishers.Add(topic)) c.RegisterPublisher<T>(topic);
        }

        public void RegisterPublisher<T>(string topic, int queueSize) where T : Message
        {
            ROSConnection c = Connection;
            if (c != null && registeredPublishers.Add(topic)) c.RegisterPublisher<T>(topic, queueSize);
        }

        public void RegisterRosService<TRequest, TResponse>(string serviceName)
            where TRequest : Message where TResponse : Message
        {
            // Once per connection: every registration makes the endpoint replace its client for
            // the service, and a request that arrives while it does comes back empty.
            ROSConnection c = Connection;
            if (c != null && registeredServices.Add(serviceName)) c.RegisterRosService<TRequest, TResponse>(serviceName);
        }

        public void SendServiceMessage<TResponse>(string serviceName, Message request, Action<TResponse> callback)
            where TResponse : Message, new() =>
            Connection?.SendServiceMessage(serviceName, request, callback);

        public IRosActionClient<TGoal, TResult, TFeedback>
            RegisterActionClient<TGoal, TResult, TFeedback>(string actionName)
            where TGoal : Message where TResult : Message where TFeedback : Message =>
            Connection?.RegisterActionClient<TGoal, TResult, TFeedback>(actionName);

        public void Publish(string topic, Message message)
        {
            ROSConnection c = Existing;
            if (c != null && IsAnnounced(c, topic)) c.Publish(topic, message);
        }

        public void Publish(string topic, Message message, bool useTrailingPad)
        {
            ROSConnection c = Existing;
            if (c != null && IsAnnounced(c, topic)) c.Publish(topic, message, useTrailingPad);
        }

        // A publisher registered before the connection thread exists is announced to the
        // endpoint only when the connection is up, a frame or more after the socket opens; the
        // connector sends whatever was published before that first, and the endpoint throws it
        // away with an error per message ("Not registered to publish topic"). Such a message
        // is not sent at all. Senders that must not lose anything wait for the connection
        // (HasConnectionError) and say it again, as the MoveIt plugin does for the planning scenes.
        private static bool IsAnnounced(ROSConnection c, string topic)
        {
            RosTopicState state = c.GetTopic(topic);
            return state == null || !state.IsPublisher || state.SentPublisherRegistration;
        }
    }
}
