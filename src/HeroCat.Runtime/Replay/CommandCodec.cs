using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HeroCat.Sim.Commands;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace HeroCat.Runtime.Replay
{
    /// <summary>
    /// The JSONL command-log line format (01 section 2.6, 00 contract 12; issue #67): one JSON object per command,
    /// <c>{"tick":N,"type":"VerbNoun",...fields}</c>. <c>tick</c> is the command's IssuedAtTick; <c>type</c> is a stable
    /// tag; the fields are the command's constructor parameters in camelCase, enums as their names, doubles round-trip
    /// exact. The same format serves the runtime log, the golden command log and the server re-simulator.
    ///
    /// Type tags: the tag is the canonical VerbNoun command name of 00 section 5.1, registered explicitly in
    /// <see cref="Tags"/> (never the CLR type name, so a namespace move or rename cannot change a log). A Command type
    /// defined by another issue and not yet listed is registered by its type name on first use, which is the same
    /// VerbNoun name; add it to <see cref="Tags"/> to pin it.
    /// </summary>
    public static class CommandCodec
    {
        /// <summary>Stable tag per command type. Append a row for every new command (00 section 5.1 names).</summary>
        static readonly (Type type, string tag)[] Tags =
        {
            (typeof(ApplyServerTime), "ApplyServerTime"),
            (typeof(ComputeOfflineCatchup), "ComputeOfflineCatchup"),
            (typeof(ApplySummonResult), "ApplySummonResult"),
            (typeof(ApplyInboxGrant), "ApplyInboxGrant"),
            (typeof(ClaimOfflineReward), "ClaimOfflineReward"),
            // The commands the bots issue (greedy-f2p, 10 section 3.4), pinned so the golden command log does not depend
            // on CLR type names.
            (typeof(TrainStat), "TrainStat"),
            (typeof(ReleaseMax), "ReleaseMax"),
            (typeof(MergeAll), "MergeAll"),
            (typeof(MergeFish), "MergeFish"),
            (typeof(DiscardFish), "DiscardFish"),
            (typeof(AttemptBoss), "AttemptBoss"),
            (typeof(AutoEquipBestWeapon), "AutoEquipBestWeapon"),
            (typeof(AutoEquipBestSkills), "AutoEquipBestSkills"),
        };

        static readonly object Lock = new object();
        static readonly Dictionary<Type, string> TagOf = new Dictionary<Type, string>();
        static readonly Dictionary<string, Type> TypeOf = new Dictionary<string, Type>(StringComparer.Ordinal);

        static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            Converters = { new StringEnumConverter() },
            FloatFormatHandling = FloatFormatHandling.String,
            FloatParseHandling = FloatParseHandling.Double,
            DateParseHandling = DateParseHandling.None,
            NullValueHandling = NullValueHandling.Include,
            Culture = CultureInfo.InvariantCulture,
            MissingMemberHandling = MissingMemberHandling.Error,
        });

        static CommandCodec()
        {
            foreach (var (t, tag) in Tags) Register(t, tag);
        }

        /// <summary>The explicitly registered command types.</summary>
        public static IReadOnlyList<Type> RegisteredTypes => Tags.Select(x => x.type).ToArray();

        /// <summary>Every concrete Command type in the sim assembly.</summary>
        public static IReadOnlyList<Type> SimCommandTypes =>
            typeof(Command).Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(Command).IsAssignableFrom(t) && t.IsPublic).ToArray();

        public static string TagFor(Type t)
        {
            lock (Lock)
            {
                if (TagOf.TryGetValue(t, out var tag)) return tag;
                if (!typeof(Command).IsAssignableFrom(t) || t.IsAbstract) throw new ArgumentException(t + " is not a concrete Command");
                Register(t, t.Name);
                return t.Name;
            }
        }

        public static Type TypeForTag(string tag)
        {
            lock (Lock)
            {
                if (TypeOf.TryGetValue(tag, out var t)) return t;
                foreach (var c in SimCommandTypes)
                    if (!TagOf.ContainsKey(c)) Register(c, c.Name);
                return TypeOf.TryGetValue(tag, out t) ? t : null;
            }
        }

        static void Register(Type t, string tag)
        {
            if (TypeOf.TryGetValue(tag, out var other) && other != t) throw new InvalidOperationException($"command tag {tag} used by {other} and {t}");
            TagOf[t] = tag;
            TypeOf[tag] = t;
        }

        /// <summary>One line, no trailing newline.</summary>
        public static string Encode(Command c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            var body = JObject.FromObject(c, Serializer);
            body.Remove("issuedAtTick");
            var line = new JObject
            {
                ["tick"] = c.IssuedAtTick,
                ["type"] = TagFor(c.GetType()),
            };
            foreach (var p in body.Properties()) line.Add(p.Name, p.Value);
            return line.ToString(Formatting.None);
        }

        /// <summary>Parses one line. Throws <see cref="FormatException"/> for an unknown type tag or malformed JSON.</summary>
        public static Command Decode(string line)
        {
            JObject o;
            try { o = JObject.Parse(line); }
            catch (JsonException e) { throw new FormatException("command log line is not JSON: " + e.Message, e); }
            string tag = (string)o["type"] ?? throw new FormatException("command log line has no type");
            var t = TypeForTag(tag) ?? throw new FormatException("unknown command type tag " + tag);
            long tick = (long?)o["tick"] ?? throw new FormatException("command log line has no tick");
            o.Remove("type");
            o.Remove("tick");
            o["issuedAtTick"] = tick;
            try { return (Command)o.ToObject(t, Serializer); }
            catch (JsonException e) { throw new FormatException($"bad {tag} line: {e.Message}", e); }
        }
    }
}
