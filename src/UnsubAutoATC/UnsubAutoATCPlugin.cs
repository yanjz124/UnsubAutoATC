using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using RossCarlson.Vatsim.Vpilot.Plugins;

namespace UnsubAutoATC
{
    /// <summary>
    /// Mutes or hides private messages from the callsigns listed in UnsubAutoATC.ini (AUTO_ATC by
    /// default). Each message is shown normally ("show"), shown without the notification sound
    /// ("mute"), or dropped ("hide"), based on MuteAll and a keyword whitelist.
    ///
    /// The official plugin API only lets plugins observe events, not cancel them. vPilot routes
    /// everything through an internal event broker (bbv.Common.EventBroker), so this plugin wraps
    /// the delegates behind two broker topics:
    ///   - "PrivateMessageReceived": dropping a message here means vPilot never shows it, plays a
    ///     sound, flashes the taskbar, or passes it on to other plugins.
    ///   - "PlaySoundRequested": used to swallow the PM sounds right after a muted message.
    ///
    /// vPilot itself is obfuscated, so nothing here relies on its internal names. Publishers are
    /// looked up through the (unobfuscated) event broker, with a fallback that scans for
    /// [EventPublication] attributes, and backing fields are found by probing which field changes
    /// when a handler is added.
    /// </summary>
    public class UnsubAutoATCPlugin : IPlugin
    {
        private const string MessageTopic = "PrivateMessageReceived";
        private const string SoundTopic = "PlaySoundRequested";
        private static readonly string[] MutedSounds = { "PrivateMessage", "NewMessage" };
        private static readonly TimeSpan MuteWindow = TimeSpan.FromSeconds(3);
        private const int MaxSearchDepth = 3;
        private const int RetryIntervalMs = 2000;
        private const int MaxRetries = 150;

        private enum Action { Show, Mute, Hide }

        private readonly object sync = new object();
        private readonly HashSet<Delegate> wrappers = new HashSet<Delegate>();

        private IBroker broker;
        private Timer retryTimer;
        private int retries;
        private bool messageHookInstalled;
        private bool soundHookInstalled;
        private string lastStatus;

        // Config (see UnsubAutoATC.ini for what each one means).
        private bool enabled = true;
        private HashSet<string> senders = DefaultSenders();
        private bool muteAll;
        private Regex whitelist = KeywordRegex("squawk");
        private Action defaultAction = Action.Hide;
        private Action whitelistAction = Action.Show;
        private bool logSuppressed = true;
        private string iniPath;
        private string logPath;
        private DateTime iniWriteTime;

        private DateTime muteSoundsUntil = DateTime.MinValue;

        public string Name => "UnsubAutoATC";

        public void Initialize(IBroker broker)
        {
            this.broker = broker;
            LoadConfig();

            // Publishers can register late; catch any that appeared since startup.
            broker.NetworkConnected += (s, e) => TryInstall();

            if (!TryInstall())
            {
                retryTimer = new Timer(_ => RetryInstall(), null, RetryIntervalMs, RetryIntervalMs);
            }
        }

        private void RetryInstall()
        {
            if (TryInstall() || ++retries >= MaxRetries)
            {
                retryTimer?.Dispose();
                retryTimer = null;
            }
        }

        // ---- Filters -----------------------------------------------------------------------
        // Both are called from compiled wrappers on whatever thread vPilot raises the event on.
        // They must never throw; on any doubt, let the event through.

        private bool ShouldDeliverMessage(object args)
        {
            try
            {
                ReloadConfigIfChanged();
                if (!enabled || args == null)
                {
                    return true;
                }
                string from = ReadProperty(args, "From") as string;
                string data = ReadProperty(args, "Data") as string ?? "";
                if (from == null || !senders.Contains(from.Trim()))
                {
                    return true;
                }

                Action action;
                string reason;
                lock (sync)
                {
                    if (muteAll)
                    {
                        action = Action.Mute;
                        reason = "muteall";
                    }
                    else if (whitelist != null && whitelist.IsMatch(data))
                    {
                        action = whitelistAction;
                        reason = "whitelist";
                    }
                    else
                    {
                        action = defaultAction;
                        reason = "default";
                    }
                    if (action == Action.Mute)
                    {
                        muteSoundsUntil = DateTime.UtcNow + MuteWindow;
                    }
                }
                Log($"{action.ToString().ToUpperInvariant(),-5} ({reason,-9})  {from}: {data}");
                return action != Action.Hide;
            }
            catch
            {
                return true;
            }
        }

        private bool ShouldPlaySound(object args)
        {
            try
            {
                if (DateTime.UtcNow >= muteSoundsUntil)
                {
                    return true;
                }
                string sound = ReadProperty(args, "SoundEvent")?.ToString();
                return !MutedSounds.Contains(sound);
            }
            catch
            {
                return true;
            }
        }

        private static object ReadProperty(object obj, string name)
        {
            return obj.GetType().GetProperty(name)?.GetValue(obj);
        }

        // ---- Hook installation -------------------------------------------------------------

        private bool TryInstall()
        {
            try
            {
                int messages = InstallTopic(MessageTopic, nameof(ShouldDeliverMessage));
                int sounds = InstallTopic(SoundTopic, nameof(ShouldPlaySound));
                string status = $"{Name}: hooked {messages} message publisher(s), {sounds} sound publisher(s).";
                lock (sync)
                {
                    messageHookInstalled |= messages > 0;
                    soundHookInstalled |= sounds > 0;
                    if (status == lastStatus)
                    {
                        return messageHookInstalled && soundHookInstalled;
                    }
                    lastStatus = status;
                }
                broker.PostDebugMessage(status);
                Log(status);
                return messageHookInstalled && soundHookInstalled;
            }
            catch (Exception ex)
            {
                broker.PostDebugMessage($"{Name}: failed to install hooks: {ex.Message}");
                return false;
            }
        }

        private int InstallTopic(string topic, string predicateName)
        {
            MethodInfo predicate = typeof(UnsubAutoATCPlugin).GetMethod(predicateName, BindingFlags.Instance | BindingFlags.NonPublic);
            int installed = 0;
            foreach (var (publisher, ev) in FindPublishers(topic))
            {
                if (InstallWrapper(publisher, ev, predicate))
                {
                    installed++;
                }
            }
            return installed;
        }

        private bool InstallWrapper(object publisher, EventInfo ev, MethodInfo predicate)
        {
            FieldInfo field = FindBackingField(publisher, ev);
            if (field == null)
            {
                return false;
            }
            lock (sync)
            {
                var current = field.GetValue(publisher) as Delegate;
                if (current == null)
                {
                    // No subscribers wired up yet; try again later.
                    return false;
                }
                if (wrappers.Contains(current.GetInvocationList()[0]))
                {
                    return true;
                }
                Delegate wrapper = BuildWrapper(field.FieldType, current, predicate);
                wrappers.Add(wrapper);
                field.SetValue(publisher, wrapper);
                return true;
            }
        }

        // (sender, e) => { if (this.predicate(e)) inner(sender, e); }
        private Delegate BuildWrapper(Type delegateType, Delegate inner, MethodInfo predicate)
        {
            ParameterExpression[] parameters = Parameters(delegateType);
            Expression body = Expression.IfThen(
                Expression.Call(
                    Expression.Constant(this),
                    predicate,
                    Expression.Convert(parameters[parameters.Length - 1], typeof(object))),
                Expression.Invoke(Expression.Constant(inner), parameters));
            return Expression.Lambda(delegateType, body, parameters).Compile();
        }

        private static Delegate BuildNoop(Type delegateType)
        {
            ParameterExpression[] parameters = Parameters(delegateType);
            return Expression.Lambda(delegateType, Expression.Empty(), parameters).Compile();
        }

        private static ParameterExpression[] Parameters(Type delegateType)
        {
            return delegateType.GetMethod("Invoke").GetParameters()
                .Select(p => Expression.Parameter(p.ParameterType, p.Name))
                .ToArray();
        }

        // Finds which field backs the event by adding a no-op handler and seeing which field changes.
        private static FieldInfo FindBackingField(object publisher, EventInfo ev)
        {
            List<FieldInfo> candidates = AllInstanceFields(publisher.GetType())
                .Where(f => f.FieldType == ev.EventHandlerType)
                .ToList();
            if (candidates.Count <= 1)
            {
                return candidates.FirstOrDefault();
            }
            object[] before = candidates.Select(f => f.GetValue(publisher)).ToArray();
            Delegate probe = BuildNoop(ev.EventHandlerType);
            ev.AddEventHandler(publisher, probe);
            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!ReferenceEquals(before[i], candidates[i].GetValue(publisher)))
                    {
                        return candidates[i];
                    }
                }
                return null;
            }
            finally
            {
                ev.RemoveEventHandler(publisher, probe);
            }
        }

        // ---- Publisher discovery -----------------------------------------------------------

        private List<(object, EventInfo)> FindPublishers(string topic)
        {
            var found = new List<(object, EventInfo)>();
            var seen = new HashSet<object>(ReferenceComparer.Instance);
            foreach (var (publisher, ev) in FindPublishersViaEventBroker(topic).Concat(FindPublishersByScan(topic)))
            {
                if (seen.Add(publisher))
                {
                    found.Add((publisher, ev));
                }
            }
            return found;
        }

        // Asks vPilot's event broker which objects publish the topic. bbv.Common.EventBroker is
        // not obfuscated, so its member names are stable.
        private IEnumerable<(object, EventInfo)> FindPublishersViaEventBroker(string topic)
        {
            var results = new List<(object, EventInfo)>();
            try
            {
                object eventBroker = AllInstanceFields(broker.GetType())
                    .Select(f => f.GetValue(broker))
                    .FirstOrDefault(v => v != null && v.GetType().GetInterfaces().Any(i => i.Name == "IEventBroker"));
                object topicHost = eventBroker?.GetType()
                    .GetField("eventTopicHost", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(eventBroker);
                object eventTopic = topicHost?.GetType().GetMethod("GetEventTopic")?.Invoke(topicHost, new object[] { topic });
                var publications = eventTopic?.GetType()
                    .GetField("publications", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(eventTopic) as IEnumerable;
                if (publications == null)
                {
                    return results;
                }
                foreach (object publication in publications)
                {
                    object publisher = ReadProperty(publication, "Publisher");
                    string eventName = ReadProperty(publication, "EventName") as string;
                    EventInfo ev = publisher == null || eventName == null ? null : publisher.GetType()
                        .GetEvent(eventName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (ev != null)
                    {
                        results.Add((publisher, ev));
                    }
                }
            }
            catch
            {
            }
            return results;
        }

        // Fallback: walks the object graph reachable from the plugin broker (vPilot types only)
        // looking for objects that publish the topic.
        private IEnumerable<(object, EventInfo)> FindPublishersByScan(string topic)
        {
            Assembly host = broker.GetType().Assembly;
            var visited = new HashSet<object>(ReferenceComparer.Instance) { broker };
            var queue = new Queue<(object obj, int depth)>();
            queue.Enqueue((broker, 0));

            while (queue.Count > 0)
            {
                var (obj, depth) = queue.Dequeue();
                Type type = obj.GetType();

                EventInfo ev = type.GetEvents(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(e => IsPublicationOf(e, topic));
                if (ev != null)
                {
                    yield return (obj, ev);
                }

                if (depth >= MaxSearchDepth)
                {
                    continue;
                }
                foreach (FieldInfo f in AllInstanceFields(type))
                {
                    if (f.FieldType.IsValueType || f.FieldType == typeof(string) || typeof(Delegate).IsAssignableFrom(f.FieldType))
                    {
                        continue;
                    }
                    object child;
                    try
                    {
                        child = f.GetValue(obj);
                    }
                    catch
                    {
                        continue;
                    }
                    if (child != null && child.GetType().Assembly == host && visited.Add(child))
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
            }
        }

        private static bool IsPublicationOf(EventInfo ev, string topic)
        {
            return ev.GetCustomAttributesData().Any(a =>
                a.AttributeType.Name == "EventPublicationAttribute" &&
                a.ConstructorArguments.Count > 0 &&
                topic.Equals(a.ConstructorArguments[0].Value as string));
        }

        private static IEnumerable<FieldInfo> AllInstanceFields(Type type)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    yield return f;
                }
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        // ---- Config & logging --------------------------------------------------------------

        // Picks up edits to the .ini without restarting vPilot.
        private void ReloadConfigIfChanged()
        {
            try
            {
                if (iniPath != null && File.Exists(iniPath) && File.GetLastWriteTimeUtc(iniPath) != iniWriteTime)
                {
                    LoadConfig();
                }
            }
            catch
            {
            }
        }

        private void LoadConfig()
        {
            string dir = Path.GetDirectoryName(typeof(UnsubAutoATCPlugin).Assembly.Location);
            iniPath = Path.Combine(dir, "UnsubAutoATC.ini");
            logPath = Path.Combine(dir, "UnsubAutoATC.log");
            if (!File.Exists(iniPath))
            {
                return;
            }
            try
            {
                iniWriteTime = File.GetLastWriteTimeUtc(iniPath);

                // Keys missing from the file fall back to their defaults.
                bool newEnabled = true;
                HashSet<string> newSenders = DefaultSenders();
                bool newMuteAll = false;
                Regex newWhitelist = KeywordRegex("squawk");
                Action newDefaultAction = Action.Hide;
                Action newWhitelistAction = Action.Show;
                bool newLogSuppressed = true;

                foreach (string raw in File.ReadAllLines(iniPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("["))
                    {
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq < 0)
                    {
                        continue;
                    }
                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    switch (key.ToLowerInvariant())
                    {
                        case "enabled":
                            newEnabled = ParseBool(value, true);
                            break;
                        case "senders":
                            newSenders = new HashSet<string>(SplitList(value), StringComparer.OrdinalIgnoreCase);
                            break;
                        case "muteall":
                            newMuteAll = ParseBool(value, false);
                            break;
                        case "mode":
                            newDefaultAction = ParseAction(value, Action.Hide);
                            break;
                        case "whitelist":
                            newWhitelist = KeywordRegex(value);
                            break;
                        case "whitelistmode":
                            newWhitelistAction = ParseAction(value, Action.Show);
                            break;
                        case "log":
                        case "logsuppressed":
                            newLogSuppressed = ParseBool(value, true);
                            break;
                    }
                }

                lock (sync)
                {
                    enabled = newEnabled;
                    senders = newSenders;
                    muteAll = newMuteAll;
                    whitelist = newWhitelist;
                    defaultAction = newDefaultAction;
                    whitelistAction = newWhitelistAction;
                    logSuppressed = newLogSuppressed;
                }
            }
            catch (Exception ex)
            {
                broker?.PostDebugMessage($"{Name}: error reading {iniPath}: {ex.Message}");
            }
        }

        private static HashSet<string> DefaultSenders()
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AUTO_ATC" };
        }

        private static IEnumerable<string> SplitList(string value)
        {
            return value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);
        }

        // "squawk, cleared to" -> whole-word, case-insensitive match on any of the keywords.
        // Returns null for an empty list.
        private static Regex KeywordRegex(string list)
        {
            string[] words = SplitList(list).ToArray();
            if (words.Length == 0)
            {
                return null;
            }
            string alternatives = string.Join("|", words.Select(w => Regex.Escape(w).Replace(@"\ ", @"\s+")));
            return new Regex($@"(?<!\w)(?:{alternatives})(?!\w)", RegexOptions.IgnoreCase);
        }

        private static Action ParseAction(string value, Action fallback)
        {
            return Enum.TryParse(value, true, out Action action) ? action : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            switch (value.ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
                default: return fallback;
            }
        }

        private void Log(string line)
        {
            if (!logSuppressed)
            {
                return;
            }
            try
            {
                lock (sync)
                {
                    File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
                }
            }
            catch
            {
            }
        }
    }
}
