using System.Collections.Generic;
using HeroCat.Client.Core;
using UnityEngine;
using UnityEngine.UI;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// Loads art by content address from <c>Resources/Art/&lt;Kind&gt;/</c> (<see cref="ArtAddresses"/>; 07 sections 15.2-15.3,
    /// issue #291) until the Addressables catalog exists. Misses are cached too, so a screen without art asks once and keeps
    /// its code-built placeholder (07 section 15.1).
    /// </summary>
    public static class SpriteLibrary
    {
        static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();

        /// <summary>The sprite for <paramref name="address"/>, or null when the build has no file for it.</summary>
        public static Sprite Get(string address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            if (Cache.TryGetValue(address, out var s)) return s;
            string path = ArtAddresses.ResourcePath(address);
            s = path != null ? Resources.Load<Sprite>(path) : null;
            Cache[address] = s;
            return s;
        }

        /// <summary>The first of <paramref name="addresses"/> that has a file, or null.</summary>
        public static Sprite First(params string[] addresses)
        {
            foreach (var a in addresses)
            {
                var s = Get(a);
                if (s != null) return s;
            }
            return null;
        }

        /// <summary>
        /// Adds a child Image showing <paramref name="sprite"/> over a placeholder, aspect preserved. Returns null (and adds
        /// nothing) when there is no sprite, so the caller keeps its placeholder.
        /// </summary>
        public static Image Overlay(string name, Transform parent, Sprite sprite, float inset = 0f)
        {
            if (sprite == null) return null;
            var rt = UiKit.Rect(name, parent);
            UiKit.Fill(rt, inset, inset, inset, inset);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.preserveAspect = true;
            img.raycastTarget = false;
            return img;
        }
    }
}
