using UnityEngine;
using UnityEngine.EventSystems;

namespace HeroCat.Client.Boot
{
    /// <summary>
    /// UI Toolkit routes pointer input through an EventSystem when one exists. Create one with the input module that
    /// matches Player Settings > Active Input Handling, so taps work with the new Input System, the old Input Manager
    /// or "Both".
    /// </summary>
    public static class InputSetup
    {
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            var go = new GameObject("EventSystem");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<EventSystem>();
#if ENABLE_INPUT_SYSTEM
            var module = go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            module.AssignDefaultActions();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}
