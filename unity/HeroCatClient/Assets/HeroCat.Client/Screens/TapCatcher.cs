using HeroCat.Client.Core;
using HeroCat.Sim.Content;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>The full-viewport tap target behind the combat UI (07 section 16 Input).</summary>
    public sealed class TapCatcher : MonoBehaviour, IPointerDownHandler
    {
        MainScreen _main;
        public void Init(MainScreen main) => _main = main;
        public void OnPointerDown(PointerEventData e) => _main?.OnViewportTap();
    }
}
