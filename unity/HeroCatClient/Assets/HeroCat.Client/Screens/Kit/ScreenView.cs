using System.Collections.Generic;
using HeroCat.Client.Core;
using UnityEngine;

namespace HeroCat.Client.Screens
{
    /// <summary>
    /// 07 section 3's <c>ScreenBase</c>: a thin view over its vm. <see cref="Build"/> creates the placeholder hierarchy and
    /// binds every change-only field once; after that the vm writes the labels itself, so <see cref="Tick"/> only handles
    /// what is per frame (lists that rebuild on a version change, animations). No screen reads GameState or holds the
    /// Simulation (07 section 17.1). Unity-only: not compiled in cloud sessions.
    /// </summary>
    public abstract class ScreenView : MonoBehaviour
    {
        protected GameViewModel Vm;
        protected UiRoot Ui;

        public RectTransform Root => (RectTransform)transform;

        public void Init(GameViewModel vm, UiRoot ui)
        {
            Vm = vm;
            Ui = ui;
            Build();
        }

        protected abstract void Build();

        /// <summary>The router showed this screen (modal args are in <paramref name="e"/>).</summary>
        public virtual void OnShown(ScreenEntry e) { }

        public virtual void OnHidden() { }

        /// <summary>Every frame while visible.</summary>
        public virtual void Tick(double now) { }

        /// <summary>Registers a reject-toast anchor (the pressed button) under <paramref name="id"/>.</summary>
        protected void Anchor(string id, RectTransform rt) => Ui.Anchors[id] = rt;
    }

    /// <summary>
    /// Tiny tween runner for placeholder feedback (pulse, shake, pop, float). No coroutines and no allocation per frame
    /// once the list has grown; Reduced motion skips movement (07 section 11).
    /// </summary>
    public sealed class Tweens
    {
        enum Kind { Pulse, Shake, Pop, Float }

        struct T
        {
            public Kind Kind;
            public RectTransform Rt;
            public Vector2 Origin;
            public Vector3 Scale;
            public double Start, Duration;
            public float Amount;
        }

        readonly List<T> _list = new List<T>(32);

        public bool ReducedMotion { get; set; }

        public void Pulse(RectTransform rt, double now, float amount = 0.15f, double sec = 0.25) => Add(Kind.Pulse, rt, now, sec, amount);

        /// <summary>07 section 5's invalid drop: 3 x 6 rp over 180 ms.</summary>
        public void Shake(RectTransform rt, double now, float rp = 6, double sec = 0.18) => Add(Kind.Shake, rt, now, sec, rp);

        /// <summary>Scale 1.3 -&gt; 1.0 over 200 ms (merge pop).</summary>
        public void Pop(RectTransform rt, double now) => Add(Kind.Pop, rt, now, 0.2, 0.3f);

        public void Float(RectTransform rt, double now, float rp = 80, double sec = UiConstants.DamageNumberLifeSec) => Add(Kind.Float, rt, now, sec, rp);

        void Add(Kind k, RectTransform rt, double now, double sec, float amount)
        {
            if (rt == null) return;
            for (int i = 0; i < _list.Count; i++)
                if (_list[i].Rt == rt && _list[i].Kind == k) { Restore(_list[i]); _list.RemoveAt(i); break; }
            _list.Add(new T { Kind = k, Rt = rt, Origin = rt.anchoredPosition, Scale = rt.localScale, Start = now, Duration = sec, Amount = amount });
        }

        public void Update(double now)
        {
            for (int i = _list.Count - 1; i >= 0; i--)
            {
                var t = _list[i];
                if (t.Rt == null) { _list.RemoveAt(i); continue; }
                float p = (float)((now - t.Start) / t.Duration);
                if (p >= 1f)
                {
                    Restore(t);
                    _list.RemoveAt(i);
                    continue;
                }
                switch (t.Kind)
                {
                    case Kind.Pulse:
                        t.Rt.localScale = t.Scale * (1f + t.Amount * Mathf.Sin(p * Mathf.PI));
                        break;
                    case Kind.Pop:
                        t.Rt.localScale = t.Scale * (1f + t.Amount * (1f - p));
                        break;
                    case Kind.Shake:
                        if (!ReducedMotion) t.Rt.anchoredPosition = t.Origin + new Vector2(t.Amount * Mathf.Sin(p * Mathf.PI * 6f), 0);
                        break;
                    case Kind.Float:
                        if (!ReducedMotion) t.Rt.anchoredPosition = t.Origin + new Vector2(0, t.Amount * p);
                        break;
                }
            }
        }

        static void Restore(T t)
        {
            if (t.Rt == null) return;
            if (t.Kind == Kind.Shake || t.Kind == Kind.Float) t.Rt.anchoredPosition = t.Origin;
            else t.Rt.localScale = t.Scale;
        }
    }
}
