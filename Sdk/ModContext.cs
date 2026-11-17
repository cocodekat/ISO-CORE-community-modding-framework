using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace ISOCORE.ModSdk
{
    public interface IMod
    {
        void Load(ModContext mod);
    }

    public sealed class ModContext
    {
        private readonly GameObject _host;

        internal ModContext(GameObject host, ManualLogSource log, ConfigFile config,
            string modId, Assembly assembly)
        {
            _host = host;
            Log = log;
            Config = config;
            Diagnostics = new ModDiagnostics(log);
            Assets = new AssetsApi(assembly, log, modId);
            Content = new ContentApi(modId, log);
            Combat = new CombatApi(modId, host, log);
            Paths = new PathfindingApi();
            World = new WorldApi(Combat, Paths);
#if ISOCORE_INVENTORY
            Inventory = new InventoryApi(log);
#endif
#if ISOCORE_UI
            Ui = new MenuApi(log);
#endif
        }

        public ManualLogSource Log { get; }
        public ConfigFile Config { get; }
        public ModDiagnostics Diagnostics { get; }
        public AssetsApi Assets { get; }
        public ContentApi Content { get; }
        public CombatApi Combat { get; }
        public PathfindingApi Paths { get; }
        public WorldApi World { get; }

#if ISOCORE_INVENTORY
        public InventoryApi Inventory { get; }
#endif
#if ISOCORE_UI
        public MenuApi Ui { get; }
#endif

        public T AddComponent<T>() where T : Component => _host.AddComponent<T>();

        /// <summary>
        /// Starts an SDK-aware component after the first scene load. Services, diagnostics,
        /// persistence, guarded lifecycle callbacks and cleanup are supplied automatically.
        /// </summary>
        public void Start<T>(Action<T> configure = null, string objectName = null)
            where T : ModComponent
        {
            AddPersistentComponentAfterSceneLoad<T>(component =>
            {
                configure?.Invoke(component);
                component.Initialize(this);
            }, objectName);
        }

        /// <summary>
        /// Creates a component after the first game scene has loaded and keeps it alive across
        /// later scene changes. Use this for runtime UI and other components that depend on the
        /// game scene existing.
        /// </summary>
        public void AddPersistentComponentAfterSceneLoad<T>(Action<T> configure = null,
            string objectName = null) where T : Component
        {
            UnityAction<Scene, LoadSceneMode> loaded = null;
            loaded = delegate(Scene scene, LoadSceneMode mode)
            {
                SceneManager.sceneLoaded -= loaded;
                string name = string.IsNullOrWhiteSpace(objectName) ? typeof(T).Name : objectName;
                Log.LogInfo("Scene loaded: " + scene.name + " (" + mode +
                    "); creating persistent " + name + ".");

                var host = new GameObject(name);
                UnityEngine.Object.DontDestroyOnLoad(host);
                try
                {
                    T component = host.AddComponent<T>();
                    configure?.Invoke(component);
                    Log.LogInfo("Persistent " + typeof(T).Name + " attached and initialized.");
                }
                catch (Exception exception)
                {
                    UnityEngine.Object.Destroy(host);
                    Log.LogError("Could not initialize persistent " + typeof(T).Name + ": " +
                        exception);
                }
            };

            Log.LogInfo("Waiting for the first loaded scene before creating " + typeof(T).Name + ".");
            SceneManager.sceneLoaded += loaded;
        }
    }

    public sealed class ModDiagnostics
    {
        private readonly ManualLogSource _log;

        internal ModDiagnostics(ManualLogSource log) => _log = log;

        /// <summary>Controls lifecycle and UI/inventory trace messages. Errors are always logged.</summary>
        public bool Verbose { get; set; } = true;

        internal void Trace(string message)
        {
            if (Verbose) _log?.LogInfo(message);
        }

        internal void Error(string message, Exception exception)
        {
            _log?.LogError(message + ": " + exception);
        }
    }

    /// <summary>
    /// Base class for runtime mod features. Override OnModStart/OnModUpdate/OnModStop instead of
    /// Unity Start/Update/OnDestroy so the SDK can diagnose failures and release subscriptions.
    /// </summary>
    public abstract class ModComponent : MonoBehaviour
    {
        private readonly List<Action> _cleanup = new List<Action>();
        private bool _initialized;
        private bool _updateFailed;

        protected ModContext Mod { get; private set; }
        protected ManualLogSource Log => Mod.Log;
        protected ConfigFile Config => Mod.Config;
        protected AssetsApi Assets => Mod.Assets;
        protected ContentApi Content => Mod.Content;
        protected CombatApi Combat => Mod.Combat;
        protected PathfindingApi Paths => Mod.Paths;
        protected WorldApi World => Mod.World;
#if ISOCORE_INVENTORY
        protected InventoryApi Inventory => Mod.Inventory;
#endif
#if ISOCORE_UI
        protected MenuApi Ui => Mod.Ui;
#endif

        internal void Initialize(ModContext mod)
        {
            if (_initialized) return;
            _initialized = true;
            Mod = mod;
            Mod.Diagnostics.Trace(GetType().Name + " starting.");
            try
            {
                OnModStart();
                Mod.Diagnostics.Trace(GetType().Name + " started.");
            }
            catch (Exception exception)
            {
                Mod.Diagnostics.Error(GetType().Name + " failed during startup", exception);
            }
        }

        protected virtual void OnModStart() { }
        protected virtual void OnModUpdate() { }
        protected virtual void OnModStop() { }

        /// <summary>Registers cleanup that runs automatically in reverse order.</summary>
        protected void Cleanup(Action action)
        {
            if (action != null) _cleanup.Add(action);
        }

        /// <summary>Wraps a callback in automatic exception diagnostics.</summary>
        protected Action Guard(Action action, string name = null)
        {
            return delegate
            {
                try { action?.Invoke(); }
                catch (Exception exception)
                {
                    Mod.Diagnostics.Error(GetType().Name + " callback " +
                        (name ?? action?.Method.Name ?? "<anonymous>") + " failed", exception);
                }
            };
        }

#if ISOCORE_UI
        protected UnityAction GuardUi(Action action, string name = null)
        {
            Action guarded = Guard(action, name);
            return () => guarded();
        }
#endif

#if ISOCORE_INVENTORY
        protected void WhenInventoryOpened(Action action)
        {
            Action guarded = Guard(action, "inventory opened");
            Inventory.Opened += guarded;
            Cleanup(() => Inventory.Opened -= guarded);
        }

        protected void WhenInventoryClosed(Action action)
        {
            Action guarded = Guard(action, "inventory closed");
            Inventory.Closed += guarded;
            Cleanup(() => Inventory.Closed -= guarded);
        }

        protected void WhenSlotClicked(EventHandler<InventorySlotClickEventArgs> action)
        {
            EventHandler<InventorySlotClickEventArgs> guarded = delegate(object sender,
                InventorySlotClickEventArgs args)
            {
                try { action?.Invoke(sender, args); }
                catch (Exception exception)
                {
                    Mod.Diagnostics.Error(GetType().Name + " slot-click callback failed", exception);
                }
            };
            Inventory.SlotClicked += guarded;
            Cleanup(() => Inventory.SlotClicked -= guarded);
        }
#endif

        protected void Update()
        {
            if (!_initialized || _updateFailed) return;
            try { OnModUpdate(); }
            catch (Exception exception)
            {
                _updateFailed = true;
                Mod.Diagnostics.Error(GetType().Name + " update failed; further updates disabled",
                    exception);
            }
        }

        protected void OnDestroy()
        {
            if (!_initialized) return;
            try { OnModStop(); }
            catch (Exception exception)
            {
                Mod.Diagnostics.Error(GetType().Name + " failed during shutdown", exception);
            }
            for (int index = _cleanup.Count - 1; index >= 0; index--)
            {
                try { _cleanup[index](); }
                catch (Exception exception)
                {
                    Mod.Diagnostics.Error(GetType().Name + " cleanup failed", exception);
                }
            }
            _cleanup.Clear();
            Mod.Diagnostics.Trace(GetType().Name + " stopped.");
        }
    }
}
