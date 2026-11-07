using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HeroCat.Server.Observability;

/// <summary>Where finished log lines go: stdout in a container (the log driver ships it), a list in tests.</summary>
public interface ILogSink
{
    void WriteLine(string line);
}

/// <summary>One JSON object per line on stdout.</summary>
public sealed class StdoutLogSink : ILogSink
{
    static readonly object Gate = new();

    public void WriteLine(string line)
    {
        lock (Gate) Console.Out.WriteLine(line);
    }
}

/// <summary>
/// The structured JSON log of specs/08 section 9.4. Each line is one object with <c>ts, level, category, msg</c>, the
/// request fields <c>route, status, latencyMs, playerIdHash, deviceId, requestId, code, traceId</c> when known (from
/// the call's arguments or the request scope <see cref="RequestTelemetryMiddleware"/> opens), any other arguments under
/// <c>props</c>, and <c>exception</c>. Every value and the message go through <see cref="LogRedactor"/>; the message is
/// rendered from its template with the redacted values, never from the caller's formatted string.
/// </summary>
[ProviderAlias("HeroCatJson")]
public sealed partial class StructuredLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    readonly ILogSink _sink;
    IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public StructuredLoggerProvider(ILogSink sink) => _sink = sink;

    public ILogger CreateLogger(string categoryName) => new StructuredLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose() { }

    /// <summary>The top-level request fields, in output order.</summary>
    public static readonly string[] RequestFields =
        { "route", "status", "latencyMs", "playerIdHash", "deviceId", "requestId", "code", "traceId" };

    static readonly Dictionary<string, string> FieldByName =
        RequestFields.ToDictionary(f => f, f => f, StringComparer.OrdinalIgnoreCase);

    const string OriginalFormat = "{OriginalFormat}";

    // Log lines are read by people and the log store, never put into HTML: keep quotes and '+' readable.
    static readonly JsonWriterOptions WriterOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    sealed class StructuredLogger : ILogger
    {
        readonly string _category;
        readonly StructuredLoggerProvider _owner;

        public StructuredLogger(string category, StructuredLoggerProvider owner)
        {
            _category = category;
            _owner = owner;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _owner._sink.WriteLine(Format(_category, logLevel, DateTimeOffset.UtcNow, state, exception, formatter, _owner._scopes));
        }
    }

    /// <summary>Builds one line. Public for tests.</summary>
    public static string Format<TState>(string category, LogLevel level, DateTimeOffset ts, TState state, Exception? exception,
                                        Func<TState, Exception?, string> formatter, IExternalScopeProvider? scopes)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var props = new Dictionary<string, object?>(StringComparer.Ordinal);

        // The request scope first, then the call's own arguments over it.
        scopes?.ForEachScope((scope, _) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> kvs)
                foreach (var kv in kvs) Put(kv.Key, kv.Value, fields, props, fromScope: true);
        }, (object?)null);

        string message;
        if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
        {
            string? template = null;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in list)
            {
                if (kv.Key == OriginalFormat) { template = kv.Value as string; continue; }
                object? safe = LogRedactor.Value(kv.Key, kv.Value);
                values[kv.Key] = safe;
                Put(kv.Key, safe, fields, props, fromScope: false);
            }
            message = template != null ? Render(template, values) : LogRedactor.Scrub(formatter(state, null));
        }
        else
        {
            message = LogRedactor.Scrub(formatter(state, null));
        }

        if (!fields.ContainsKey("traceId") && Activity.Current is { } a) fields["traceId"] = a.TraceId.ToHexString();

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("ts", ts.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            w.WriteString("level", LevelName(level));
            w.WriteString("category", category);
            w.WriteString("msg", message);
            foreach (string f in RequestFields)
                if (fields.TryGetValue(f, out var v) && v != null) WriteValue(w, f, v);
            if (props.Count > 0)
            {
                w.WriteStartObject("props");
                foreach (var (k, v) in props) WriteValue(w, k, v);
                w.WriteEndObject();
            }
            if (exception != null) w.WriteString("exception", LogRedactor.Scrub(exception.ToString()));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    static void Put(string key, object? value, Dictionary<string, object?> fields, Dictionary<string, object?> props, bool fromScope)
    {
        if (key == OriginalFormat) return;
        object? safe = fromScope ? LogRedactor.Value(key, value) : value;
        if (FieldByName.TryGetValue(key, out var field)) fields[field] = safe;
        else if (!fromScope) props[char.ToLowerInvariant(key[0]) + key[1..]] = safe;
    }

    [GeneratedRegex(@"\{\{|\}\}|\{[@$]?([A-Za-z0-9_.]+)(,[^}:]*)?(:[^}]*)?\}")]
    private static partial Regex Hole();

    /// <summary>The template with each hole replaced by its (already redacted) value; the result is scrubbed too.</summary>
    static string Render(string template, Dictionary<string, object?> values)
    {
        string s = Hole().Replace(template, m =>
        {
            if (m.Value == "{{") return "{";
            if (m.Value == "}}") return "}";
            if (!values.TryGetValue(m.Groups[1].Value, out var v)) return m.Value;
            string? format = m.Groups[3].Success ? m.Groups[3].Value[1..] : null;
            return v switch
            {
                null => "(null)",
                IFormattable f => f.ToString(format, CultureInfo.InvariantCulture),
                _ => v.ToString() ?? "",
            };
        });
        return LogRedactor.Scrub(s);
    }

    static void WriteValue(Utf8JsonWriter w, string name, object? v)
    {
        switch (v)
        {
            case null: w.WriteNull(name); break;
            case bool b: w.WriteBoolean(name, b); break;
            case int i: w.WriteNumber(name, i); break;
            case long l: w.WriteNumber(name, l); break;
            case short sh: w.WriteNumber(name, sh); break;
            case uint ui: w.WriteNumber(name, ui); break;
            case ulong ul: w.WriteNumber(name, ul); break;
            case double d when double.IsFinite(d): w.WriteNumber(name, Math.Round(d, 3)); break;
            case float f when float.IsFinite(f): w.WriteNumber(name, f); break;
            case decimal m: w.WriteNumber(name, m); break;
            case DateTimeOffset dto: w.WriteString(name, dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)); break;
            case DateTime dt: w.WriteString(name, dt.ToString("O", CultureInfo.InvariantCulture)); break;
            case IFormattable fm: w.WriteString(name, fm.ToString(null, CultureInfo.InvariantCulture)); break;
            default: w.WriteString(name, v.ToString()); break;
        }
    }

    static string LevelName(LogLevel l) => l switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        LogLevel.Critical => "critical",
        _ => "none",
    };
}
