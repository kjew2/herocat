// Minimal UnityEngine / uGUI / TMP / Input System stubs (tools/unity_typecheck): only to type-check the client's Unity-only code in a cloud
// session. Members are the ones the client uses; bodies do nothing.
#pragma warning disable
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public class Object
    {
        public string name { get; set; }
        public static void Destroy(Object o) { }
        public static void DontDestroyOnLoad(Object o) { }
        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b);
        public static bool operator !=(Object a, Object b) => !ReferenceEquals(a, b);
        public static implicit operator bool(Object o) => o != null;
        public override bool Equals(object o) => base.Equals(o);
        public override int GetHashCode() => 0;
        public static T Instantiate<T>(T o) where T : Object => o;
    }
    public class Component : Object
    {
        public GameObject gameObject => null;
        public Transform transform => null;
        public string tag { get; set; }
        public T GetComponent<T>() => default;
        public T GetComponentInChildren<T>(bool includeInactive = false) => default;
    }
    public class Behaviour : Component { public bool enabled { get; set; } }
    public class MonoBehaviour : Behaviour { }
    public sealed class GameObject : Object
    {
        public GameObject(string name) { }
        public GameObject(string name, params Type[] components) { }
        public Transform transform => null;
        public int layer { get; set; }
        public bool activeSelf => true;
        public bool activeInHierarchy => true;
        public void SetActive(bool v) { }
        public T AddComponent<T>() where T : Component => default;
        public T GetComponent<T>() => default;
    }
    public class Transform : Component
    {
        public void SetParent(Transform p, bool worldPositionStays) { }
        public void SetAsLastSibling() { }
        public void SetAsFirstSibling() { }
        public int childCount => 0;
        public Transform GetChild(int i) => null;
        public Vector3 localScale { get; set; }
        public Vector3 InverseTransformPoint(Vector3 p) => p;
    }
    public sealed class RectTransform : Transform
    {
        public Vector2 anchorMin { get; set; }
        public Vector2 anchorMax { get; set; }
        public Vector2 pivot { get; set; }
        public Vector2 anchoredPosition { get; set; }
        public Vector2 sizeDelta { get; set; }
        public Vector2 offsetMin { get; set; }
        public Vector2 offsetMax { get; set; }
        public Rect rect => default;
        public void GetWorldCorners(Vector3[] c) { }
    }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => default;
        public static Vector2 one => new Vector2(1, 1);
        public static Vector2 operator +(Vector2 a, Vector2 b) => a;
        public static bool operator ==(Vector2 a, Vector2 b) => true;
        public static bool operator !=(Vector2 a, Vector2 b) => false;
        public override bool Equals(object o) => true;
        public override int GetHashCode() => 0;
    }
    public struct Vector2Int
    {
        public int x, y;
        public Vector2Int(int x, int y) { this.x = x; this.y = y; }
        public static bool operator ==(Vector2Int a, Vector2Int b) => true;
        public static bool operator !=(Vector2Int a, Vector2Int b) => false;
        public override bool Equals(object o) => true;
        public override int GetHashCode() => 0;
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 operator *(Vector3 a, float f) => a;
        public static implicit operator Vector2(Vector3 v) => default;
        public static implicit operator Vector3(Vector2 v) => default;
    }
    public struct Rect
    {
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
        public float x, y, width, height;
        public float xMin => x;
        public float yMax => y + height;
        public static bool operator ==(Rect a, Rect b) => true;
        public static bool operator !=(Rect a, Rect b) => false;
        public override bool Equals(object o) => true;
        public override int GetHashCode() => 0;
    }
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a = 1) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => default;
        public static Color Lerp(Color a, Color b, float t) => a;
        public static Color HSVToRGB(float h, float s, float v) => default;
        public static Color operator *(Color c, float f) => c;
    }
    public struct Color32 { public Color32(byte r, byte g, byte b, byte a) { } }
    public static class Mathf
    {
        public const float PI = 3.14159f;
        public static float Clamp01(float v) => v;
        public static float Sin(float v) => v;
        public static float Sqrt(float v) => v;
        public static float Min(float a, float b) => a;
        public static float Clamp(float v, float a, float b) => v;
    }
    public static class Screen
    {
        public static Rect safeArea => default;
        public static int width => 0;
        public static int height => 0;
        public static int sleepTimeout { get; set; }
    }
    public static class SleepTimeout { public const int NeverSleep = -1; public const int SystemSetting = -2; }
    public static class QualitySettings { public static int vSyncCount { get; set; } }
    public static class Time { public static float unscaledDeltaTime => 0; public static float unscaledTime => 0; }
    public static class Application
    {
        public static int targetFrameRate { get; set; }
        public static bool runInBackground { get; set; }
        public static string persistentDataPath => "";
        public static string dataPath => "";
        public static string streamingAssetsPath => "";
        public static string version => "";
        public static string identifier => "";
        public static string buildGUID => "";
        public static NetworkReachability internetReachability => NetworkReachability.NotReachable;
        public static void Quit() { }
        public static void OpenURL(string u) { }
        public static event Action lowMemory;
    }
    public enum NetworkReachability { NotReachable, ReachableViaCarrierDataNetwork, ReachableViaLocalAreaNetwork }
    public static class Debug
    {
        public static void Log(object o) { }
        public static void LogWarning(object o) { }
        public static void LogError(object o) { }
        public static void LogException(Exception e) { }
    }
    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => default;
        public static T GetBuiltinResource<T>(string path) where T : Object => default;
    }
    public class TextAsset : Object { public string text => ""; public byte[] bytes => null; }
    public class Font : Object { }
    public enum TextureFormat { RGBA32 }
    public enum TextureWrapMode { Clamp }
    public enum FilterMode { Point, Bilinear }
    public class Texture2D : Object
    {
        public Texture2D(int w, int h, TextureFormat f, bool mip) { }
        public TextureWrapMode wrapMode { get; set; }
        public FilterMode filterMode { get; set; }
        public void SetPixels32(Color32[] p) { }
        public void Apply(bool a, bool b) { }
    }
    public class Sprite : Object { public static Sprite Create(Texture2D t, Rect r, Vector2 p, float ppu) => null; public Rect rect => default; }
    public enum CameraClearFlags { SolidColor }
    public class Camera : Behaviour
    {
        public static Camera main => null;
        public CameraClearFlags clearFlags { get; set; }
        public Color backgroundColor { get; set; }
        public bool orthographic { get; set; }
    }
    public enum RenderMode { ScreenSpaceOverlay }
    public class Canvas : Behaviour
    {
        public RenderMode renderMode { get; set; }
        public int sortingOrder { get; set; }
        public bool overrideSorting { get; set; }
    }
    public sealed class CanvasGroup : Behaviour { public float alpha { get; set; } public bool blocksRaycasts { get; set; } }
    public static class Handheld { public static void Vibrate() { } }
    public enum KeyCode { Escape }
    public static class Input { public static bool GetKeyDown(KeyCode k) => false; }
    public enum RuntimeInitializeLoadType { AfterSceneLoad }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) { } }
    public static class RectTransformUtility
    {
        public static bool ScreenPointToLocalPointInRectangle(RectTransform rt, Vector2 p, Camera c, out Vector2 local) { local = default; return true; }
    }
}

namespace UnityEngine.Events
{
    public delegate void UnityAction();
    public delegate void UnityAction<T>(T a);
    public class UnityEvent { public void AddListener(UnityAction a) { } }
    public class UnityEvent<T> { public void AddListener(UnityAction<T> a) { } }
}

namespace UnityEngine.EventSystems
{
    public class EventSystem : UnityEngine.Behaviour { public static EventSystem current => null; }
    public class StandaloneInputModule : UnityEngine.Behaviour { }
    public class PointerEventData
    {
        public int pointerId;
        public UnityEngine.Vector2 position;
        public UnityEngine.Camera pressEventCamera;
        public bool useDragThreshold;
    }
    public interface IPointerDownHandler { void OnPointerDown(PointerEventData e); }
    public interface IPointerUpHandler { void OnPointerUp(PointerEventData e); }
    public interface IInitializePotentialDragHandler { void OnInitializePotentialDrag(PointerEventData e); }
    public interface IDragHandler { void OnDrag(PointerEventData e); }
}

namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.Behaviour
    {
        public UnityEngine.Color color { get; set; }
        public bool raycastTarget { get; set; }
        public UnityEngine.RectTransform rectTransform => null;
    }
    public class Image : Graphic { public UnityEngine.Sprite sprite { get; set; } public bool preserveAspect { get; set; } }
    public class Selectable : UnityEngine.Behaviour { public bool interactable { get; set; } public Graphic targetGraphic { get; set; } }
    public class Button : Selectable { public UnityEngine.Events.UnityEvent onClick { get; } = new UnityEngine.Events.UnityEvent(); }
    public class CanvasScaler : UnityEngine.Behaviour
    {
        public enum ScaleMode { ScaleWithScreenSize }
        public ScaleMode uiScaleMode { get; set; }
        public UnityEngine.Vector2 referenceResolution { get; set; }
        public float matchWidthOrHeight { get; set; }
    }
    public class GraphicRaycaster : UnityEngine.Behaviour { }
    public class ScrollRect : UnityEngine.Behaviour
    {
        public bool horizontal { get; set; }
        public UnityEngine.RectTransform content { get; set; }
        public UnityEngine.RectTransform viewport { get; set; }
    }
    public class RectMask2D : UnityEngine.Behaviour { }
    public class Outline : UnityEngine.Behaviour
    {
        public UnityEngine.Vector2 effectDistance { get; set; }
        public UnityEngine.Color effectColor { get; set; }
    }
}

namespace TMPro
{
    public enum TextAlignmentOptions { MidlineLeft, MidlineRight, Center, Bottom, BottomRight }
    public enum TextOverflowModes { Ellipsis }
    [Flags] public enum FontStyles { Normal = 0, Bold = 1 }
    public class TMP_FontAsset : UnityEngine.Object
    {
        public static TMP_FontAsset CreateFontAsset(UnityEngine.Font f) => null;
    }
    public static class TMP_Settings { public static TMP_FontAsset defaultFontAsset => null; }
    public class TMP_Text : UnityEngine.UI.Graphic
    {
        public string text { get; set; }
        public TMP_FontAsset font { get; set; }
        public float fontSize { get; set; }
        public TextAlignmentOptions alignment { get; set; }
        public TextOverflowModes overflowMode { get; set; }
        public FontStyles fontStyle { get; set; }
    }
    public class TextMeshProUGUI : TMP_Text { }
    public class TMP_InputField : UnityEngine.UI.Selectable
    {
        public string text { get; set; }
        public UnityEngine.RectTransform textViewport { get; set; }
        public TMP_Text textComponent { get; set; }
        public UnityEngine.Events.UnityEvent<string> onValueChanged { get; } = new UnityEngine.Events.UnityEvent<string>();
    }
}

namespace UnityEngine.InputSystem
{
    public class KeyControl { public bool wasPressedThisFrame => false; }
    public class Keyboard { public static Keyboard current => null; public KeyControl escapeKey => null; }
}

namespace UnityEngine.InputSystem.UI
{
    public class InputSystemUIInputModule : UnityEngine.Behaviour { public void AssignDefaultActions() { } }
}

// UnityEditor: only what Editor/ContentPreBuild.cs uses (the other Editor scripts are not type-checked).
namespace UnityEditor
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class MenuItem : System.Attribute { public MenuItem(string itemName) { } }
    [System.Flags] public enum ImportAssetOptions { Default = 0, ForceUpdate = 1, ForceSynchronousImport = 8 }
    public static class AssetDatabase { public static void Refresh(ImportAssetOptions options = ImportAssetOptions.Default) { } }
    public class AssetImporter : UnityEngine.Object { }
    public enum TextureImporterType { Default, Sprite }
    public enum SpriteImportMode { None, Single, Multiple }
    public enum TextureImporterCompression { Uncompressed, Compressed }
    public class TextureImporter : AssetImporter
    {
        public TextureImporterType textureType { get; set; }
        public SpriteImportMode spriteImportMode { get; set; }
        public bool mipmapEnabled { get; set; }
        public bool alphaIsTransparency { get; set; }
        public UnityEngine.FilterMode filterMode { get; set; }
        public TextureImporterCompression textureCompression { get; set; }
    }
    public class AssetPostprocessor
    {
        public string assetPath => "";
        public AssetImporter assetImporter => null;
    }
}

namespace UnityEditor.Build.Reporting
{
    public sealed class BuildReport { }
}

namespace UnityEditor.Build
{
    public class BuildFailedException : System.Exception { public BuildFailedException(string message) : base(message) { } }
    public interface IOrderedCallback { int callbackOrder { get; } }
    public interface IPreprocessBuildWithReport : IOrderedCallback { void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report); }
}
