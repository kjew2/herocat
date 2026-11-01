using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HeroCat.Client.Core;
using HeroCat.Sim.Commands;
using HeroCat.Sim.State;

namespace HeroCat.Runtime.Tests
{
    /// <summary>A booted session with the client's GameViewModel on top, driven by fake clocks.</summary>
    internal sealed class ClientFixture
    {
        public TestSession S;
        public GameViewModel Vm;
        public MemoryClientPrefs Prefs = new MemoryClientPrefs();

        public double Now => S.Mono.Ticks / (double)S.Mono.Frequency;

        public static ClientFixture Open(Action<GameState> setup = null, GameVmOptions options = null)
        {
            var f = new ClientFixture { S = TestSession.Open() };
            setup?.Invoke(f.S.Sim.State);
            f.S.Sim.Query.MarkDirty();
            f.S.Sim.Advance(0);   // re-takes the snapshot after the scripted state
            options = options ?? new GameVmOptions();
            options.Prefs = options.Prefs ?? f.Prefs;
            options.Now = options.Now ?? (() => f.S.Mono.Ticks / (double)f.S.Mono.Frequency);
            f.Vm = new GameViewModel(f.S.Runner, null, options);
            f.Vm.Refresh();
            return f;
        }

        /// <summary>One rendered frame of <paramref name="seconds"/>, as ClientSession.Frame runs it.</summary>
        public int Frame(double seconds = 1.0 / 60)
        {
            int ticks = S.Frame(seconds);
            Vm.Frame();
            Vm.Refresh();
            return ticks;
        }

        public void Frames(int n, double seconds = 1.0 / 60)
        {
            for (int i = 0; i < n; i++) Frame(seconds);
        }

        public int Logged<T>() where T : Command => S.Log.Records.OfType<T>().Count();

        public List<T> LoggedOf<T>() where T : Command => S.Log.Records.OfType<T>().ToList();
    }

    internal sealed class CountingSink : ITextSink, IFillSink, IFlagSink
    {
        public int Writes;
        public string LastText;
        public void SetText(string text) { Writes++; LastText = text; }
        public void SetFill(float fill01) { Writes++; }
        public void SetFlag(bool on) { Writes++; }
    }

    internal sealed class CountingValueSink<T> : IValueSink<T>
    {
        public int Writes;
        public T Last;
        public void SetValue(T value) { Writes++; Last = value; }
    }

    /// <summary>Binds a counting sink to every change-only field of a vm (reflection is test-only).</summary>
    internal static class SinkBinder
    {
        public static int BindAll(object vm, List<Func<int>> counters, int depth = 0)
        {
            if (vm == null || depth > 3) return 0;
            int n = 0;
            foreach (var f in vm.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var v = f.GetValue(vm);
                n += BindValue(v, counters, depth);
            }
            return n;
        }

        static int BindValue(object v, List<Func<int>> counters, int depth)
        {
            switch (v)
            {
                case null: return 0;
                case NumberLabel l: { var s = new CountingSink(); l.Bind(s); counters.Add(() => s.Writes); return 1; }
                case IntLabel l: { var s = new CountingSink(); l.Bind(s); counters.Add(() => s.Writes); return 1; }
                case TextKeyLabel l: { var s = new CountingSink(); l.Bind(s); counters.Add(() => s.Writes); return 1; }
                case BarFill l: { var s = new CountingSink(); l.Bind(s); counters.Add(() => s.Writes); return 1; }
                case Flag l: { var s = new CountingSink(); l.Bind(s); counters.Add(() => s.Writes); return 1; }
                case BadgeDot l: { var s = new CountingSink(); l.Bind(s, s); counters.Add(() => s.Writes); return 1; }
                case ButtonState b: return BindAll(b, counters, depth + 1);
                case Array a:
                {
                    int n = 0;
                    foreach (var x in a) n += BindValue(x, counters, depth);
                    return n;
                }
            }
            var t = v.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Bindable<>))
            {
                var sinkType = typeof(CountingValueSink<>).MakeGenericType(t.GetGenericArguments()[0]);
                var sink = Activator.CreateInstance(sinkType);
                t.GetMethod("Bind").Invoke(v, new[] { sink });
                var field = sinkType.GetField("Writes");
                counters.Add(() => (int)field.GetValue(sink));
                return 1;
            }
            return 0;
        }

        public static int Total(List<Func<int>> counters) => counters.Sum(c => c());
    }
}
