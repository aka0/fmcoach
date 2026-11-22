// FM Coach v0.7 - data layer part 1: request engine + reference introspection.
// READ-ONLY: BindingUtils.Request / Bindings reads only (the mechanism FM's own widgets use to display data). Nothing is set.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using SI.Bindable;
using SI.Bindable.Reference.Core;
using SI.Core;

public partial class CoachBehaviour
{
    internal static bool ProbeEnabled = true, DataEnabled = true, SlotProviderProbes;
    internal static float RefreshMinutes = 15f;

    // v0.7.1: a request is only complete when FM calls back. v0.7 also read the node value right after Request(),
    // which (when the data was not cached yet) is still the *source* object, e.g. "team.Name = FM.UI.TeamReference";
    // that produced the garbage snapshots in the 20261119 runs. Now: callback only, with validation, queued + rate-limited.
    sealed class Req
    {
        public string Path, Owner, Str, Err, SrcClass, Expect; public TypedValue Tv, Src; public bool Done, KeepTv, Got, Issued, Empty;
        public Dictionary<string, TypedValue> Context; public Bindings.Key Scope;
        public float Deadline, Timeout; public Bindings.Key Key; public Bindings.ValueChangedCallback Cb; public int Rejected;
    }
    readonly List<Req> reqLive = new();
    readonly List<Req> reqToUnbind = new();
    readonly Queue<Req> reqQueue = new();
    Bindings dataB;
    int reqIssued, reqResolved, reqTimedOut, reqFailed, reqEmpty, reqSkipped, reqRejected;
    const int MaxIssuePerFrame = 120, MaxLive = 700;

    Req Ask(TypedValue source, string path, float timeout = 6f, bool keepTv = false, string owner = null, Dictionary<string, TypedValue> context = null, Bindings.Key scope = default, bool forceFresh = false)
    {
        var r = new Req { Path = path, Owner = owner, KeepTv = keepTv, Timeout = timeout, Src = source, Context = context, Scope = scope };
        if (source == null || source.IsNull || ClassOf(source) == "SI.Bindable.NullReference" || dataB == null) { r.Done = true; r.Err = "no source"; return r; }
        r.SrcClass = ClassOf(source);
        // Skip names the source class does not have (no binding, no timeout): FM's own property catalogue decides.
        var first = path.Split('.')[0];
        var props = PropsOf(r.SrcClass);
        if (props.Count > 0)
        {
            var pi = props.FirstOrDefault(x => x.Name == first);
            if (pi.Name == null) { r.Done = true; r.Err = "not a property of " + r.SrcClass; reqSkipped++; return r; }
            if (!path.Contains('.')) r.Expect = pi.IsList ? "list" : pi.Kind.StartsWith("Reference") ? "ref" : pi.Kind.StartsWith("Value:System.") ? "prim" : "value";
        }
        // Resolved record values are available directly. An explicit NullReference
        // must not start a nested request (e.g. MatchPlayer.Name outside a match).
        try
        {
            var record = source.Get()?.TryCast<SI.Core.Record>();
            if (record != null)
            {
                var property = PropsOf(source).FirstOrDefault(p => p.Name == first);
                if (property.Name != null && record.TryGetValue(property.Id, out var resolved))
                {
                    if (!path.Contains('.'))
                    {
                        OnReqValue(r, resolved);
                        if (r.Done) { r.Issued = true; return r; }
                    }
                    else if (resolved == null || resolved.IsNull || ClassOf(resolved) == "SI.Bindable.NullReference")
                    {
                        r.Done = r.Empty = true; r.Err = "null reference"; reqEmpty++; return r;
                    }
                }
            }
        }
        catch { }
        if (!forceFresh && scope.m_key != 0 && (context == null || context.Count == 0))
        {
            try
            {
                var key = scope; var pathSpan = Il2CppSystem.MemoryExtensions.AsSpan(path);
                var cached = dataB.Get(ref key, ref pathSpan);
                if (cached != null && !cached.IsNull) OnReqValue(r, cached);
                if (r.Done && r.Got) { r.Issued = true; return r; }
            }
            catch { }
        }
        reqQueue.Enqueue(r);
        return r;
    }

    void Issue(Req r)
    {
        r.Issued = true; r.Deadline = Time.unscaledTime + r.Timeout;
        try
        {
            r.Cb = DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>((Action<Bindings.Key, TypedValue>)((k, v) => OnReqValue(r, v)));
            reqIssued++;
            if (r.Context == null || r.Context.Count == 0)
                r.Key = r.Scope.m_key != 0
                    ? BindingUtils.Request(dataB, r.Scope, r.Src, r.Path, panelRoot, r.Cb)
                    : BindingUtils.Request(dataB, r.Src, r.Path, panelRoot, r.Cb); // may call back synchronously when cached
            else
            {
                var builder = r.Scope.m_key != 0 ? BindingUtils.BuildRequest(dataB, r.Scope, r.Src) : BindingUtils.BuildRequest(dataB, r.Src);
                foreach (var entry in r.Context) builder = builder.AddContext(Il2CppSystem.MemoryExtensions.AsSpan(entry.Key), entry.Value);
                r.Key = builder.BindOneShot(Il2CppSystem.MemoryExtensions.AsSpan(r.Path), panelRoot, r.Cb);
            }
        }
        catch (Exception ex) { r.Done = true; r.Err = ex.GetType().Name; reqFailed++; LogOnce("Request " + r.Path, ex); }
        reqLive.Add(r);
    }

    void OnReqValue(Req r, TypedValue v)
    {
        try
        {
            if (r.Done) return;
            if (v == null || v.IsNull || ClassOf(v) == "SI.Bindable.NullReference") { r.Done = true; r.Empty = true; reqEmpty++; return; }
            var str = Describe(v);
            // still the source object (not resolved yet) -> wait for the next callback
            if (r.SrcClass != null && str == r.SrcClass && r.Expect != "ref") { r.Rejected++; reqRejected++; return; }
            if (r.Expect == "ref" && str == r.SrcClass && ReferenceIdentity(v) == ReferenceIdentity(r.Src))
            { r.Rejected++; reqRejected++; return; } // same source echoed back, not the requested reference
            if (r.Expect == "prim" && str != null && str.StartsWith("FM.UI.") && str.EndsWith("Reference")) { r.Rejected++; reqRejected++; return; }
            r.Str = str;
            if (r.KeepTv) { try { r.Tv = TypedValue.CreateCopyFrom(v); } catch { r.Tv = v; } }
            r.Got = true; r.Done = true; reqResolved++;
        }
        catch { }
    }

    void PumpRequests()
    {
        float now = Time.unscaledTime;
        int issued = 0;
        while (reqQueue.Count > 0 && issued < MaxIssuePerFrame && reqLive.Count < MaxLive && dataB != null) { var q = reqQueue.Dequeue(); if (!q.Done) { Issue(q); issued++; } }
        for (int i = reqLive.Count - 1; i >= 0; i--)
        {
            var r = reqLive[i];
            if (!r.Done && now >= r.Deadline) { r.Done = true; r.Err = r.Rejected > 0 ? "only unresolved source values" : "no callback"; reqTimedOut++; }
            if (r.Done) { reqLive.RemoveAt(i); if (r.Cb != null) reqToUnbind.Add(r); }
        }
        int n = 0;
        while (reqToUnbind.Count > 0 && n++ < 200)
        {
            var r = reqToUnbind[reqToUnbind.Count - 1]; reqToUnbind.RemoveAt(reqToUnbind.Count - 1);
            try { var k = r.Key; BindingUtils.UnbindIfValid(ref k, r.Cb); } catch { }
        }
    }

    string ReqStats() => $"issued={reqIssued} resolved={reqResolved} empty={reqEmpty} skipped={reqSkipped} rejectedSourceEchoes={reqRejected} timedOut={reqTimedOut} failed={reqFailed}";

    static IEnumerator WaitAll(IEnumerable<Req> rs, float maxSeconds)
    {
        // maxSeconds counts from when the last request was actually issued (requests are queued and rate-limited)
        var list = rs.ToList(); float end = Time.unscaledTime + maxSeconds;
        float hardEnd = Time.unscaledTime + CoachCoroutine.BatchWaitSeconds(maxSeconds, list.Count, MaxLive);
        while (list.Any(r => !r.Done))
        {
            if (Time.unscaledTime >= hardEnd)
            {
                foreach (var request in list.Where(r => !r.Done)) { request.Done = true; request.Err = "batch wait budget exceeded"; }
                break;
            }
            if (list.Any(r => !r.Issued && !r.Done)) end = Time.unscaledTime + maxSeconds;
            else if (Time.unscaledTime >= end) break;
            yield return null;
        }
    }

    static List<TypedValue> ListItems(TypedValue tv, int max)
    {
        var res = new List<TypedValue>();
        try
        {
            var o = tv?.Get();
            var list = o?.TryCast<Il2CppSystem.Collections.Generic.List<TypedValue>>();
            if (list == null)
            {   // v0.7.1: other collection shapes (e.g. an IList of TypedValues)
                var il = o?.TryCast<Il2CppSystem.Collections.IList>();
                int cnt = il == null ? 0 : (o.TryCast<Il2CppSystem.Collections.ICollection>()?.Count ?? 0);
                for (int i = 0; i < cnt && res.Count < max; i++)
                {
                    var it = il[i]?.TryCast<TypedValue>(); if (it == null || it.IsNull) continue;
                    try { res.Add(TypedValue.CreateCopyFrom(it)); } catch { res.Add(it); }
                }
                return res;
            }
            for (int i = 0; i < list.Count && res.Count < max; i++)
            {
                var it = list[i]; if (it == null || it.IsNull) continue;
                try { res.Add(TypedValue.CreateCopyFrom(it)); } catch { res.Add(it); }
            }
        }
        catch { }
        return res;
    }

    static int ListCount(TypedValue tv)
    {
        try
        {
            var o = tv?.Get(); var l = o?.TryCast<Il2CppSystem.Collections.Generic.List<TypedValue>>(); if (l != null) return l.Count;
            var ic = o?.TryCast<Il2CppSystem.Collections.ICollection>(); return ic != null && o.TryCast<Il2CppSystem.Collections.IList>() != null ? ic.Count : -1;
        }
        catch { return -1; }
    }

    static string RawType(TypedValue tv)
    {
        try { var o = tv?.Get(); return o == null ? "null" : o.GetIl2CppType().FullName; } catch (Exception ex) { return ex.GetType().Name; }
    }

    static string ClassOf(TypedValue tv)
    {
        try { var o = tv?.Get(); if (o != null) return o.GetIl2CppType().FullName; } catch { }
        try { return tv?.AsString(); } catch { return null; }
    }

    internal struct PropInfo { public string Name; public uint Id; public bool IsList; public string Kind; }
    static readonly Dictionary<string, List<PropInfo>> propCache = new();

    // Property catalogue of a reference class via its generated static GetPropertiesInternal/GetPropertyTypeInternal.
    static List<PropInfo> PropsOf(string className)
    {
        if (string.IsNullOrEmpty(className)) return new List<PropInfo>();
        if (propCache.TryGetValue(className, out var c)) return c;
        var res = new List<PropInfo>();
        try
        {
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(className, false) ?? a.GetType("Il2Cpp" + className, false); } catch { }
                if (t != null) break;
            }
            var gp = t?.GetMethod("GetPropertiesInternal", BindingFlags.Public | BindingFlags.Static);
            var gt = t?.GetMethod("GetPropertyTypeInternal", BindingFlags.Public | BindingFlags.Static);
            if (gp != null)
            {
                var lst = new Il2CppSystem.Collections.Generic.List<PropertyID>();
                gp.Invoke(null, new object[] { lst });
                var set = PropertyIdentifierSet.Instance;
                for (int i = 0; i < lst.Count; i++)
                {
                    uint id = lst[i].id;
                    string nm = null; try { nm = set.GetInfo(id)?.Name; } catch { }
                    var pi = new PropInfo { Name = nm ?? ("#" + id), Id = id, Kind = "?" };
                    if (gt != null)
                    {
                        try
                        {
                            var k = (BindingKind)gt.Invoke(null, new object[] { id });
                            pi.IsList = k.IsList;
                            string tn = null; try { tn = k.Type?.FullName; } catch { }
                            pi.Kind = (k.IsList ? "list<" : "") + k.Kind.ToString() + (tn != null ? ":" + tn : "") + (k.IsList ? ">" : "");
                        }
                        catch { }
                    }
                    res.Add(pi);
                }
            }
        }
        catch (Exception ex) { CoachPlugin.L.LogWarning("PropsOf(" + className + ") failed: " + ex.GetType().Name + " " + ex.Message); }
        propCache[className] = res;
        return res;
    }

    // DynamicReference inherits SI.Core.Record; its instance owns the actual property IDs.
    // Static reference catalogues are empty for these rows, so inspect the record itself.
    static List<PropInfo> PropsOf(TypedValue value)
    {
        var declared = PropsOf(ClassOf(value));
        if (declared.Count > 0) return declared;
        var result = new List<PropInfo>();
        try
        {
            var record = value?.Get()?.TryCast<SI.Core.Record>();
            if (record == null) return result;
            var entries = record.GetEnumerator();
            while (entries.MoveNext())
            {
                var entry = entries.Current;
                var name = PropertyIdentifierSet.Instance.GetInfo(entry.Key)?.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                bool list = ListCount(entry.Value) >= 0;
                result.Add(new PropInfo { Name = name, Id = entry.Key, IsList = list,
                    Kind = list ? "list<record>" : ValueText(Describe(entry.Value)) != null ? "Value" : "Reference" });
            }
        }
        catch (Exception ex) { CoachPlugin.L.LogWarning("Dynamic record inspection: " + ex.GetType().Name); }
        return result;
    }

    static bool IsValueKind(PropInfo p) => !p.IsList && !p.Kind.StartsWith("Reference", StringComparison.OrdinalIgnoreCase);
    static string Trunc(string s, int n) => s == null ? null : (s.Length > n ? s.Substring(0, n) + "…" : s).Replace("\n", "\\n");
}
